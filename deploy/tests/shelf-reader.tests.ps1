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
    $global:CmFindDownload = { param($Partial) $false }   # no curl is running unless a test says so
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
        image    = 'ghcr.io/ggml-org/llama.cpp:server-cuda-b12282@sha256:bb29527ecb3da442d9459591c252351d957b6e3ee1635e8d0406c0a4442eb08c'
        networks = [pscustomobject]@{ reader = $null }
        volumes  = @([pscustomobject]@{ type = 'bind'; source = $Source; target = '/models'; read_only = $ReadOnly })
        deploy  = [pscustomobject]@{ resources = [pscustomobject]@{ reservations = [pscustomobject]@{ devices = @([pscustomobject]@{ driver = 'nvidia'; count = 1; capabilities = @('gpu') }) } } }
    }
}
function New-GuardConfigWithReader {
    param($Reader = (New-GuardReader))
    $cfg = New-GoodComposeConfig
    $cfg.services | Add-Member -NotePropertyName 'shelf-reader' -NotePropertyValue $Reader
    $cfg.services.api | Add-Member -NotePropertyName networks -NotePropertyValue ([pscustomobject]@{ default = $null; reader = $null })
    $cfg | Add-Member -NotePropertyName networks -NotePropertyValue ([pscustomobject]@{ default = [pscustomobject]@{ name = 'collectionmaxxing_default' }; reader = [pscustomobject]@{ name = 'collectionmaxxing_reader'; internal = $true } })
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


Test-Case 'shelf reader guard: only the pinned image, one GPU by count with no device ids, no runtime, and no NVIDIA variables elsewhere' {
    $reader = New-GuardReader; $reader.image = 'ghcr.io/ggml-org/llama.cpp:server-cuda-b12282'
    Assert-Match ((Get-GuardProblems (New-GuardConfigWithReader $reader)) -join ';') "service 'shelf-reader' must run the pinned image"
    $reader = New-GuardReader; $reader.image = 'ghcr.io/someone/llama.cpp@sha256:bb29527ecb3da442d9459591c252351d957b6e3ee1635e8d0406c0a4442eb08c'
    Assert-Match ((Get-GuardProblems (New-GuardConfigWithReader $reader)) -join ';') 'pinned image'
    $reader = New-GuardReader; $reader.deploy.resources.reservations.devices[0].count = 2
    Assert-Match ((Get-GuardProblems (New-GuardConfigWithReader $reader)) -join ';') "count '2'"
    $reader = New-GuardReader; $reader.deploy.resources.reservations.devices[0].count = 'all'
    Assert-Match ((Get-GuardProblems (New-GuardConfigWithReader $reader)) -join ';') "count 'all'"
    $reader = New-GuardReader; $reader.deploy.resources.reservations.devices[0] | Add-Member -NotePropertyName device_ids -NotePropertyValue @('0', '1')
    Assert-Match ((Get-GuardProblems (New-GuardConfigWithReader $reader)) -join ';') 'device_ids'
    foreach ($svc in 'api', 'shelf-reader') {
        $cfg = New-GuardConfigWithReader; $cfg.services.$svc | Add-Member -NotePropertyName runtime -NotePropertyValue 'nvidia'
        Assert-Match ((Get-GuardProblems $cfg) -join ';') "service '$svc' sets a container runtime"
    }
    foreach ($var in 'NVIDIA_VISIBLE_DEVICES', 'NVIDIA_DRIVER_CAPABILITIES') {
        $cfg = New-GuardConfigWithReader; $cfg.services.api | Add-Member -NotePropertyName environment -NotePropertyValue ([pscustomobject]@{ $var = 'all' })
        Assert-Match ((Get-GuardProblems $cfg) -join ';') "service 'api' asks the NVIDIA runtime"
    }
}

Test-Case 'shelf reader guard: CM_MODELS_DIR cannot widen the binds, for the reader or any other service' {
    foreach ($dir in 'C:\', 'C:/', 'C:\CollectionMaxxing', 'F:\Backups', '/') {
        $envMap = New-GuardEnv; $envMap['CM_MODELS_DIR'] = $dir
        $problems = @(Get-GuardProblems (New-GuardConfigWithReader (New-GuardReader -Source 'C:\Users\someone')) $envMap)
        Assert-Match ($problems -join ';') 'CM_MODELS_DIR names a drive root' "[$dir]"
        Assert-Match ($problems -join ';') "service 'shelf-reader' bind-mounts a host folder outside the models folder" "[$dir] then allows the reader nothing"
    }
    $envMap = New-GuardEnv; $envMap['CM_MODELS_DIR'] = 'D:\Models'
    $cfg = New-GuardConfigWithReader (New-GuardReader -Source 'D:\Models')
    $cfg.services.api | Add-Member -NotePropertyName volumes -NotePropertyValue @([pscustomobject]@{ type = 'bind'; source = 'D:\Models'; target = '/m'; read_only = $true }) -Force
    $problems = @(Get-GuardProblems $cfg $envMap)
    Assert-Equal 1 $problems.Count ($problems -join '; ')
    Assert-Match $problems[0] "service 'api' bind-mounts a host folder outside" 'the models folder is the reader''s alone, even read-only'
}

Test-Case 'shelf reader guard: the reader is only on its internal network, and only the api joins it' {
    $reader = New-GuardReader; $reader.networks = [pscustomobject]@{ default = $null; reader = $null }
    Assert-Match ((Get-GuardProblems (New-GuardConfigWithReader $reader)) -join ';') "service 'shelf-reader' must be on the 'reader' network only"
    $reader = New-GuardReader; $reader.PSObject.Properties.Remove('networks')
    Assert-Match ((Get-GuardProblems (New-GuardConfigWithReader $reader)) -join ';') "service 'shelf-reader' must be on the 'reader' network only" 'no networks means the default one'
    $cfg = New-GuardConfigWithReader
    $cfg.services | Add-Member -NotePropertyName backup -NotePropertyValue ([pscustomobject]@{ networks = [pscustomobject]@{ default = $null; reader = $null } })
    Assert-Match ((Get-GuardProblems $cfg) -join ';') "service 'backup' joins the 'reader' network"
    $cfg = New-GuardConfigWithReader; $cfg.networks.reader.internal = $false
    Assert-Match ((Get-GuardProblems $cfg) -join ';') 'must exist and be internal'
}

Test-Case 'shelf reader compose: the WSL driver folder is mounted read-only at its own path, on the reader only (#802)' {
    $config = Get-RealComposeConfig -Profiles @('gpu')
    $drivers = @($config.services.'shelf-reader'.volumes | Where-Object { $_.target -eq '/usr/lib/wsl/drivers' })
    Assert-Equal 1 $drivers.Count 'the reader mounts the driver folder'
    Assert-Equal 'bind' "$($drivers[0].type)"
    Assert-Equal '/usr/lib/wsl/drivers' "$($drivers[0].source)" 'a path inside Docker Desktop''s Linux VM, kept as written (not resolved against the compose folder or a drive)'
    Assert-True ($drivers[0].read_only -eq $true) 'read-only'
    foreach ($p in $config.services.PSObject.Properties) {
        if ($p.Name -eq 'shelf-reader') { continue }
        Assert-Equal 0 @($p.Value.volumes | Where-Object { "$($_.source)" -match 'wsl' -or "$($_.target)" -match 'wsl' }).Count "$($p.Name) does not mount the driver folder"
    }
}

function New-GuardDriversMount {
    param([string]$Source = '/usr/lib/wsl/drivers', [string]$Target = '/usr/lib/wsl/drivers', [bool]$ReadOnly = $true)
    [pscustomobject]@{ type = 'bind'; source = $Source; target = $Target; read_only = $ReadOnly }
}

Test-Case 'shelf reader guard: the reader may mount the WSL driver folder read-only at its own path (#802)' {
    $reader = New-GuardReader; $reader.volumes = @($reader.volumes) + @(New-GuardDriversMount)
    $problems = @(Get-GuardProblems (New-GuardConfigWithReader $reader))
    Assert-Equal 0 $problems.Count ($problems -join '; ')
}

Test-Case 'shelf reader guard: the driver folder writable, at another target, or from another source is refused (#802)' {
    foreach ($case in @(
            @{ Mount = (New-GuardDriversMount -ReadOnly $false); Want = 'writable' }
            @{ Mount = (New-GuardDriversMount -Target '/drivers'); Want = 'driver folder' }
            @{ Mount = (New-GuardDriversMount -Target '/usr/lib/wsl'); Want = 'driver folder' }
            @{ Mount = (New-GuardDriversMount -Source 'C:\Users\someone'); Want = 'driver folder' }
            @{ Mount = (New-GuardDriversMount -Source '/usr/lib/wsl'); Want = 'driver folder' }
            @{ Mount = (New-GuardDriversMount -Source '/usr/lib/wsl' -Target '/usr/lib/wsl'); Want = 'outside the models folder' }
            @{ Mount = (New-GuardDriversMount -Source '/usr/lib/wsl/drivers/..' -Target '/x'); Want = 'outside the models folder' }
            @{ Mount = (New-GuardDriversMount -Source '/usr/lib/wsl/drivers/sub' -Target '/x'); Want = 'outside the models folder' }
            @{ Mount = (New-GuardDriversMount -Source '/USR/LIB/WSL/DRIVERS' -Target '/USR/LIB/WSL/DRIVERS'); Want = 'outside the models folder' }
            @{ Mount = (New-GuardDriversMount -Source '/' -Target '/host'); Want = 'outside the models folder' }
        )) {
        $m = $case.Mount
        $what = "$($m.source) -> $($m.target) ro=$($m.read_only)"
        $reader = New-GuardReader; $reader.volumes = @($reader.volumes) + @($m)
        $problems = @(Get-GuardProblems (New-GuardConfigWithReader $reader))
        Assert-Equal 1 $problems.Count "[$what]: $($problems -join '; ')"
        Assert-Match $problems[0] "service 'shelf-reader'" $what
        Assert-Match $problems[0] $case.Want $what
    }
}

Test-Case 'shelf reader guard: the WSL driver folder on any other service is refused, even read-only (#802)' {
    foreach ($svc in 'api', 'backup') {
        $cfg = New-GuardConfigWithReader
        if (-not $cfg.services.$svc) { $cfg.services | Add-Member -NotePropertyName $svc -NotePropertyValue ([pscustomobject]@{}) }
        $cfg.services.$svc | Add-Member -NotePropertyName volumes -NotePropertyValue @(New-GuardDriversMount) -Force
        $problems = @(Get-GuardProblems $cfg)
        Assert-Equal 1 $problems.Count "[$svc]: $($problems -join '; ')"
        Assert-Match $problems[0] "service '$svc' bind-mounts a host folder outside"
    }
}

Test-Case 'shelf reader compose: the reader is on the internal reader network only, the api on both' {
    $config = Get-RealComposeConfig -Profiles @('gpu')
    Assert-Equal 'reader' (@($config.services.'shelf-reader'.networks.PSObject.Properties.Name) -join ',')
    Assert-Equal 'default,reader' (@($config.services.api.networks.PSObject.Properties.Name | Sort-Object) -join ',')
    Assert-True ($config.networks.reader.internal -eq $true) 'internal: no route out'
    foreach ($p in $config.services.PSObject.Properties) {
        if ($p.Name -in 'api', 'shelf-reader') { continue }
        Assert-False (@($p.Value.networks.PSObject.Properties.Name) -contains 'reader') "$($p.Name) is not on the reader network"
    }
    Assert-Equal '10m0s' "$($config.services.'shelf-reader'.healthcheck.start_period)" 'start_period matches the 10-minute start window'
}

Test-Case 'shelf reader: the production pins are the ones in issue #795' {
    $pins = @($script:CmShelfReaderPins)
    Assert-Equal 2 $pins.Count
    Assert-Equal 'Qwen3VL-8B-Instruct-Q4_K_M.gguf' $pins[0].Name
    Assert-Equal '5027784800' "$($pins[0].Size)"
    Assert-Equal '67d1659bfe71b89d50b45a4ad1a9e5b997e5bb16ce5da66a6a6167abd569e9e2' $pins[0].Sha256
    Assert-Equal 'mmproj-Qwen3VL-8B-Instruct-F16.gguf' $pins[1].Name
    Assert-Equal '1159029824' "$($pins[1].Size)"
    Assert-Equal 'ca524100ebf825c9a870db1c580d03879e0da0ab2541697e2458e64891cf9d38' $pins[1].Sha256
    Assert-Equal 'Qwen/Qwen3-VL-8B-Instruct-GGUF' $script:CmShelfReaderRepo
    Assert-Equal 'f982a07559d4a2f6c8744d840bf6fccab30eea96' $script:CmShelfReaderRevision
    Assert-Equal 'ghcr.io/ggml-org/llama.cpp:server-cuda-b12282@sha256:bb29527ecb3da442d9459591c252351d957b6e3ee1635e8d0406c0a4442eb08c' $script:CmShelfReaderImage
    $global:CmShelfReaderFiles = $null
    Assert-Equal 'https://huggingface.co/Qwen/Qwen3-VL-8B-Instruct-GGUF/resolve/f982a07559d4a2f6c8744d840bf6fccab30eea96/Qwen3VL-8B-Instruct-Q4_K_M.gguf' (Get-CmShelfReaderFileUrl 'Qwen3VL-8B-Instruct-Q4_K_M.gguf')
    $compose = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../docker-compose.yml') -Raw
    Assert-True ($compose.Contains($script:CmShelfReaderImage)) 'the compose file runs the image the deployer pulls'
}

# ---- the deployer: helpers -----------------------------------------------------------------------------------------------------

function Get-UpToDateState { @{ current = @{ sha = $script:Sha2; created = '2026-10-13T12:00:00.0000000Z'; deployedAt = '2026-10-13T12:05:00Z' } } }
function Add-TestReaderFiles { param([string]$Root) Add-TestReaderFile $Root $script:ReaderModel | Out-Null; Add-TestReaderFile $Root $script:ReaderProjector | Out-Null }
function Remove-TestReaderFile { param([string]$Root, [string]$Name) [IO.File]::Delete((Join-Path (Join-Path $Root 'models') $Name)) }
function Invoke-TestReaderUntilOn {
    <# Quiet passes until the state says the reader is on (start, then switch), at most four. #>
    param([string]$Root)
    for ($i = 0; $i -lt 4; $i++) { $r = Invoke-TestReaderPass $Root; if ($r.State.shelfReader.on) { return $r } }
    throw "the reader never came on: $($r.Log)"
}
function Get-ReaderAlerts { param([string]$Kind = 'shelf-reader-failed') @($global:CmMachine.Issues | Where-Object { $_.body -match "cm-alert:$Kind" }) }

# ---- the deployer: switched off --------------------------------------------------------------------------------------------

function Assert-ReaderUntouched {
    param([string]$Why)
    Assert-Equal 0 (Get-ReaderUps).Count "no reader start ($Why)"
    Assert-Equal 0 (Get-Calls 'docker' 'docker pull*llama.cpp*').Count "no reader image pull ($Why)"
    Assert-Equal 0 (Get-Calls 'docker' '*shelf-reader*').Count "no reader call at all ($Why)"
    Assert-Equal 0 $global:CmDetachedStarts.Count "no download ($Why)"
    Assert-Equal 0 (Get-ApiSwitches).Count "no api restart ($Why)"
    foreach ($up in @(Get-StackUps)) { Assert-Equal '' "$($up.Env['CM_SHELF_READER_ENDPOINT'])" "the api endpoint is empty ($Why)" }
}

Test-Case 'shelf reader: with CM_SHELF_READER=0 nothing is pulled, downloaded or started and the api endpoint is empty' {
    Use-TestReaderRoot -State (Get-OldGoodState) -EnvText (Get-TestReaderEnv @('CM_SHELF_READER=0')) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true }
        Add-TestReaderFiles $root
        $deploy = Invoke-TestReaderPass $root
        Assert-Equal 'deployed' $deploy.Result.Outcome
        $quiet = Invoke-TestReaderPass $root
        Assert-Equal 'up-to-date' $quiet.Result.Outcome
        Assert-Equal 0 (Get-Calls 'nvidia-smi').Count 'the off switch is read before anything is asked of the card'
        Assert-ReaderUntouched 'switched off'
        Assert-Match $quiet.Log 'shelf reader: off: CM_SHELF_READER=0'
    }
}

