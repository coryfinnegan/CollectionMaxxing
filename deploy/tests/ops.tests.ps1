# Tests for Set-TailscaleServe, Install/Uninstall-Deployer, Sync-Ipa and the compose file.

$script:TsScript = Join-Path $PSScriptRoot '../tailscale/Set-TailscaleServe.ps1'
$script:InstallScript = Join-Path $PSScriptRoot '../deployer/Install-Deployer.ps1'
$script:UninstallScript = Join-Path $PSScriptRoot '../deployer/Uninstall-Deployer.ps1'

function Set-FakeTailscale {
    <# Installs a fake machine whose `tailscale serve status --json` says $StatusJson. #>
    param([string]$StatusJson, [int]$StatusExit = 0)
    $global:CmTsStatus = $StatusJson
    $global:CmTsExit = $StatusExit
    $global:CmRunner = {
        param($Command, $Arguments, $Env, $Mutating)
        $display = Format-CmCommand -Command $Command -Arguments $Arguments -Env $Env
        $global:CmCalls.Add([pscustomobject]@{ Command = $Command; Arguments = $Arguments; Env = $Env; Mutating = $Mutating; Display = $display })
        if ($Command -eq 'tailscale' -and ($Arguments -join ' ') -eq 'serve status --json') {
            return [pscustomobject]@{ ExitCode = $global:CmTsExit; Output = @($global:CmTsStatus) }
        }
        [pscustomobject]@{ ExitCode = 0; Output = @() }
    }
}

$script:OurServe = '{"TCP":{"443":{"HTTPS":true}},"Web":{"pc.tailnet.ts.net:443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:5480"}}}}}'

Test-Case 'tailscale: -WhatIf prints the exact command and runs nothing that changes' {
    Set-FakeTailscale '{}'
    $out = (& $script:TsScript -WhatIf 6>&1 | Out-String)
    Assert-Equal 0 $LASTEXITCODE
    Assert-Match $out ([regex]::Escape('tailscale serve --bg --https=443 http://127.0.0.1:5480'))
    Assert-Equal 0 (Get-MutatingCalls).Count
    Assert-Equal 1 $global:CmCalls.Count 'only `tailscale serve status` ran'
    Assert-Equal 'serve status --json' ($global:CmCalls[0].Arguments -join ' ')
}

Test-Case 'tailscale: with no -Apply it is also a dry run' {
    Set-FakeTailscale ''
    & $script:TsScript 6>$null
    Assert-Equal 0 $LASTEXITCODE
    Assert-Equal 0 (Get-MutatingCalls).Count
}

Test-Case 'tailscale: an existing foreign serve config is not overwritten' {
    Set-FakeTailscale '{"TCP":{"443":{"HTTPS":true}},"Web":{"pc.tailnet.ts.net:443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:8096"}}}}}'
    & $script:TsScript -Apply 6>$null
    Assert-Equal 4 $LASTEXITCODE
    Assert-Equal 0 (Get-MutatingCalls).Count
    Assert-Equal 0 (Get-Calls 'tailscale' '*--bg*').Count
}

Test-Case 'tailscale: a second site on the same name counts as foreign, and so does anything with Funnel on' {
    Set-FakeTailscale '{"TCP":{"443":{"HTTPS":true},"8443":{"HTTPS":true}},"Web":{"a.ts.net:443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:5480"}}},"a.ts.net:8443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:8096"}}}}}'
    & $script:TsScript -Apply 6>$null
    Assert-Equal 4 $LASTEXITCODE
}

Test-Case 'tailscale: Funnel on for our own proxy is foreign (valid JSON, only the Funnel guard can refuse it)' {
    $o = $script:OurServe | ConvertFrom-Json -AsHashtable
    $o['AllowFunnel'] = @{ 'pc.tailnet.ts.net:443' = $true }
    $json = $o | ConvertTo-Json -Depth 8 -Compress
    Set-FakeTailscale $json
    & $script:TsScript -Apply 6>$null
    Assert-Equal 4 $LASTEXITCODE 'funnel exposes the app to the internet'
    Assert-Equal 0 (Get-MutatingCalls).Count
}

Test-Case 'tailscale: an AllowFunnel key with Funnel off and our proxy is ours' {
    $o = $script:OurServe | ConvertFrom-Json -AsHashtable
    $o['AllowFunnel'] = @{ 'pc.tailnet.ts.net:443' = $false }
    Set-FakeTailscale ($o | ConvertTo-Json -Depth 8 -Compress)
    & $script:TsScript -Apply 6>$null
    Assert-Equal 0 $LASTEXITCODE
    Assert-Equal 0 (Get-MutatingCalls).Count
}

