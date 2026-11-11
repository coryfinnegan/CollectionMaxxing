# The shelf reader (ADR-033, #795): the compose service, the config guard's rules for it, and the deployer switching it
# on by itself. Uses Invoke-TestDeploy-style passes with the fake machine (TestKit.ps1) and Get-RealComposeConfig from
# ops.tests.ps1, so this file must sort after both. Nothing here pulls an image, downloads a file or starts a container:
# docker, nvidia-smi, the background download and the free-space reading are all fakes.

if (-not (Get-Command Get-RealComposeConfig -ErrorAction SilentlyContinue)) { throw 'shelf-reader.tests.ps1 must load after ops.tests.ps1 (it needs Get-RealComposeConfig)' }

$script:ReaderUrl = 'http://shelf-reader:8431'
$script:ReaderModel = 'Qwen3VL-8B-Instruct-Q4_K_M.gguf'
$script:ReaderProjector = 'mmproj-Qwen3VL-8B-Instruct-F16.gguf'
$script:ReaderContent = @{ $script:ReaderModel = 'fake model weights for the tests'; $script:ReaderProjector = 'fake projector for the tests' }

function Get-TestSha256 { param([string]$Text) ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::ASCII.GetBytes($Text)))).ToLowerInvariant() }

function Use-TestReaderPins {
    <# Replaces the two pinned 5 GB files with tiny ones whose size and SHA-256 the tests know. #>
    $global:CmShelfReaderFiles = @(foreach ($name in $script:ReaderModel, $script:ReaderProjector) {
            $text = $script:ReaderContent[$name]
            @{ Name = $name; Size = [long]$text.Length; Sha256 = (Get-TestSha256 $text) }
        })
}

function Add-TestReaderFile {
    <# Writes one model file (or its .partial download) into <root>/models; -Wrong writes content of the right size but the wrong hash. #>
    param([string]$Root, [string]$Name, [switch]$Partial, [switch]$Wrong, [DateTime]$WrittenAt = [DateTime]::UtcNow.AddHours(-1))
    $dir = Join-Path $Root 'models'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $text = $script:ReaderContent[$Name]
    if ($Wrong) { $text = 'X' * $text.Length }
    $path = Join-Path $dir $(if ($Partial) { "$Name.partial" } else { $Name })
    [IO.File]::WriteAllBytes($path, [Text.Encoding]::ASCII.GetBytes($text))
    [IO.File]::SetLastWriteTimeUtc($path, $WrittenAt)
    $path
}

function Get-TestReaderEnv {
    param([string[]]$Extra = @())
    @('POSTGRES_PASSWORD=test-only-password', 'CM_BACKUP_DIR=/tmp/cm-test-backups', 'CM_API_PORT=5480', 'CM_DASHBOARD_PORT=18888',
        'CM_HEARTBEAT_URL=https://hc.example/test-uuid', 'CM_SECRETS_DIR=/tmp/cm-test-secrets',
        'CM_PUBLIC_ORIGIN=https://pc.tailnet.ts.net', 'CM_AUTH0_DOMAIN=test.us.auth0.com', 'CM_AUTH0_WEB_CLIENT_ID=test-web', 'CM_AUTH0_WEB_CLIENT_SECRET=test-secret',
        'CM_AUTH0_MANAGEMENT_CLIENT_ID=test-mgmt', 'CM_AUTH0_MANAGEMENT_CLIENT_SECRET=test-secret') + $Extra
}

function Start-TestReaderMachine {
    <#
      Installs the fake machine and the fake background download for a series of passes on one root. The download starter
      records each start; with -DownloadFinishes it writes the whole file to the .partial name, the way curl would by the
      next pass (-DownloadWrong: the right size with the wrong bytes).
    #>
    param([hashtable]$Machine = @{}, [switch]$DownloadFinishes, [switch]$DownloadWrong)
    Use-TestReaderPins
    $global:CmDetachedStarts = [System.Collections.Generic.List[object]]::new()
    $finishes = [bool]$DownloadFinishes; $wrong = [bool]$DownloadWrong; $content = $script:ReaderContent
    $global:CmStartDetached = {
        param($File, $ArgumentList)
        $global:CmDetachedStarts.Add([pscustomobject]@{ File = $File; Arguments = @($ArgumentList); Line = (@($ArgumentList) -join ' ') })
        if ($finishes) {
            $out = @($ArgumentList)[[array]::IndexOf(@($ArgumentList), '-o') + 1]
            $name = (Split-Path -Leaf $out) -replace '\.partial$', ''
            $text = $content[$name]
            if ($wrong) { $text = 'X' * $text.Length }
            [IO.File]::WriteAllBytes($out, [Text.Encoding]::ASCII.GetBytes($text))
        }
        [pscustomobject]@{ Id = 4242 }
    }.GetNewClosure()
    $global:CmFreeBytes = { param($Path) [long]500GB }
    New-FakeMachine $Machine | Out-Null
}