Test-Case 'shelf reader: a host with no NVIDIA card starts nothing, downloads nothing and leaves the endpoint empty' {
    foreach ($gpu in $false, 'empty') {
        $global:CmCalls.Clear()
        Use-TestReaderRoot -State (Get-UpToDateState) {
            param($root)
            Start-TestReaderMachine @{ Gpu = $gpu; Up = $script:Sha2 }
            $r = Invoke-TestReaderPass $root
            Assert-Equal 'up-to-date' $r.Result.Outcome
            Assert-Equal 1 (Get-Calls 'nvidia-smi').Count
            Assert-ReaderUntouched "gpu=$gpu"
            Assert-Match $r.Log 'shelf reader: off: no NVIDIA graphics card answered'
            Assert-False (Test-Path -LiteralPath (Join-Path $root 'models')) 'no models folder is made'
        }
    }
}

Test-Case 'shelf reader: the reason it is off is logged once, not on every poll' {
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $false; Up = $script:Sha2 }
        $first = Invoke-TestReaderPass $root
        $second = Invoke-TestReaderPass $root
        Assert-Match $first.Log 'no NVIDIA graphics card'
        Assert-False ($second.Log -match 'shelf reader') "a quiet poll repeats nothing: [$($second.Log)]"
    }
}

# ---- the deployer: switching on --------------------------------------------------------------------------------------------