Test-Case 'tailscale: our own config already in place is left alone' {
    Set-FakeTailscale $script:OurServe
    & $script:TsScript -Apply 6>$null
    Assert-Equal 0 $LASTEXITCODE
    Assert-Equal 0 (Get-MutatingCalls).Count
}

Test-Case 'tailscale: -Apply on an empty config runs exactly one command, to 127.0.0.1:5480' {
    Set-FakeTailscale '{}'
    & $script:TsScript -Apply 6>$null
    Assert-Equal 0 $LASTEXITCODE
    $m = Get-MutatingCalls
    Assert-Equal 1 $m.Count
    Assert-Equal 'serve --bg --https=443 http://127.0.0.1:5480' ($m[0].Arguments -join ' ')
}

Test-Case 'tailscale: -Apply -WhatIf together still change nothing' {
    Set-FakeTailscale '{}'
    & $script:TsScript -Apply -WhatIf 6>$null
    Assert-Equal 0 (Get-MutatingCalls).Count
}

Test-Case 'tailscale: a port other apps use is refused before anything runs' {
    foreach ($port in 8080, 8409, 32400, 8096) {
        Set-FakeTailscale '{}'
        & $script:TsScript -Port $port -Apply 6>$null
        Assert-Equal 2 $LASTEXITCODE "port $port"
        Assert-Equal 0 $global:CmCalls.Count
    }
}

Test-Case 'tailscale: an unreadable status stops with exit 3' {
    Set-FakeTailscale '' 1
    & $script:TsScript -Apply 6>$null
    Assert-Equal 3 $LASTEXITCODE
    Assert-Equal 0 (Get-MutatingCalls).Count
}

# ---- scheduled task -------------------------------------------------------------------------------------------------

function Set-FakeSchtasks {
    param([int]$QueryExit = 1)
    $global:CmQueryExit = $QueryExit
    $global:CmRunner = {
        param($Command, $Arguments, $Env, $Mutating)
        $display = Format-CmCommand -Command $Command -Arguments $Arguments -Env $Env
        $global:CmCalls.Add([pscustomobject]@{ Command = $Command; Arguments = $Arguments; Env = $Env; Mutating = $Mutating; Display = $display })
        $code = if ($Arguments[0] -eq '/Query') { $global:CmQueryExit } else { 0 }
        [pscustomobject]@{ ExitCode = $code; Output = @() }
    }
}