function Invoke-TestReaderPass {
    <# One real deployer pass on an existing root, keeping the machine (and so the reader's container) from the passes before. #>
    param([string]$Root)
    Initialize-Cm -Apply $true
    $ctx = New-TestContext -Root $Root
    $before = if (Test-Path -LiteralPath $ctx.LogFile) { (Get-Content -LiteralPath $ctx.LogFile -Raw).Length } else { 0 }
    $result = Invoke-CmDeploy -Ctx $ctx 6>$null
    $log = if (Test-Path -LiteralPath $ctx.LogFile) { (Get-Content -LiteralPath $ctx.LogFile -Raw).Substring($before) } else { '' }
    [pscustomobject]@{ Result = $result; State = (Get-TestState -Root $Root); Log = $log }
}

function Use-TestReaderRoot {
    <# A throwaway root with the given state and .env; the body gets it and it is always removed. #>
    param([Parameter(Position = 0)][scriptblock]$Body, [hashtable]$State = $null, [string[]]$EnvText = $null)
    $root = New-TestRoot
    try {
        if ($State) { Set-TestState -Root $root -State $State }
        if ($EnvText) { Set-Content -LiteralPath (Join-Path $root '.env') -Value $EnvText }
        & $Body $root
    }
    finally { Remove-TestRoot $root }
}

function Get-ReaderUps { @(Get-Calls 'docker' '*up -d --no-deps shelf-reader*') }
function Get-ApiSwitches { @(Get-Calls 'docker' '*up -d --no-deps api*') }
function Get-StackUps { @(Get-Calls 'docker' '*up -d api backup aspire-dashboard*') }

# ---- the compose file -----------------------------------------------------------------------------------------------------

Test-Case 'shelf reader compose: the service is behind the gpu profile, so a plain up never starts it' {
    $plain = Get-RealComposeConfig
    Assert-True ($null -eq $plain.services.'shelf-reader') 'without --profile gpu the reader is not part of the stack'
    $gpu = Get-RealComposeConfig -Profiles @('gpu')
    Assert-True ($null -ne $gpu.services.'shelf-reader') 'with --profile gpu it is'
}

Test-Case 'shelf reader compose: pinned by digest, no published port, one NVIDIA GPU, models read-only, listening on 8431' {
    $svc = (Get-RealComposeConfig -Profiles @('gpu')).services.'shelf-reader'
    Assert-Equal 'ghcr.io/ggml-org/llama.cpp:server-cuda-b12282@sha256:bb29527ecb3da442d9459591c252351d957b6e3ee1635e8d0406c0a4442eb08c' $svc.image
    Assert-Equal 0 @($svc.ports | Where-Object { $_ }).Count 'no ports: reachable on the compose network only'
    $devices = @($svc.deploy.resources.reservations.devices)
    Assert-Equal 1 $devices.Count
    Assert-Equal 'nvidia' $devices[0].driver
    Assert-Equal '1' "$($devices[0].count)"
    Assert-Equal 'gpu' (@($devices[0].capabilities) -join ',')
    $models = @($svc.volumes | Where-Object { $_.target -eq '/models' })
    Assert-Equal 1 $models.Count 'the models folder is mounted at /models'
    Assert-True ([bool]$models[0].read_only) 'and read-only'
    $command = @($svc.command) -join ' '
    Assert-Match $command '--port 8431'
    Assert-Match $command '--host 0\.0\.0\.0'
    Assert-Match $command "-m /models/$([regex]::Escape($script:ReaderModel))"
    Assert-Match $command "--mmproj /models/$([regex]::Escape($script:ReaderProjector))"
    Assert-Match $command '--sleep-idle-seconds 300'
    Assert-Match $command '-ngl 99 -c 12288 -np 1 --no-webui --jinja'
    foreach ($port in 8080, 8409, 32400, 8096, 5480) { Assert-False ($command -match "\b$port\b") "the reader never names port $port" }
    Assert-Match (@($svc.healthcheck.test) -join ' ') 'http://127\.0\.0\.1:8431/health'
}