Test-Case 'shelf reader: with the files present and right, the reader starts after the api is healthy, on a quiet poll, and the api then gets the endpoint' {
    Use-TestReaderRoot -State (Get-OldGoodState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true }
        Add-TestReaderFiles $root
        $deploy = Invoke-TestReaderPass $root
        Assert-Equal 'deployed' $deploy.Result.Outcome
        Assert-Equal 0 (Get-Calls 'nvidia-smi').Count 'a pass that deployed does not look at the reader'
        Assert-Equal '' "$((Get-StackUps)[0].Env['CM_SHELF_READER_ENDPOINT'])" 'the api comes up first with the endpoint empty'
        $start = Invoke-TestReaderPass $root
        Assert-Equal 'up-to-date' $start.Result.Outcome
        $stack = Get-CallIndex '*up -d api backup aspire-dashboard*'
        $smoke = Get-CallIndex 'curl*health/ready'
        $pull = Get-CallIndex 'docker pull*llama.cpp:server-cuda-b12282@sha256:*'
        $readerUp = Get-CallIndex '*up -d --no-deps shelf-reader*'
        Assert-True ($stack -lt $smoke -and $smoke -lt $pull -and $pull -lt $readerUp) "order stack=$stack smoke=$smoke pull=$pull reader=$readerUp"
        Assert-Equal 0 (Get-ApiSwitches).Count 'the start pass does not wait for the reader'
        Assert-Match (Get-ReaderUps)[0].Display 'compose --profile gpu '
        Assert-Match (Get-ReaderUps)[0].ComposeFile '\.reader\.\d+\.yml$'
        $on = Invoke-TestReaderPass $root
        $switches = Get-ApiSwitches
        Assert-Equal 1 $switches.Count 'one api restart, to give it the endpoint'
        Assert-Equal $script:ReaderUrl "$($switches[0].Env['CM_SHELF_READER_ENDPOINT'])"
        Assert-Equal $script:Sha2 "$($switches[0].Env['CM_TAG'])" 'the api is restarted at the deployed commit'
        Assert-Match $switches[0].ComposeFile '\.reader\.\d+\.yml$' 'the step''s compose copy has a name of its own'
        Assert-True ([bool]$on.State.shelfReader.on) 'the state remembers the reader is on'
        Assert-Equal $script:Sha2 $on.State.current.sha
        Assert-Match $on.Log 'shelf reader: on'
        Assert-Equal 0 $global:CmDetachedStarts.Count 'nothing to download'
    }
}

Test-Case 'shelf reader: a healthy reader and an api that already has the endpoint need no api restart' {
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2; ReaderHealth = 'healthy'; ApiEndpoint = $script:ReaderUrl }
        Add-TestReaderFiles $root
        $r = Invoke-TestReaderPass $root
        Assert-True ([bool]$r.State.shelfReader.on)
        Assert-Equal 0 (Get-ApiSwitches).Count
    }
}

Test-Case 'shelf reader: once on, later deploys and the rollback keep the endpoint, and a quiet poll touches nothing' {
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2 }
        Add-TestReaderFiles $root
        Invoke-TestReaderUntilOn $root | Out-Null
        $global:CmCalls.Clear()
        $quiet = Invoke-TestReaderPass $root
        Assert-Equal 'up-to-date' $quiet.Result.Outcome
        Assert-Equal 0 @(Get-Calls 'docker' '*up -d*').Count 'nothing is restarted on a quiet poll'
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
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2 }
        Add-TestReaderFiles $root
        Invoke-TestReaderUntilOn $root | Out-Null
        $global:CmHashCount = 0
        $global:CmFileHash = { param($Path) $global:CmHashCount++; (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
        $sha3 = 'c3' * 20
        $global:CmMachine.Sha = $sha3; $global:CmMachine.Graph[$sha3] = @($script:Sha1, $script:Sha2)
        $global:CmCalls.Clear()
        $r = Invoke-TestReaderPass $root
        Assert-Equal 'deployed' $r.Result.Outcome
        Invoke-TestReaderPass $root | Out-Null
        Assert-Equal 0 $global:CmDetachedStarts.Count 'no download'
        Assert-Equal 0 $global:CmHashCount 'files already verified (same size and time) are not hashed again'
        Assert-Equal 0 (Get-Calls 'docker' 'docker pull*llama.cpp*').Count 'no reader image pull'
        Assert-Equal 0 (Get-ReaderUps).Count 'the healthy reader is not restarted'
        Assert-Equal 0 (Get-ApiSwitches).Count 'no api restart'
        Assert-Equal $script:ReaderUrl "$((Get-StackUps)[0].Env['CM_SHELF_READER_ENDPOINT'])"
    }
}

# ---- the deployer: downloads -----------------------------------------------------------------------------------------------