Test-Case 'install: without -Apply it prints the exact schtasks command and registers nothing' {
    Set-FakeSchtasks
    $root = New-TestRoot
    try {
        $out = (& $script:InstallScript -Root $root 6>&1 | Out-String)
        Assert-Equal 0 $LASTEXITCODE
        Assert-Match $out '/Create /TN CollectionMaxxingDeployer /SC MINUTE /MO 2 /TR'
        Assert-Match $out 'Deploy-CollectionMaxxing\.ps1'
        Assert-Equal 0 (Get-MutatingCalls).Count
        Assert-NoForbiddenCalls
        Assert-False (Test-Path (Join-Path $root 'logs')) 'no folders are created in a dry run'
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'install: the task starts pwsh through conhost --headless, so no console window flashes every 2 minutes' {
    Set-FakeSchtasks
    $root = New-TestRoot
    try {
        & $script:InstallScript -Root $root -Apply 6>$null
        $args_ = @((Get-MutatingCalls)[0].Arguments)
        $tr = $args_[[array]::IndexOf($args_, '/TR') + 1]
        Assert-Match $tr '^conhost\.exe --headless "[^"]*pwsh(\.exe)?" -NoProfile '
        Assert-Match $tr 'Deploy-CollectionMaxxing\.ps1" -Root "[^"]+" -Apply$'
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'install: -WhatIf is a dry run even with -Apply' {
    Set-FakeSchtasks
    $root = New-TestRoot
    try {
        & $script:InstallScript -Root $root -Apply -WhatIf 6>$null
        Assert-Equal 0 (Get-MutatingCalls).Count
        Assert-NoForbiddenCalls
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'install: -Apply (against the fake) makes one schtasks /Create every 2 minutes and creates logs and state' {
    Set-FakeSchtasks
    $root = New-TestRoot
    try {
        & $script:InstallScript -Root $root -Apply 6>$null
        Assert-Equal 0 $LASTEXITCODE
        $m = Get-MutatingCalls
        Assert-Equal 1 $m.Count
        Assert-Match $m[0].Display '/SC MINUTE /MO 2'
        Assert-Match $m[0].Display ' -Apply'
        Assert-True (Test-Path (Join-Path $root 'logs'))
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'install: it will not overwrite a task that already exists' {
    Set-FakeSchtasks -QueryExit 0
    $root = New-TestRoot
    try {
        & $script:InstallScript -Root $root -Apply 6>$null
        Assert-Equal 1 $LASTEXITCODE
        Assert-Equal 0 (Get-MutatingCalls).Count
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'uninstall: dry run prints the delete and removes nothing; -Apply deletes only our task' {
    Set-FakeSchtasks -QueryExit 0
    & $script:UninstallScript 6>$null
    Assert-Equal 0 (Get-MutatingCalls).Count
    & $script:UninstallScript -Apply 6>$null
    $m = Get-MutatingCalls
    Assert-Equal 1 $m.Count
    Assert-Equal '/Delete /TN CollectionMaxxingDeployer /F' ($m[0].Arguments -join ' ')
}

# ---- signing keys -------------------------------------------------------------------------------------------------------

Test-Case 'secrets: a dry run creates nothing; -Apply makes two loadable PFX files and never overwrites them' {
    $dir = Join-Path ([IO.Path]::GetTempPath()) ("cm-ops1-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $script = Join-Path $PSScriptRoot '../deployer/New-Secrets.ps1'
    try {
        & $script -Path $dir 6>$null
        Assert-False (Test-Path $dir) 'dry run must not create the folder'
        & $script -Path $dir -Apply 6>$null
        foreach ($name in 'openiddict-signing.pfx', 'openiddict-encryption.pfx') {
            $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $dir $name), [string]$null)
            Assert-True $cert.HasPrivateKey "$name has a private key"
        }
        $before = (Get-FileHash (Join-Path $dir 'openiddict-signing.pfx')).Hash
        & $script -Path $dir -Apply 6>$null
        Assert-Equal $before (Get-FileHash (Join-Path $dir 'openiddict-signing.pfx')).Hash 'an existing key is never replaced'
    }
    finally { Remove-TestRoot $dir }
}

# ---- .ipa sync ----------------------------------------------------------------------------------------------------------

function Invoke-TestIpaSync {
    param([hashtable]$Machine = @{}, [hashtable]$State = @{}, [switch]$DryRun)
    $root = New-TestRoot
    try {
        New-FakeMachine $Machine | Out-Null
        Set-TestState -Root $root -State $State
        Initialize-Cm -Apply (-not $DryRun)
        $ctx = New-TestContext -Root $root
        $result = Invoke-CmIpaSync -Ctx $ctx 6>$null
        [pscustomobject]@{ Result = $result; State = (Get-TestState -Root $root) }
    }
    finally { Remove-TestRoot $root }
}

$script:IpaState = { @{ current = @{ sha = $script:Sha1 }; ipa = @{ sha256 = ('0' * 64); file = 'old.ipa' } } }

Test-Case 'ipa sync: a checksum mismatch leaves the previous file in place' {
    $r = Invoke-TestIpaSync -Machine @{ IpaSha256 = ('f' * 64) } -State (& $script:IpaState)
    Assert-Equal 'checksum-mismatch' $r.Result.Outcome
    Assert-Equal 1 $r.Result.ExitCode
    Assert-Equal 0 (Get-Calls 'docker' '*run --rm*').Count 'nothing is copied into the volume'
    Assert-Equal ('0' * 64) $r.State.ipa.sha256 'the recorded current file is unchanged'
}

Test-Case 'ipa sync: a release with no checksum file is not trusted' {
    $r = Invoke-TestIpaSync -Machine @{ IpaSha256 = $null } -State (& $script:IpaState)
    Assert-Equal 'no-checksum' $r.Result.Outcome
    Assert-Equal 0 (Get-Calls 'docker' '*run --rm*').Count
}

Test-Case 'ipa sync: a verified .ipa is copied into the files volume under ipa/ with fixed names' {
    $r = Invoke-TestIpaSync -State (& $script:IpaState)
    Assert-Equal 'synced' $r.Result.Outcome
    $copy = Get-Calls 'docker' '*run --rm*'
    Assert-Equal 1 $copy.Count
    Assert-Match $copy[0].Display 'collectionmaxxing_files:/data/files'
    Assert-Match $copy[0].Display '/data/files/ipa/CollectionMaxxing\.ipa'
    Assert-Match $copy[0].Display 'collectionmaxxing-backup:'
    Assert-True ($r.State.ipa.sha256 -ne ('0' * 64)) 'state records the new checksum'
}

Test-Case 'ipa sync: the same .ipa again is not copied twice' {
    $first = Invoke-TestIpaSync -State (& $script:IpaState)
    $global:CmCalls.Clear()
    $second = Invoke-TestIpaSync -State @{ current = @{ sha = $script:Sha1 }; ipa = @{ sha256 = $first.State.ipa.sha256 } }
    Assert-Equal 'up-to-date' $second.Result.Outcome
    Assert-Equal 0 (Get-Calls 'docker' '*run --rm*').Count
}

Test-Case 'ipa sync: a dry run downloads and copies nothing' {
    $r = Invoke-TestIpaSync -DryRun -State (& $script:IpaState)
    Assert-Equal 'dry-run' $r.Result.Outcome
    Assert-Equal 0 $global:CmCalls.Count
}

Test-Case 'ipa sync script: no -Apply means no calls' {
    $root = New-TestRoot
    try {
        New-FakeMachine @{} | Out-Null
        & (Join-Path $PSScriptRoot '../deployer/Sync-Ipa.ps1') -Root $root 6>$null
        Assert-Equal 0 $LASTEXITCODE
        Assert-Equal 0 $global:CmCalls.Count
    }
    finally { Remove-TestRoot $root }
}

# ---- the compose file (real `docker compose config`, which needs the CLI but no daemon) -------------------------

function Get-RealComposeConfig {
    param([hashtable]$EnvValues = @{})
    $dir = Join-Path ([IO.Path]::GetTempPath()) ("cm-ops1-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    try {
        $envFile = Join-Path $dir '.env'
        $values = @{ POSTGRES_PASSWORD = 'throwaway'; CM_BACKUP_DIR = $dir; CM_TAG = 'test'; CM_RELYING_PARTY_ID = 'pc.tailnet.ts.net'; CM_SECRETS_DIR = $dir } + $EnvValues
        Set-Content -LiteralPath $envFile -Value ($values.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
        $compose = Join-Path $PSScriptRoot '../docker-compose.yml'
        $r = Invoke-CmReal -Command 'docker' -Arguments @('compose', '--env-file', $envFile, '-f', $compose, '-p', 'collectionmaxxing', 'config', '--format', 'json') -Env @{}
        if ($r.ExitCode -ne 0) { throw "docker compose config failed: $($r.Output -join ' ')" }
        ($r.Output -join "`n") | ConvertFrom-Json
    }
    finally { Remove-TestRoot $dir }
}

Test-Case 'compose: the file is valid with a throwaway .env' {
    $config = Get-RealComposeConfig
    foreach ($name in 'postgres', 'migrate', 'api', 'aspire-dashboard', 'backup') {
        Assert-True ($null -ne $config.services.$name) "service $name exists"
    }
}

Test-Case 'compose: the only published ports are 127.0.0.1:5480 and 127.0.0.1:18888' {
    $config = Get-RealComposeConfig
    $published = @($config.services.PSObject.Properties | ForEach-Object {
            foreach ($p in @($_.Value.ports)) { if ($p) { "$($p.host_ip):$($p.published)" } }
        } | Sort-Object)
    Assert-Equal '127.0.0.1:18888 127.0.0.1:5480' ($published -join ' ')
    Assert-Equal 0 @($config.services.postgres.ports | Where-Object { $_ }).Count 'postgres is not published'
    Assert-Equal 0 @(Test-CmConfig -EnvMap @{ CM_API_PORT = '5480'; CM_DASHBOARD_PORT = '18888' } -ComposeConfig $config).Count
}

Test-Case 'compose: the config guard catches a forbidden port set through .env' {
    $config = Get-RealComposeConfig -EnvValues @{ CM_API_PORT = '8080' }
    Assert-True (@(Test-CmConfig -EnvMap @{ CM_API_PORT = '8080' } -ComposeConfig $config).Count -ge 1)
}

Test-Case 'compose: the api listens where the code reads its settings (connection string name, covers folder)' {
    $config = Get-RealComposeConfig
    $envNames = @($config.services.api.environment.PSObject.Properties.Name)
    Assert-True ($envNames -contains 'ConnectionStrings__collectionmaxxing') 'the API reads ConnectionStrings:collectionmaxxing'
    Assert-True ($envNames -contains 'Catalog__CoversRoot') 'the API reads Catalog:CoversRoot'
    Assert-True (@($config.services.migrate.environment.PSObject.Properties.Name) -contains 'ConnectionStrings__collectionmaxxing')
}