Test-Case 'shelf reader compose: no other service reserves a GPU, and the api never waits for the reader' {
    $config = Get-RealComposeConfig -Profiles @('gpu')
    foreach ($p in $config.services.PSObject.Properties) {
        if ($p.Name -eq 'shelf-reader') { continue }
        Assert-Equal 0 @($p.Value.deploy.resources.reservations.devices | Where-Object { $_ }).Count "$($p.Name) reserves no device"
        if ($p.Value.depends_on) { Assert-False (@($p.Value.depends_on.PSObject.Properties.Name) -contains 'shelf-reader') "$($p.Name) does not depend on the reader" }
    }
}

Test-Case 'shelf reader compose: the api endpoint is empty unless the deployer sets it, and the model is named' {
    $off = Get-RealComposeConfig
    Assert-Equal '' "$($off.services.api.environment.ShelfReader__Endpoint)"
    Assert-Equal $script:ReaderModel "$($off.services.api.environment.ShelfReader__Model)"
    $on = Get-RealComposeConfig -EnvValues @{ CM_SHELF_READER_ENDPOINT = $script:ReaderUrl }
    Assert-Equal $script:ReaderUrl "$($on.services.api.environment.ShelfReader__Endpoint)"
}

Test-Case 'shelf reader guard: the guard accepts the shelf-reader service as written' {
    $config = Get-RealComposeConfig -Profiles @('gpu')
    $problems = @(Test-CmConfig -EnvMap $script:LastRealComposeEnv -ComposeConfig $config -ComposeFile $script:LastRealComposeFile)
    Assert-Equal 0 $problems.Count ($problems -join '; ')
}

Test-Case 'shelf reader guard: the deployer runs the guard with the gpu profile, so the reader is always checked' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState)
    $configs = @(Get-Calls 'docker' '*config --format json*')
    Assert-True ($configs.Count -ge 1)
    foreach ($c in $configs) { Assert-Match $c.Display 'compose --profile gpu ' }
}

# ---- the config guard ------------------------------------------------------------------------------------------------------

function New-GuardReader {
    param([string]$Source = 'C:\CollectionMaxxing\models', [bool]$ReadOnly = $true)
    [pscustomobject]@{
        image   = 'ghcr.io/ggml-org/llama.cpp:server-cuda-b12282@sha256:' + ('b' * 64)
        volumes = @([pscustomobject]@{ type = 'bind'; source = $Source; target = '/models'; read_only = $ReadOnly })
        deploy  = [pscustomobject]@{ resources = [pscustomobject]@{ reservations = [pscustomobject]@{ devices = @([pscustomobject]@{ driver = 'nvidia'; count = 1; capabilities = @('gpu') }) } } }
    }
}
function New-GuardConfigWithReader {
    param($Reader = (New-GuardReader))
    $cfg = New-GoodComposeConfig
    $cfg.services | Add-Member -NotePropertyName 'shelf-reader' -NotePropertyValue $Reader
    $cfg
}

Test-Case 'shelf reader guard: the reader with its read-only models mount and its GPU passes' {
    Assert-Equal 0 (Get-GuardProblems (New-GuardConfigWithReader)).Count ((Get-GuardProblems (New-GuardConfigWithReader)) -join '; ')
    $envMap = New-GuardEnv; $envMap['CM_MODELS_DIR'] = 'D:\Models'
    Assert-Equal 0 (Get-GuardProblems (New-GuardConfigWithReader (New-GuardReader -Source 'D:\Models')) $envMap).Count 'a models folder named in .env'
}

Test-Case 'shelf reader guard: a published port on the reader is refused, even on 127.0.0.1' {
    $reader = New-GuardReader
    $reader | Add-Member -NotePropertyName ports -NotePropertyValue @([pscustomobject]@{ host_ip = '127.0.0.1'; published = '8431'; target = 8431 })
    $problems = @(Get-GuardProblems (New-GuardConfigWithReader $reader))
    Assert-Equal 1 $problems.Count ($problems -join '; ')
    Assert-Match $problems[0] "service 'shelf-reader' must not publish a port"
}

Test-Case 'shelf reader guard: a writable models mount is refused, on the reader or any other service' {
    $problems = @(Get-GuardProblems (New-GuardConfigWithReader (New-GuardReader -ReadOnly $false)))
    Assert-Equal 1 $problems.Count ($problems -join '; ')
    Assert-Match $problems[0] "service 'shelf-reader'"
    Assert-Match $problems[0] 'read-only'
    Assert-Match $problems[0] '/models'
    $cfg = New-GuardConfigWithReader; Add-GuardVolume $cfg -Source 'C:\CollectionMaxxing\models\sub' -Target '/m'
    $problems = @(Get-GuardProblems $cfg)
    Assert-Equal 1 $problems.Count ($problems -join '; ')
    Assert-Match $problems[0] "service 'api'"
}