Test-Case 'shelf reader: missing files are downloaded once from the pinned revision in the background, and the deploy does not wait' {
    Use-TestReaderRoot -State (Get-OldGoodState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true }
        $r = Invoke-TestReaderPass $root
        Assert-Equal 'deployed' $r.Result.Outcome 'the deploy finishes by itself'
        Assert-Equal 0 $global:CmDetachedStarts.Count 'and does not start downloads'
        $quiet = Invoke-TestReaderPass $root
        Assert-Equal 'up-to-date' $quiet.Result.Outcome
        Assert-Equal 2 $global:CmDetachedStarts.Count 'one background download per file'
        foreach ($name in $script:ReaderModel, $script:ReaderProjector) {
            $start = @($global:CmDetachedStarts | Where-Object { $_.Line -like "*$name*" })
            Assert-Equal 1 $start.Count $name
            Assert-Match $start[0].Line ([regex]::Escape("https://huggingface.co/Qwen/Qwen3-VL-8B-Instruct-GGUF/resolve/f982a07559d4a2f6c8744d840bf6fccab30eea96/$name"))
            Assert-False ($start[0].Line -match '/resolve/main/') 'never by main'
            Assert-Match $start[0].Line ([regex]::Escape("$name.partial")) 'to a temporary name beside the final one'
            Assert-Match $start[0].Line '--connect-timeout 30'
            Assert-Match $start[0].Line '--retry 5 '
        }
        Assert-Equal 0 (Get-ReaderUps).Count 'nothing starts before the files verify'
        Assert-Match $quiet.Log 'shelf reader: downloading'
        Invoke-TestReaderPass $root | Out-Null
        Assert-Equal 2 $global:CmDetachedStarts.Count 'a running download is not started again'
    }
}

Test-Case 'shelf reader: a download is alive while its .partial grows, and a stalled one is resumed only when no curl writes it' {
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2 }
        Add-TestReaderFile $root $script:ReaderModel | Out-Null
        $global:CmRunningDownload = $false
        $global:CmFindDownload = { param($Partial) $global:CmRunningDownload }
        $partial = Join-Path $root "models/$($script:ReaderProjector).partial"
        [IO.File]::WriteAllBytes($partial, [byte[]]@(1, 2, 3))
        [IO.File]::SetLastWriteTimeUtc($partial, [DateTime]::UtcNow.AddHours(-3))   # an old time stamp does not make it dead
        $t0 = [DateTime]::UtcNow
        $global:CmNow = $t0
        Invoke-TestReaderPass $root | Out-Null
        Assert-Equal 0 $global:CmDetachedStarts.Count 'first look: the partial is taken as a running download'
        [IO.File]::WriteAllBytes($partial, [byte[]]@(1, 2, 3, 4, 5))
        [IO.File]::SetLastWriteTimeUtc($partial, $t0.AddHours(-3))
        $global:CmNow = $t0.AddMinutes(14)
        Invoke-TestReaderPass $root | Out-Null
        $global:CmNow = $t0.AddMinutes(28)
        Invoke-TestReaderPass $root | Out-Null
        Assert-Equal 0 $global:CmDetachedStarts.Count 'it grew since the last pass, so it is alive'
        $global:CmRunningDownload = $true
        $global:CmNow = $t0.AddMinutes(60)
        Invoke-TestReaderPass $root | Out-Null
        Assert-Equal 0 $global:CmDetachedStarts.Count 'not grown for 30 minutes, but a curl still writes it: no second one'
        $global:CmRunningDownload = $false
        $global:CmNow = $t0.AddMinutes(62)
        Invoke-TestReaderPass $root | Out-Null
        Assert-Equal 1 $global:CmDetachedStarts.Count 'dead: resumed once'
        Assert-Match $global:CmDetachedStarts[0].Line '-C - '
        $global:CmNow = $t0.AddMinutes(64)
        Invoke-TestReaderPass $root | Out-Null
        Assert-Equal 1 $global:CmDetachedStarts.Count 'and not started again right after'
    }
}

Test-Case 'shelf reader: a finished download is verified, renamed, and only then used, on a later pass' {
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2 } -DownloadFinishes
        Invoke-TestReaderPass $root | Out-Null
        Assert-Equal 0 (Get-ReaderUps).Count 'not in the pass that started the download'
        $second = Invoke-TestReaderPass $root
        foreach ($name in $script:ReaderModel, $script:ReaderProjector) {
            Assert-True (Test-Path -LiteralPath (Join-Path $root "models/$name")) "$name is in place"
            Assert-False (Test-Path -LiteralPath (Join-Path $root "models/$name.partial")) "$name.partial is gone"
        }
        Assert-Match $second.Log 'verified'
        Assert-Equal 1 (Get-ReaderUps).Count 'the reader starts in the pass after the files verify'
        Invoke-TestReaderPass $root | Out-Null
        Assert-Equal $script:ReaderUrl "$((Get-ApiSwitches)[0].Env['CM_SHELF_READER_ENDPOINT'])"
        Assert-Equal 2 $global:CmDetachedStarts.Count 'downloaded once'
    }
}

Test-Case 'shelf reader: a download with the wrong hash is deleted, logged as an alert and leaves the reader off, and is not fetched again at once' {
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2 }
        Add-TestReaderFile $root $script:ReaderModel | Out-Null
        $partial = Add-TestReaderFile $root $script:ReaderProjector -Partial -Wrong
        $r = Invoke-TestReaderPass $root
        Assert-Equal 'up-to-date' $r.Result.Outcome
        Assert-Equal 0 $r.Result.ExitCode 'a bad download never fails the poll'
        Assert-False (Test-Path -LiteralPath $partial) 'the bad download is deleted'
        Assert-False (Test-Path -LiteralPath (Join-Path $root "models/$($script:ReaderProjector)")) 'and never renamed into place'
        Assert-Match $r.Log 'ERROR ALERT shelf-reader '
        Assert-Match $r.Log ([regex]::Escape($script:ReaderProjector))
        Assert-Match $r.Log 'SHA-256'
        Assert-Equal 0 (Get-ReaderUps).Count
        Assert-Equal 0 (Get-ApiSwitches).Count
        Assert-Equal 0 $global:CmDetachedStarts.Count 'not fetched again in the same pass'
        Invoke-TestReaderPass $root | Out-Null
        Assert-Equal 0 $global:CmDetachedStarts.Count 'nor on the next poll: a bad file waits a day before another 6 GB'
        Assert-Equal 1 (Get-ReaderAlerts).Count 'one alert issue'
        Assert-Equal 0 (Get-Calls 'curl' '*/fail*').Count 'the backup heartbeat is not pinged as failed for the reader'
    }
}

Test-Case 'shelf reader: not enough free disk space starts no download' {
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2 }
        $global:CmFreeBytes = { param($Path) [long]1GB }
        $r = Invoke-TestReaderPass $root
        Assert-Equal 0 $global:CmDetachedStarts.Count
        Assert-Match $r.Log 'ALERT shelf-reader .*free'
    }
}

# ---- the deployer: the reader never takes the app down ----------------------------------------------------------------------

Test-Case 'shelf reader: a reader that fails to start is logged as an alert, never rolls the api back and never restarts the api' {
    foreach ($case in @(@{ ReaderUpFails = $true }, @{ ReaderPullFails = $true }, @{ ReaderStartsAs = 'restarting' })) {
        $global:CmCalls.Clear()
        Use-TestReaderRoot -State (Get-OldGoodState) {
            param($root)
            Start-TestReaderMachine (@{ Gpu = $true } + $case)
            Add-TestReaderFiles $root
            $what = ($case.Keys -join ',')
            $deploy = Invoke-TestReaderPass $root
            Assert-Equal 'deployed' $deploy.Result.Outcome $what
            $log = ''
            for ($i = 0; $i -lt 2; $i++) { $p = Invoke-TestReaderPass $root; $log += $p.Log; Assert-Equal 0 $p.Result.ExitCode $what }
            Assert-Equal $script:Sha2 $p.State.current.sha "the new version stays ($what)"
            Assert-False $p.State.ContainsKey('failedSha') "nothing is blamed ($what)"
            Assert-Equal 1 (Get-StackUps).Count "no rollback up ($what)"
            Assert-Equal $script:Sha2 $global:CmMachine.Up $what
            Assert-Equal 0 (Get-ApiSwitches).Count "the api is never restarted ($what)"
            Assert-Match $log 'ERROR ALERT shelf-reader '
            Assert-False ($log -match 'ALERT deploy') "no deploy alert ($what)"
            Assert-False ([bool]$p.State.shelfReader.on) $what
            Assert-Equal 0 (Get-Calls 'curl' '*/fail*').Count "the backup heartbeat is not pinged as failed ($what)"
            $alerts = Get-ReaderAlerts
            Assert-Equal 1 $alerts.Count $what
            Assert-Equal 'agent-task,alert,p2,type:infra' (@($alerts[0].labels | Sort-Object) -join ',') "agent work, not the owner's ($what)"
            $global:CmCalls.Clear()
            Invoke-TestReaderPass $root | Out-Null
            Assert-Equal 0 (Get-ReaderUps).Count "no retry on the next poll ($what)"
            Assert-Equal 0 (Get-Calls 'docker' 'docker pull*llama.cpp*').Count "no pull retry on the next poll ($what)"
        }
    }
}

Test-Case 'shelf reader: an unhealthy or starting reader inside the 10-minute start window gets no endpoint and no alert, and fails after it' {
    foreach ($as in 'unhealthy', 'starting') {
        $global:CmCalls.Clear()
        Use-TestReaderRoot -State (Get-UpToDateState) {
            param($root)
            Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2; ReaderStartsAs = $as }
            Add-TestReaderFiles $root
            $t0 = [DateTime]::UtcNow
            $global:CmNow = $t0
            Invoke-TestReaderPass $root | Out-Null
            Assert-Equal 1 (Get-ReaderUps).Count $as
            $log = ''
            foreach ($minutes in 2, 5, 9) { $global:CmNow = $t0.AddMinutes($minutes); $log += (Invoke-TestReaderPass $root).Log }
            Assert-False ($log -match 'ALERT') "no alert inside the window ($as): $log"
            Assert-Equal 0 (Get-ApiSwitches).Count "no endpoint while it loads ($as)"
            Assert-Equal 0 (Get-Calls 'docker' '*stop shelf-reader*').Count "not stopped ($as)"
            $global:CmNow = $t0.AddMinutes(11)
            $late = Invoke-TestReaderPass $root
            Assert-Match $late.Log 'ERROR ALERT shelf-reader .*within 10 minutes'
            Assert-Equal 1 (Get-Calls 'docker' '*--profile gpu*stop shelf-reader*').Count "stopped after the window ($as)"
            Assert-Equal 0 (Get-ApiSwitches).Count "and the api is left alone ($as)"
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

Test-Case 'shelf reader: an api that fails its smoke test with the endpoint is restarted once without it, and the reader is stopped' {
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2; ApiUnhealthyWithReader = $true }
        Add-TestReaderFiles $root
        Invoke-TestReaderPass $root | Out-Null
        $r = Invoke-TestReaderPass $root
        Assert-Equal 'up-to-date' $r.Result.Outcome
        $switches = Get-ApiSwitches
        Assert-Equal 2 $switches.Count 'on, then restored'
        Assert-Equal $script:ReaderUrl "$($switches[0].Env['CM_SHELF_READER_ENDPOINT'])"
        Assert-Equal '' "$($switches[1].Env['CM_SHELF_READER_ENDPOINT'])"
        Assert-False ([bool]$r.State.shelfReader.on)
        Assert-Match $r.Log 'ERROR ALERT shelf-reader '
        Assert-False ($r.Log -match 'ALERT api') 'the restored api answers, so no api alert'
        Assert-Equal 1 (Get-Calls 'docker' '*--profile gpu*stop shelf-reader*').Count 'the reader is stopped, freeing the card'
    }
}

Test-Case 'shelf reader: an api that still fails after the restore raises an agent-fixable api alert' {
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2; UnhealthyTags = @($script:Sha2) }
        Add-TestReaderFiles $root
        Invoke-TestReaderPass $root | Out-Null
        $r = Invoke-TestReaderPass $root
        Assert-Equal 0 $r.Result.ExitCode
        Assert-Equal 2 (Get-ApiSwitches).Count
        Assert-Match $r.Log 'ERROR ALERT api '
        $api = Get-ReaderAlerts 'shelf-reader-api-down'
        Assert-Equal 1 $api.Count
        Assert-Equal (@((Get-CmAlertKinds)['smoke-failed'].Labels) -join ',') (@($api[0].labels) -join ',') 'labelled like smoke-failed'
        Invoke-TestReaderPass $root | Out-Null
        Assert-Equal 'open' (Get-ReaderAlerts 'shelf-reader-api-down')[0].state 'a quiet poll proves nothing about the api, so it stays open'
    }
}

Test-Case 'shelf reader: api restarts from the reader are capped at 4 a day' {
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2; ApiUnhealthyWithReader = $true }
        Add-TestReaderFiles $root
        $day = [DateTime]::new(2026, 11, 12, 0, 30, 0, [DateTimeKind]::Utc)
        foreach ($cycle in 0..3) {
            $global:CmNow = $day.AddHours(6 * $cycle).AddMinutes($cycle)
            for ($i = 0; $i -lt 3; $i++) { Invoke-TestReaderPass $root | Out-Null }
        }
        Assert-Equal 4 $script:CmShelfReaderApiRestartsPerDay
        Assert-Equal 4 (Get-ApiSwitches).Count 'four tries six hours apart would be eight restarts without the cap'
    }
}

Test-Case 'shelf reader: a reader that dies while on is stopped after ten minutes, and the api is left alone' {
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2 }
        Add-TestReaderFiles $root
        $global:CmNow = [DateTime]::UtcNow
        Invoke-TestReaderUntilOn $root | Out-Null
        $global:CmMachine.ReaderHealth = 'unhealthy'
        $global:CmCalls.Clear()
        $down = Invoke-TestReaderPass $root
        Assert-True ([bool]$down.State.shelfReader.on) 'a reader that just went down is given time'
        $global:CmNow = ([DateTime]$global:CmNow).AddMinutes(11)
        $late = Invoke-TestReaderPass $root
        Assert-Equal 'up-to-date' $late.Result.Outcome
        Assert-Equal 0 (Get-ApiSwitches).Count 'the api is never restarted to switch the reader off'
        Assert-Equal 1 (Get-Calls 'docker' '*--profile gpu*stop shelf-reader*').Count
        Assert-False ([bool]$late.State.shelfReader.on)
        Assert-Match $late.Log 'ERROR ALERT shelf-reader '
        # The next deploy brings the api up without the endpoint, because the state says off.
        $sha3 = 'c3' * 20
        $global:CmMachine.Sha = $sha3; $global:CmMachine.Graph[$sha3] = @($script:Sha1, $script:Sha2)
        $global:CmCalls.Clear()
        Assert-Equal 'deployed' (Invoke-TestReaderPass $root).Result.Outcome
        Assert-Equal '' "$((Get-StackUps)[0].Env['CM_SHELF_READER_ENDPOINT'])"
    }
}

Test-Case 'shelf reader: switching it off in .env, or a model file going missing, stops the reader and leaves the api alone' {
    foreach ($how in 'switch', 'file') {
        $global:CmCalls.Clear()
        Use-TestReaderRoot -State (Get-UpToDateState) {
            param($root)
            Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2 }
            Add-TestReaderFiles $root
            Invoke-TestReaderUntilOn $root | Out-Null
            if ($how -eq 'switch') { Set-Content -LiteralPath (Join-Path $root '.env') -Value (Get-TestReaderEnv @('CM_SHELF_READER=0')) }
            else { Remove-TestReaderFile $root $script:ReaderModel }
            $global:CmCalls.Clear()
            $r = Invoke-TestReaderPass $root
            Assert-Equal 'up-to-date' $r.Result.Outcome $how
            Assert-Equal 0 (Get-ApiSwitches).Count "no api restart ($how)"
            Assert-Equal 1 (Get-Calls 'docker' '*--profile gpu*stop shelf-reader*').Count $how
            Assert-False ([bool]$r.State.shelfReader.on) $how
        }
    }
}