Test-Case 'shelf reader guard: a models mount outside the allowed folder is refused' {
    foreach ($source in 'C:\Users\someone\models', 'C:\CollectionMaxxing', 'C:\CollectionMaxxing\models-old', 'C:\CollectionMaxxing\models\..\state') {
        $problems = @(Get-GuardProblems (New-GuardConfigWithReader (New-GuardReader -Source $source)))
        Assert-Equal 1 $problems.Count "[$source]: $($problems -join '; ')"
        Assert-Match $problems[0] "service 'shelf-reader'"
    }
}

Test-Case 'shelf reader guard: a GPU reservation on any other service is refused, and so is any other device reservation' {
    $cfg = New-GuardConfigWithReader
    $cfg.services.api | Add-Member -NotePropertyName deploy -NotePropertyValue (New-GuardReader).deploy
    $problems = @(Get-GuardProblems $cfg)
    Assert-Equal 1 $problems.Count ($problems -join '; ')
    Assert-Match $problems[0] "service 'api' reserves a device"
    $cfg = New-GuardConfigWithReader
    $cfg.services.api | Add-Member -NotePropertyName gpus -NotePropertyValue @([pscustomobject]@{ driver = 'nvidia'; count = -1 })
    Assert-Match ((Get-GuardProblems $cfg) -join ';') "service 'api'.*GPU"
    $reader = New-GuardReader; $reader.deploy.resources.reservations.devices[0].driver = 'cdi'
    Assert-Match ((Get-GuardProblems (New-GuardConfigWithReader $reader)) -join ';') "service 'shelf-reader'.*nvidia"
    $reader = New-GuardReader; $reader.deploy.resources.reservations.devices[0].capabilities = @('gpu', 'compute', 'utility', 'display')
    Assert-Match ((Get-GuardProblems (New-GuardConfigWithReader $reader)) -join ';') "service 'shelf-reader'"
}

Test-Case 'shelf reader guard: .env may not set the endpoint, which only the deployer passes' {
    $envMap = New-GuardEnv; $envMap['CM_SHELF_READER_ENDPOINT'] = 'http://elsewhere:1234'
    $problems = @(Get-GuardProblems (New-GoodComposeConfig) $envMap)
    Assert-Equal 1 $problems.Count ($problems -join '; ')
    Assert-Match $problems[0] 'CM_SHELF_READER_ENDPOINT'
}

# ---- the deployer: switched off ----------------------------------------------------------------------------------------------

function Assert-ReaderUntouched {
    param($Pass, [string]$Why)
    Assert-Equal 0 (Get-ReaderUps).Count "no reader start ($Why)"
    Assert-Equal 0 (Get-Calls 'docker' 'docker pull*llama.cpp*').Count "no reader image pull ($Why)"
    Assert-Equal 0 (Get-Calls 'docker' '*shelf-reader*').Count "no reader call at all ($Why)"
    Assert-Equal 0 $global:CmDetachedStarts.Count "no download ($Why)"
    foreach ($up in @(Get-StackUps) + @(Get-ApiSwitches)) { Assert-Equal '' "$($up.Env['CM_SHELF_READER_ENDPOINT'])" "the api endpoint is empty ($Why)" }
}

Test-Case 'shelf reader: with CM_SHELF_READER=0 nothing is pulled, downloaded or started and the api endpoint is empty' {
    Use-TestReaderRoot -State (Get-OldGoodState) -EnvText (Get-TestReaderEnv @('CM_SHELF_READER=0')) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true }
        $r = Invoke-TestReaderPass $root
        Assert-Equal 'deployed' $r.Result.Outcome
        Assert-Equal 0 $r.Result.ExitCode
        Assert-Equal 1 (Get-StackUps).Count
        Assert-Equal 0 (Get-Calls 'nvidia-smi').Count 'the off switch is read before anything is asked of the card'
        Assert-ReaderUntouched $r 'switched off'
        Assert-Match $r.Log 'shelf reader: off: CM_SHELF_READER=0'
        Assert-False (Test-Path -LiteralPath (Join-Path $root 'models')) 'no models folder is made'
    }
}

Test-Case 'shelf reader: a host with no NVIDIA card starts nothing, downloads nothing and leaves the endpoint empty' {
    foreach ($gpu in $false, 'empty') {
        $global:CmCalls.Clear()
        Use-TestReaderRoot -State (Get-OldGoodState) {
            param($root)
            Start-TestReaderMachine @{ Gpu = $gpu }
            $r = Invoke-TestReaderPass $root
            Assert-Equal 'deployed' $r.Result.Outcome
            Assert-Equal 1 (Get-Calls 'nvidia-smi').Count
            Assert-ReaderUntouched $r "gpu=$gpu"
            Assert-Match $r.Log 'shelf reader: off: no NVIDIA graphics card answered'
        }
    }
}