Test-Case 'shelf reader: a flapping card check while on causes no api restart and no stop; fifteen misses in a row stop the reader' {
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2 }
        Add-TestReaderFiles $root
        Invoke-TestReaderUntilOn $root | Out-Null
        $global:CmCalls.Clear()
        foreach ($i in 1..30) {
            $global:CmMachine.Gpu = if ($i % 3 -eq 0) { $true } elseif ($i % 3 -eq 1) { $false } else { 'empty' }
            Invoke-TestReaderPass $root | Out-Null
        }
        Assert-Equal 0 (Get-ApiSwitches).Count 'zero api restarts'
        Assert-Equal 0 (Get-Calls 'docker' '*stop shelf-reader*').Count 'never stopped'
        Assert-True ([bool](Get-TestState -Root $root).shelfReader.on)
        $global:CmMachine.Gpu = $false
        foreach ($i in 1..14) { Invoke-TestReaderPass $root | Out-Null }
        Assert-Equal 0 (Get-Calls 'docker' '*stop shelf-reader*').Count '14 misses in a row are still not "no card"'
        $last = Invoke-TestReaderPass $root
        Assert-Match $last.Log 'ERROR ALERT shelf-reader nvidia-smi has not listed a graphics card for 15 polls'
        Assert-Equal 1 (Get-Calls 'docker' '*--profile gpu*stop shelf-reader*').Count
        Assert-Equal 0 (Get-ApiSwitches).Count 'and still no api restart'
        Assert-False ([bool]$last.State.shelfReader.on)
    }
}

Test-Case 'shelf reader: a fake call that throws inside the step never escapes and never changes the pass''s outcome' {
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2; ReaderThrows = $true }
        Add-TestReaderFiles $root
        $r = Invoke-TestReaderPass $root
        Assert-Equal 'up-to-date' $r.Result.Outcome
        Assert-Equal 0 $r.Result.ExitCode
        Assert-Match $r.Log 'WARN  the shelf reader step failed \(docker compose exploded\)'
        $global:CmMachine.ReaderThrows = $false
        $global:CmStartDetached = { param($File, $ArgumentList) throw 'no curl here' }
        Remove-TestReaderFile $root $script:ReaderModel
        $r = Invoke-TestReaderPass $root
        Assert-Equal 'up-to-date' $r.Result.Outcome
        Assert-Equal 0 $r.Result.ExitCode
    }
}

Test-Case 'shelf reader: a state file that cannot be read during the step is left byte for byte as it was' {
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $false; Up = $script:Sha2 }
        $statePath = Join-Path $root 'state/deployer-state.json'
        $bytes = [IO.File]::ReadAllBytes($statePath)
        $inner = $global:CmRunner
        $global:CmTestLock = $null
        $global:CmRunner = {
            param($Command, $Arguments, $Env, $Mutating, $InputText, $TimeoutSeconds)
            if ($Command -eq 'nvidia-smi' -and -not $global:CmTestLock) { $global:CmTestLock = [IO.File]::Open($statePath, 'Open', 'Read', 'None') }
            & $inner $Command $Arguments $Env $Mutating $InputText $TimeoutSeconds
        }.GetNewClosure()
        Initialize-Cm -Apply $true
        $ctx = New-TestContext -Root $root
        try { Invoke-CmShelfReader -Ctx $ctx 6>$null }
        finally { if ($global:CmTestLock) { $global:CmTestLock.Dispose(); $global:CmTestLock = $null } }
        Assert-Equal 1 (Get-Calls 'nvidia-smi').Count 'the step got as far as the card (it had something to save)'
        Assert-Equal ([Convert]::ToBase64String($bytes)) ([Convert]::ToBase64String([IO.File]::ReadAllBytes($statePath))) 'the state file is untouched'
        # And without the lock the same step does save its note, so the check above proved something.
        $global:CmRunner = $inner
        Invoke-CmShelfReader -Ctx $ctx 6>$null
        Assert-Match ((Get-TestState -Root $root).shelfReader.note) 'no NVIDIA'
        Assert-Equal $script:Sha2 (Get-TestState -Root $root).current.sha
    }
}

Test-Case 'shelf reader: a state with no deployed commit is never written by the step' {
    Use-TestReaderRoot -State @{ alerts = @{} } {
        param($root)
        Start-TestReaderMachine @{ Gpu = $false }
        $statePath = Join-Path $root 'state/deployer-state.json'
        $bytes = [IO.File]::ReadAllBytes($statePath)
        Initialize-Cm -Apply $true
        Invoke-CmShelfReader -Ctx (New-TestContext -Root $root) 6>$null
        Assert-Equal ([Convert]::ToBase64String($bytes)) ([Convert]::ToBase64String([IO.File]::ReadAllBytes($statePath)))
        Assert-Equal 0 (Get-Calls 'nvidia-smi').Count
    }
}

Test-Case 'shelf reader: every call the step makes has a timeout, and one pass fits well inside the stale-lock limit' {
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2; ApiUnhealthyWithReader = $true }
        Add-TestReaderFiles $root
        foreach ($i in 1..3) { Invoke-TestReaderPass $root | Out-Null }
        $mine = @($global:CmCalls | Where-Object {
                $_.Command -eq 'nvidia-smi' -or ($_.Command -eq 'docker' -and $_.Display -match 'shelf-reader|llama\.cpp|--no-deps api|ps -q api|\.Config\.Env|docker logs |\.State\.StartedAt|config --hash')
            })
        Assert-True ($mine.Count -ge 10) "the step made its calls ($($mine.Count))"
        foreach ($kind in 'docker logs ', '\.State\.StartedAt', 'config --hash') { Assert-True (@($mine | Where-Object { $_.Display -match $kind }).Count -ge 1) "the step read $kind" }
        foreach ($c in $mine) { Assert-True ($c.TimeoutSeconds -gt 0) "no timeout on: $($c.Display)" }
        Assert-True (@($mine | Where-Object { $_.Display -match 'docker pull' })[0].TimeoutSeconds -le 600) 'the pull is capped at 10 minutes'
        $worst = Get-CmShelfReaderWorstCaseSeconds -Ctx (New-CmContext -Root $root)
        Assert-True ($worst -le 20 * 60) "worst case $worst s is within 20 minutes"
        Assert-True ($worst + $script:CmPruneSecondsPerPoll -lt $script:CmStaleLockMinutes * 60 - 300) 'with the quiet poll''s clean-up, well under the stale-lock alert'
    }
}

Test-Case 'shelf reader: a dry run starts, downloads and changes nothing' {
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2 }
        Initialize-Cm -Apply $false
        $r = Invoke-CmDeploy -Ctx (New-TestContext -Root $root) 6>$null
        Assert-Equal 'dry-run' $r.Outcome
        Assert-Equal 0 $global:CmDetachedStarts.Count
        Assert-Equal 0 (Get-ReaderUps).Count
        Assert-True ((Get-CmPlan -Ctx (New-TestContext -Root $root)) -join "`n" -match 'shelf') 'the plan describes the reader'
    }
}

# ---- the deployer: the card inside Docker, and a changed definition (#802) ---------------------------------------------------

function Get-ReaderLogReads { @(Get-Calls 'docker' '*docker logs *') }
function Get-ReaderStops { @(Get-Calls 'docker' '*--profile gpu*stop shelf-reader*') }
function Get-ReaderOnState {
    <# The deployer's state with the reader on, as a pass before #802 left it (no record of a checked container start). #>
    $s = Get-UpToDateState
    $s['shelfReader'] = @{ on = $true; note = 'on'; startedAt = 1762934400; downSince = 0; retryAt = 0; cardMisses = 0; files = @{}; downloads = @{} }
    $s
}
function Get-ReaderStateJson { param([string]$Root) ConvertTo-CmCanonicalJson (Get-TestState -Root $Root)['shelfReader'] }
function Get-ReaderDockerMutations {
    <# Mutating docker calls other than the poll's own look for a new app image (docker pull of the main tag, every poll). #>
    @($global:CmCalls | Where-Object { $_.Command -eq 'docker' -and $_.Mutating -and $_.Display -notmatch '^docker pull --quiet \S*collectionmaxxing-\w+:main$' })
}

Test-Case 'shelf reader: the PC as #802 found it (on, healthy, on the processor, the api holding the endpoint) ends recreated, checked and on, with no api restart and no alert' {
    Use-TestReaderRoot -State (Get-ReaderOnState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2; ReaderHealth = 'healthy'; ApiEndpoint = $script:ReaderUrl
            ReaderLabelHash = 'cfg-before-802'; ComposeHash = 'cfg-after-802'; ReaderLog = @(Get-TestReaderCpuLog); ReaderStartsAs = 'starting' }
        Add-TestReaderFiles $root
        $first = Invoke-TestReaderPass $root
        Assert-Equal 'up-to-date' $first.Result.Outcome
        $ups = Get-ReaderUps
        Assert-Equal 1 $ups.Count 'the running reader no longer matches the compose file, so it is recreated'
        Assert-Equal $script:Sha2 "$($ups[0].Env['CM_TAG'])" 'at the deployed commit'
        Assert-Equal $script:ReaderUrl "$($ups[0].Env['CM_SHELF_READER_ENDPOINT'])" 'and the endpoint the api already has'
        Assert-Match $ups[0].ComposeFile '\.reader\.\d+\.yml$' 'from the step''s copy of the compose file at that commit'
        Assert-Equal 0 (Get-ReaderLogReads).Count 'the old container''s log is not judged: it is replaced anyway'
        Assert-Equal 0 (Get-ReaderStops).Count 'not stopped'
        Assert-True ([bool]$first.State.shelfReader.on) 'the state stays on while it reloads'
        Assert-Equal 'cfg-after-802' $global:CmMachine.ReaderLabelHash
        $global:CmMachine.ReaderHealth = 'healthy'
        $second = Invoke-TestReaderPass $root
        Assert-Equal 1 (Get-ReaderLogReads).Count 'the new container''s start log is read once it is healthy'
        Assert-Match (Get-ReaderLogReads)[0].Display ([regex]::Escape($global:CmMachine.ReaderId))
        Assert-True ([bool]$second.State.shelfReader.on)
        Assert-Equal 'on' "$($second.State.shelfReader.note)"
        Assert-Equal 1 (Get-ReaderUps).Count 'recreated once'
        Assert-Equal 0 (Get-ApiSwitches).Count 'the api keeps its endpoint and is not restarted'
        Assert-Equal 0 (Get-ReaderAlerts).Count 'no alert'
        Assert-False (($first.Log + $second.Log) -match 'ALERT') 'no ALERT line'
        Assert-Equal $script:ReaderUrl $global:CmMachine.ApiEndpoint
        # And from then on a quiet poll is quiet: no mutating docker call, no log read, no state change.
        $global:CmCalls.Clear()
        $before = Get-ReaderStateJson $root
        foreach ($i in 1..3) { Invoke-TestReaderPass $root | Out-Null }
        Assert-Equal 0 (Get-ReaderDockerMutations).Count "no mutating docker call: $((Get-ReaderDockerMutations | ForEach-Object Display) -join '; ')"
        Assert-Equal 0 (Get-ReaderLogReads).Count 'the checked start is not read again'
        Assert-Equal $before (Get-ReaderStateJson $root) 'the reader''s state is not rewritten'
    }
}

Test-Case 'shelf reader: a reader whose start log says the card is not usable never gets the endpoint, is stopped, and raises the alert once' {
    foreach ($log in @(@(Get-TestReaderCpuLog), @('warning: no usable GPU found, --gpu-layers option will be ignored'), @('ggml_cuda_init: failed to initialize CUDA: named symbol not found'))) {
        $global:CmCalls.Clear()
        $what = $log -join ' / '
        Use-TestReaderRoot -State (Get-UpToDateState) {
            param($root)
            Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2; ReaderLogOnStart = $log }
            Add-TestReaderFiles $root
            $t0 = [DateTime]::UtcNow
            $global:CmNow = $t0
            Invoke-TestReaderPass $root | Out-Null
            Assert-Equal 1 (Get-ReaderUps).Count $what
            $global:CmNow = $t0.AddMinutes(2)
            $r = Invoke-TestReaderPass $root
            Assert-Equal 'up-to-date' $r.Result.Outcome $what
            Assert-Equal 0 $r.Result.ExitCode $what
            Assert-Equal 1 (Get-ReaderLogReads).Count $what
            Assert-True ((Get-ReaderLogReads)[0].TimeoutSeconds -gt 0) "the log read has a timeout ($what)"
            Assert-Equal 0 (Get-ApiSwitches).Count "the endpoint is not handed over ($what)"
            Assert-Equal '' "$($global:CmMachine.ApiEndpoint)" $what
            Assert-False ([bool]$r.State.shelfReader.on) $what
            Assert-Equal 1 (Get-ReaderStops).Count "stopped, freeing the card ($what)"
            Assert-Match $r.Log 'ERROR ALERT shelf-reader the reader started but cannot use the graphics card inside Docker' $what
            Assert-False ($r.Log -match 'ALERT deploy') $what
            Assert-Equal 1 (Get-ReaderAlerts).Count "one alert issue ($what)"
            Assert-Equal $script:Sha2 $r.State.current.sha 'nothing rolled back'
            $global:CmCalls.Clear()
            foreach ($minutes in 4, 60, 300) { $global:CmNow = $t0.AddMinutes($minutes); Invoke-TestReaderPass $root | Out-Null }
            Assert-Equal 0 (Get-ReaderUps).Count "it waits the usual 6 hours ($what)"
            Assert-Equal 0 (Get-ReaderLogReads).Count $what
            Assert-Equal 1 @(Get-ReaderAlerts | Where-Object { $_.state -eq 'open' }).Count "still one alert ($what)"
            $global:CmNow = $t0.AddHours(6).AddMinutes(5)
            Invoke-TestReaderPass $root | Out-Null
            Assert-Equal 1 (Get-ReaderUps).Count "then it tries again ($what)"
            $global:CmNow = $t0.AddHours(6).AddMinutes(7)
            $again = Invoke-TestReaderPass $root
            Assert-Equal 0 (Get-ApiSwitches).Count "and still no endpoint ($what)"
            Assert-False ([bool]$again.State.shelfReader.on) $what
            Assert-Equal 1 @(Get-ReaderAlerts).Count "the same alert issue, not a second one ($what)"
        }
    }
}