Test-Case 'shelf reader: the reason it is off is logged once, not on every poll' {
    Use-TestReaderRoot -State (Get-OldGoodState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $false }
        $first = Invoke-TestReaderPass $root
        $second = Invoke-TestReaderPass $root
        Assert-Equal 'up-to-date' $second.Result.Outcome
        Assert-Match $first.Log 'no NVIDIA graphics card'
        Assert-False ($second.Log -match 'shelf reader') "a quiet poll repeats nothing: [$($second.Log)]"
    }
}

# ---- the deployer: switching on ----------------------------------------------------------------------------------------------

Test-Case 'shelf reader: with the files present and right, the reader starts after the api is healthy and the api gets the endpoint' {
    Use-TestReaderRoot -State (Get-OldGoodState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true }
        Add-TestReaderFile $root $script:ReaderModel | Out-Null
        Add-TestReaderFile $root $script:ReaderProjector | Out-Null
        $r = Invoke-TestReaderPass $root
        Assert-Equal 'deployed' $r.Result.Outcome
        Assert-Equal 0 $r.Result.ExitCode
        $stack = Get-CallIndex '*up -d api backup aspire-dashboard*'
        $smoke = Get-CallIndex 'curl*health/ready'
        $pull = Get-CallIndex 'docker pull*llama.cpp:server-cuda-b12282@sha256:*'
        $readerUp = Get-CallIndex '*up -d --no-deps shelf-reader*'
        $switch = Get-CallIndex '*up -d --no-deps api*'
        Assert-True ($stack -lt $smoke -and $smoke -lt $pull -and $pull -lt $readerUp -and $readerUp -lt $switch) "order stack=$stack smoke=$smoke pull=$pull reader=$readerUp switch=$switch"
        Assert-Equal '' "$((Get-StackUps)[0].Env['CM_SHELF_READER_ENDPOINT'])" 'the api comes up first with the endpoint empty'
        Assert-Equal $script:ReaderUrl "$((Get-ApiSwitches)[0].Env['CM_SHELF_READER_ENDPOINT'])" 'and gets it once the reader is healthy'
        Assert-Equal $script:Sha2 "$((Get-ApiSwitches)[0].Env['CM_TAG'])" 'the api is restarted at the deployed commit'
        Assert-Match (Get-ReaderUps)[0].Display 'compose --profile gpu '
        Assert-True ([bool]$r.State.shelfReader.on) 'the state remembers the reader is on'
        Assert-Equal $script:Sha2 $r.State.current.sha
        Assert-Match $r.Log 'shelf reader: on'
        Assert-Equal 2 (Get-Calls 'curl' '*health/ready*').Count 'the api is smoke-tested again after the switch'
        Assert-Equal 0 $global:CmDetachedStarts.Count 'nothing to download'
    }
}

Test-Case 'shelf reader: once on, later deploys and the rollback keep the endpoint, and a quiet poll touches nothing' {
    Use-TestReaderRoot -State (Get-OldGoodState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true }
        Add-TestReaderFile $root $script:ReaderModel | Out-Null
        Add-TestReaderFile $root $script:ReaderProjector | Out-Null
        Invoke-TestReaderPass $root | Out-Null
        $global:CmCalls.Clear()
        $quiet = Invoke-TestReaderPass $root
        Assert-Equal 'up-to-date' $quiet.Result.Outcome
        Assert-Equal 0 @(Get-Calls 'docker' '*up -d*').Count 'nothing is restarted on a quiet poll'
        Assert-Equal 0 $global:CmDetachedStarts.Count
        # A newer commit deploys with the endpoint already set, and its failure rolls back with it too.
        $sha3 = 'c3' * 20
        $global:CmMachine.Sha = $sha3; $global:CmMachine.Graph[$sha3] = @($script:Sha1, $script:Sha2); $global:CmMachine.UnhealthyTags = @($sha3)
        $global:CmCalls.Clear()
        $bad = Invoke-TestReaderPass $root
        Assert-Equal 'rolled-back' $bad.Result.Outcome
        $ups = Get-StackUps
        Assert-Equal 2 $ups.Count
        foreach ($up in $ups) { Assert-Equal $script:ReaderUrl "$($up.Env['CM_SHELF_READER_ENDPOINT'])" "the remembered endpoint goes with CM_TAG=$($up.Env['CM_TAG'])" }
        Assert-Equal 0 (Get-ReaderUps).Count 'the reader itself is left running'
    }
}

Test-Case 'shelf reader: a second deploy with everything in place downloads nothing and hashes nothing' {
    Use-TestReaderRoot -State (Get-OldGoodState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true }
        Add-TestReaderFile $root $script:ReaderModel | Out-Null
        Add-TestReaderFile $root $script:ReaderProjector | Out-Null
        Invoke-TestReaderPass $root | Out-Null
        $global:CmHashCount = 0
        $global:CmFileHash = { param($Path) $global:CmHashCount++; (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
        $sha3 = 'c3' * 20
        $global:CmMachine.Sha = $sha3; $global:CmMachine.Graph[$sha3] = @($script:Sha1, $script:Sha2)
        $global:CmCalls.Clear()
        $r = Invoke-TestReaderPass $root
        Assert-Equal 'deployed' $r.Result.Outcome
        Assert-Equal 0 $global:CmDetachedStarts.Count 'no download'
        Assert-Equal 0 $global:CmHashCount 'files already verified (same size and time) are not hashed again'
        Assert-Equal 0 (Get-Calls 'docker' 'docker pull*llama.cpp*').Count 'no reader image pull'
        Assert-Equal 0 (Get-ReaderUps).Count 'the healthy reader is not restarted'
        Assert-Equal $script:ReaderUrl "$((Get-StackUps)[0].Env['CM_SHELF_READER_ENDPOINT'])"
    }
}

# ---- the deployer: downloads ---------------------------------------------------------------------------------------------------

Test-Case 'shelf reader: missing files are downloaded once from the pinned revision in the background, and the deploy does not wait' {
    Use-TestReaderRoot -State (Get-OldGoodState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true }
        $r = Invoke-TestReaderPass $root
        Assert-Equal 'deployed' $r.Result.Outcome 'the deploy finishes in the same pass'
        Assert-Equal 0 $r.Result.ExitCode
        Assert-Equal $script:Sha2 $r.State.current.sha
        Assert-Equal 2 $global:CmDetachedStarts.Count 'one background download per file'
        foreach ($name in $script:ReaderModel, $script:ReaderProjector) {
            $start = @($global:CmDetachedStarts | Where-Object { $_.Line -like "*$name*" })
            Assert-Equal 1 $start.Count $name
            Assert-Match $start[0].Line ([regex]::Escape("https://huggingface.co/Qwen/Qwen3-VL-8B-Instruct-GGUF/resolve/f982a07559d4a2f6c8744d840bf6fccab30eea96/$name"))
            Assert-False ($start[0].Line -match '/resolve/main/') 'never by main'
            Assert-Match $start[0].Line ([regex]::Escape("$name.partial")) 'to a temporary name beside the final one'
        }
        Assert-Equal 0 (Get-ReaderUps).Count 'nothing starts before the files verify'
        Assert-Equal 0 (Get-ApiSwitches).Count
        Assert-Match $r.Log 'shelf reader: downloading'
        # The next poll, while the download is still running, starts no second one.
        $again = Invoke-TestReaderPass $root
        Assert-Equal 'up-to-date' $again.Result.Outcome
        Assert-Equal 2 $global:CmDetachedStarts.Count 'a running download is not started again'
    }
}

Test-Case 'shelf reader: a finished download is verified, renamed, and only then used, on a later pass' {
    Use-TestReaderRoot -State (Get-OldGoodState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true } -DownloadFinishes
        $first = Invoke-TestReaderPass $root
        Assert-Equal 'deployed' $first.Result.Outcome
        Assert-Equal 0 (Get-ReaderUps).Count 'not in the pass that started the download'
        $second = Invoke-TestReaderPass $root
        Assert-Equal 'up-to-date' $second.Result.Outcome
        foreach ($name in $script:ReaderModel, $script:ReaderProjector) {
            Assert-True (Test-Path -LiteralPath (Join-Path $root "models/$name")) "$name is in place"
            Assert-False (Test-Path -LiteralPath (Join-Path $root "models/$name.partial")) "$name.partial is gone"
        }
        Assert-Equal 1 (Get-ReaderUps).Count 'the reader starts in the pass after the files verify'
        Assert-Equal $script:ReaderUrl "$((Get-ApiSwitches)[0].Env['CM_SHELF_READER_ENDPOINT'])"
        Assert-Equal 2 $global:CmDetachedStarts.Count 'downloaded once'
        Assert-Match $second.Log 'verified'
    }
}