Test-Case 'shelf reader: a start log that cannot be read is tried again on the next poll, neither a failure nor a pass' {
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2; ReaderLogFails = $true }
        Add-TestReaderFiles $root
        Invoke-TestReaderPass $root | Out-Null
        foreach ($i in 1..2) {
            $r = Invoke-TestReaderPass $root
            Assert-Equal 0 (Get-ApiSwitches).Count "no endpoint while the log is unreadable (poll $i)"
            Assert-False ([bool]$r.State.shelfReader.on) "not on (poll $i)"
            Assert-Equal 0 (Get-ReaderStops).Count "not stopped (poll $i)"
            Assert-False ($r.Log -match 'ALERT') "no alert (poll $i)"
        }
        Assert-Equal 2 (Get-ReaderLogReads).Count 'tried on each poll'
        $global:CmMachine.ReaderLogFails = $false
        $on = Invoke-TestReaderPass $root
        Assert-True ([bool]$on.State.shelfReader.on) 'readable and clean: on'
        Assert-Equal 1 (Get-ApiSwitches).Count
    }
}

Test-Case 'shelf reader: the start log is read once per container start, and a restarted container is checked again' {
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2 }
        Add-TestReaderFiles $root
        Invoke-TestReaderUntilOn $root | Out-Null
        Assert-Equal 1 (Get-ReaderLogReads).Count 'read when it first turned healthy'
        $global:CmCalls.Clear()
        foreach ($i in 1..3) { Invoke-TestReaderPass $root | Out-Null }
        Assert-Equal 0 (Get-ReaderLogReads).Count 'not on later polls'
        # Docker restarted the same container (restart: unless-stopped): a new start, and this time CUDA did not come up.
        $global:CmMachine.ReaderStartedAt = '2026-11-12T09:30:00.5Z'
        $global:CmMachine.ReaderLog = @(Get-TestReaderCpuLog)
        $global:CmCalls.Clear()
        $r = Invoke-TestReaderPass $root
        Assert-Equal 1 (Get-ReaderLogReads).Count 'the new start is checked'
        Assert-False ([bool]$r.State.shelfReader.on)
        Assert-Equal 1 (Get-ReaderStops).Count
        Assert-Equal 0 (Get-ApiSwitches).Count 'switching it off never restarts the api'
        Assert-Match $r.Log 'cannot use the graphics card inside Docker'
    }
}

Test-Case 'shelf reader: a changed reader definition recreates the running reader once, with the start window, and the api keeps its endpoint' {
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2 }
        Add-TestReaderFiles $root
        $t0 = [DateTime]::UtcNow
        $global:CmNow = $t0
        Invoke-TestReaderUntilOn $root | Out-Null
        $switches = (Get-ApiSwitches).Count
        $global:CmMachine.ComposeHash = 'cfg-hash-2'
        $global:CmMachine.ReaderStartsAs = 'unhealthy'
        $global:CmCalls.Clear()
        $r = Invoke-TestReaderPass $root
        Assert-Equal 1 (Get-ReaderUps).Count 'recreated'
        Assert-Equal $script:ReaderUrl "$((Get-ReaderUps)[0].Env['CM_SHELF_READER_ENDPOINT'])"
        Assert-Match $r.Log 'shelf reader: .*recreated'
        Assert-True ([bool]$r.State.shelfReader.on)
        foreach ($minutes in 3, 6, 9) {
            $global:CmNow = $t0.AddMinutes($minutes)
            $p = Invoke-TestReaderPass $root
            Assert-False ($p.Log -match 'ALERT') "loading inside the start window is no failure ($minutes min)"
        }
        Assert-Equal 1 (Get-ReaderUps).Count 'not recreated again while it loads'
        Assert-Equal 0 (Get-ReaderStops).Count
        $global:CmMachine.ReaderHealth = 'healthy'
        $global:CmNow = $t0.AddMinutes(10)
        $on = Invoke-TestReaderPass $root
        Assert-True ([bool]$on.State.shelfReader.on)
        Assert-Equal 1 (Get-ReaderLogReads).Count 'the recreated container is checked'
        Assert-Equal 0 (Get-ApiSwitches).Count 'no api restart: its endpoint did not change'
    }
}

Test-Case 'shelf reader: an unchanged definition is never recreated, and an unknown hash or start does nothing that poll' {
    foreach ($how in 'same', 'hash', 'inspect', 'label') {
        $global:CmCalls.Clear()
        Use-TestReaderRoot -State (Get-UpToDateState) {
            param($root)
            Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2 }
            Add-TestReaderFiles $root
            Invoke-TestReaderUntilOn $root | Out-Null
            if ($how -ne 'same') { $global:CmMachine.ComposeHash = 'cfg-hash-2' }
            switch ($how) {
                'hash' { $global:CmMachine.ComposeHashFails = $true }
                'inspect' { $global:CmMachine.ReaderInspectFails = $true }
                'label' { $global:CmMachine.ReaderLabelHash = '' }
            }
            $global:CmCalls.Clear()
            foreach ($i in 1..3) { $r = Invoke-TestReaderPass $root }
            Assert-Equal 0 (Get-ReaderUps).Count "no recreation ($how)"
            Assert-Equal 0 (Get-ReaderDockerMutations).Count "no mutating docker call ($how)"
            Assert-Equal 0 (Get-ReaderLogReads).Count "no log read ($how)"
            Assert-True ([bool]$r.State.shelfReader.on) "still on ($how)"
            Assert-Equal 0 (Get-ApiSwitches).Count $how
            if ($how -ne 'same') {
                # compose's hash is only asked for once the container's own can be read.
                Assert-Equal ($(if ($how -eq 'hash') { 3 } else { 0 })) @(Get-Calls 'docker' '*config --hash shelf-reader*').Count "compose's hash ($how)"
                $global:CmMachine.ComposeHashFails = $false; $global:CmMachine.ReaderInspectFails = $false
                if ($how -eq 'label') { $global:CmMachine.ReaderLabelHash = 'cfg-hash-1' }
                Invoke-TestReaderPass $root | Out-Null
                Assert-Equal 1 (Get-ReaderUps).Count "recreated once both can be read ($how)"
            }
        }
    }
}

Test-Case 'shelf reader: a recreation that does not change the container''s hash is not repeated on every poll' {
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2 }
        Add-TestReaderFiles $root
        Invoke-TestReaderUntilOn $root | Out-Null
        $global:CmMachine.ComposeHash = 'cfg-hash-2'
        $global:CmMachine.ReaderUpKeepsHash = $true
        $global:CmCalls.Clear()
        foreach ($i in 1..4) { $r = Invoke-TestReaderPass $root }
        Assert-Equal 1 (Get-ReaderUps).Count 'one try for this definition'
        Assert-True ([bool]$r.State.shelfReader.on)
        Assert-Equal 0 (Get-ApiSwitches).Count
        $global:CmMachine.ComposeHash = 'cfg-hash-3'
        Invoke-TestReaderPass $root | Out-Null
        Assert-Equal 2 (Get-ReaderUps).Count 'a newer definition is tried again'
    }
}

Test-Case 'shelf reader: a recreation that fails is a reader failure, never a deploy failure, and the api is left alone' {
    Use-TestReaderRoot -State (Get-UpToDateState) {
        param($root)
        Start-TestReaderMachine @{ Gpu = $true; Up = $script:Sha2 }
        Add-TestReaderFiles $root
        Invoke-TestReaderUntilOn $root | Out-Null
        $switches = (Get-ApiSwitches).Count
        $global:CmMachine.ComposeHash = 'cfg-hash-2'
        $global:CmMachine.ReaderUpFails = $true
        $r = Invoke-TestReaderPass $root
        Assert-Equal 'up-to-date' $r.Result.Outcome
        Assert-Equal 0 $r.Result.ExitCode
        Assert-False ([bool]$r.State.shelfReader.on)
        Assert-Match $r.Log 'ERROR ALERT shelf-reader '
        Assert-Equal $switches (Get-ApiSwitches).Count
        Assert-Equal $script:Sha2 $r.State.current.sha
    }
}