Test-Case 'shelf reader: a download with the wrong hash is deleted, logged as an alert and leaves the reader off, and is not fetched again at once' {
    Use-TestReaderRoot -State (Get-OldGoodState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true }
        Add-TestReaderFile $root $script:ReaderModel | Out-Null
        $partial = Add-TestReaderFile $root $script:ReaderProjector -Partial -Wrong
        $r = Invoke-TestReaderPass $root
        Assert-Equal 'deployed' $r.Result.Outcome
        Assert-Equal 0 $r.Result.ExitCode 'a bad download never fails the deploy'
        Assert-False (Test-Path -LiteralPath $partial) 'the bad download is deleted'
        Assert-False (Test-Path -LiteralPath (Join-Path $root "models/$($script:ReaderProjector)")) 'and never renamed into place'
        Assert-Match $r.Log 'ERROR ALERT shelf-reader '
        Assert-Match $r.Log ([regex]::Escape($script:ReaderProjector))
        Assert-Match $r.Log 'SHA-256'
        Assert-Equal 0 (Get-ReaderUps).Count
        Assert-Equal 0 (Get-ApiSwitches).Count
        Assert-False ([bool]$r.State.shelfReader.on)
        Assert-Equal 0 $global:CmDetachedStarts.Count 'not fetched again in the same pass'
        $again = Invoke-TestReaderPass $root
        Assert-Equal 0 $global:CmDetachedStarts.Count 'nor on the next poll: a bad file waits a day before another 6 GB'
        Assert-Equal 1 @($global:CmMachine.Issues | Where-Object { $_.body -match 'cm-alert:shelf-reader-failed' }).Count 'one alert issue'
        Assert-Equal 0 (Get-Calls 'curl' '*/fail*').Count 'the backup heartbeat is not pinged as failed for the reader'
    }
}

Test-Case 'shelf reader: not enough free disk space starts no download' {
    Use-TestReaderRoot -State (Get-OldGoodState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true }
        $global:CmFreeBytes = { param($Path) [long]1GB }
        $r = Invoke-TestReaderPass $root
        Assert-Equal 'deployed' $r.Result.Outcome
        Assert-Equal 0 $global:CmDetachedStarts.Count
        Assert-Match $r.Log 'ALERT shelf-reader .*free'
    }
}

# ---- the deployer: the reader never takes the app down ----------------------------------------------------------------------

Test-Case 'shelf reader: a reader that fails to start is logged as an alert and never rolls the api back' {
    foreach ($case in @(@{ ReaderUpFails = $true }, @{ ReaderPullFails = $true }, @{ ReaderStartsAs = 'unhealthy' })) {
        Use-TestReaderRoot -State (Get-OldGoodState) {
            param($root)
            Start-TestReaderMachine (@{ Gpu = $true } + $case)
            Add-TestReaderFile $root $script:ReaderModel | Out-Null
            Add-TestReaderFile $root $script:ReaderProjector | Out-Null
            $r = Invoke-TestReaderPass $root
            $what = ($case.Keys -join ',')
            Assert-Equal 'deployed' $r.Result.Outcome $what
            Assert-Equal 0 $r.Result.ExitCode $what
            Assert-Equal $script:Sha2 $r.State.current.sha "the new version stays ($what)"
            Assert-False $r.State.ContainsKey('failedSha') "nothing is blamed ($what)"
            Assert-Equal 1 (Get-StackUps).Count "no rollback up ($what)"
            Assert-Equal $script:Sha2 $global:CmMachine.Up $what
            Assert-Equal 0 @(Get-ApiSwitches | Where-Object { $_.Env['CM_SHELF_READER_ENDPOINT'] }).Count "the api never gets the endpoint ($what)"
            Assert-Match $r.Log 'ERROR ALERT shelf-reader '
            Assert-False ($r.Log -match 'ALERT deploy') "no deploy alert ($what)"
            Assert-False ([bool]$r.State.shelfReader.on) $what
            Assert-Equal 0 (Get-Calls 'curl' '*/fail*').Count "the backup heartbeat is not pinged as failed ($what)"
            # The next poll does not hammer it: a failed reader waits before the next try.
            $global:CmCalls.Clear()
            $next = Invoke-TestReaderPass $root
            Assert-Equal 'up-to-date' $next.Result.Outcome
            Assert-Equal 0 (Get-ReaderUps).Count "no retry on the next poll ($what)"
            Assert-Equal 0 (Get-Calls 'docker' 'docker pull*llama.cpp*').Count "no pull retry on the next poll ($what)"
        }
    }
}

Test-Case 'shelf reader: a crash-looping reader during a deploy does not roll the deploy back' {
    Use-TestReaderRoot -State (Get-OldGoodState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $false; ReaderHealth = 'restarting' }
        $r = Invoke-TestReaderPass $root
        Assert-Equal 'deployed' $r.Result.Outcome
        Assert-Equal 1 (Get-StackUps).Count
    }
}

Test-Case 'shelf reader: an api that fails its smoke test with the endpoint is switched back off, and nothing rolls back' {
    Use-TestReaderRoot -State (Get-OldGoodState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; ApiUnhealthyWithReader = $true }
        Add-TestReaderFile $root $script:ReaderModel | Out-Null
        Add-TestReaderFile $root $script:ReaderProjector | Out-Null
        $r = Invoke-TestReaderPass $root
        Assert-Equal 'deployed' $r.Result.Outcome
        Assert-Equal $script:Sha2 $r.State.current.sha
        $switches = Get-ApiSwitches
        Assert-Equal 2 $switches.Count 'on, then back off'
        Assert-Equal $script:ReaderUrl "$($switches[0].Env['CM_SHELF_READER_ENDPOINT'])"
        Assert-Equal '' "$($switches[1].Env['CM_SHELF_READER_ENDPOINT'])"
        Assert-Equal '' "$($global:CmMachine.ApiEndpoint)"
        Assert-False ([bool]$r.State.shelfReader.on)
        Assert-Match $r.Log 'ERROR ALERT shelf-reader '
        Assert-Equal 1 (Get-Calls 'docker' '*--profile gpu*stop shelf-reader*').Count 'the reader is stopped, freeing the card'
    }
}

Test-Case 'shelf reader: a reader that dies while on keeps the api up, and after ten minutes the api is switched off' {
    Use-TestReaderRoot -State (Get-OldGoodState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true }
        Add-TestReaderFile $root $script:ReaderModel | Out-Null
        Add-TestReaderFile $root $script:ReaderProjector | Out-Null
        $global:CmNow = [DateTime]::UtcNow
        Invoke-TestReaderPass $root | Out-Null
        $global:CmMachine.ReaderHealth = 'unhealthy'
        $global:CmCalls.Clear()
        $down = Invoke-TestReaderPass $root
        Assert-Equal 'up-to-date' $down.Result.Outcome
        Assert-Equal 0 (Get-ApiSwitches).Count 'a reader that just went down is given time; the api treats it as unavailable meanwhile'
        Assert-True ([bool]$down.State.shelfReader.on)
        $global:CmNow = ([DateTime]$global:CmNow).AddMinutes(11)
        $late = Invoke-TestReaderPass $root
        Assert-Equal 'up-to-date' $late.Result.Outcome
        Assert-Equal 0 $late.Result.ExitCode
        Assert-Equal 1 (Get-ApiSwitches).Count
        Assert-Equal '' "$((Get-ApiSwitches)[0].Env['CM_SHELF_READER_ENDPOINT'])" 'switched off'
        Assert-False ([bool]$late.State.shelfReader.on)
        Assert-Match $late.Log 'ERROR ALERT shelf-reader '
    }
}

Test-Case 'shelf reader: switching it off in .env after it was on stops the reader and empties the endpoint' {
    Use-TestReaderRoot -State (Get-OldGoodState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true }
        Add-TestReaderFile $root $script:ReaderModel | Out-Null
        Add-TestReaderFile $root $script:ReaderProjector | Out-Null
        Invoke-TestReaderPass $root | Out-Null
        Set-Content -LiteralPath (Join-Path $root '.env') -Value (Get-TestReaderEnv @('CM_SHELF_READER=0'))
        $global:CmCalls.Clear()
        $r = Invoke-TestReaderPass $root
        Assert-Equal 'up-to-date' $r.Result.Outcome
        Assert-Equal 1 (Get-ApiSwitches).Count
        Assert-Equal '' "$((Get-ApiSwitches)[0].Env['CM_SHELF_READER_ENDPOINT'])"
        Assert-Equal 1 (Get-Calls 'docker' '*--profile gpu*stop shelf-reader*').Count
        Assert-False ([bool]$r.State.shelfReader.on)
    }
}

Test-Case 'shelf reader: a dry run starts, downloads and changes nothing' {
    Use-TestReaderRoot -State (Get-OldGoodState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true }
        Initialize-Cm -Apply $false
        $r = Invoke-CmDeploy -Ctx (New-TestContext -Root $root) 6>$null
        Assert-Equal 'dry-run' $r.Outcome
        Assert-Equal 0 $global:CmDetachedStarts.Count
        Assert-Equal 0 (Get-ReaderUps).Count
        Assert-True ((Get-CmPlan -Ctx (New-TestContext -Root $root)) -join "`n" -match 'shelf') 'the plan describes the reader'
    }
}
