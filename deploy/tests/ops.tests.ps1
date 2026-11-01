# Tests for Set-TailscaleServe, Install/Uninstall-Deployer, the compose file.

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

# ---- the compose file (real `docker compose config`, which needs the CLI but no daemon) -------------------------

function Get-RealComposeConfig {
    param([hashtable]$EnvValues = @{})
    $dir = Join-Path ([IO.Path]::GetTempPath()) ("cm-ops1-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    try {
        $envFile = Join-Path $dir '.env'
        $values = @{ POSTGRES_PASSWORD = 'throwaway'; CM_BACKUP_DIR = $dir; CM_TAG = 'test'; CM_SECRETS_DIR = $dir
        CM_PUBLIC_ORIGIN = 'https://pc.tailnet.ts.net'; CM_AUTH0_DOMAIN = 'test.us.auth0.com'; CM_AUTH0_WEB_CLIENT_ID = 'test-web'
        CM_AUTH0_WEB_CLIENT_SECRET = 'test-secret'; CM_AUTH0_MANAGEMENT_CLIENT_ID = 'test-mgmt'; CM_AUTH0_MANAGEMENT_CLIENT_SECRET = 'test-secret' } + $EnvValues
        Set-Content -LiteralPath $envFile -Value ($values.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
        $compose = Join-Path $PSScriptRoot '../docker-compose.yml'
        $script:LastRealComposeEnv = $values       # for the tests that run the config guard on it
        $script:LastRealComposeFile = (Resolve-Path -LiteralPath $compose).Path
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
    $envMap = @{ CM_API_PORT = '5480'; CM_DASHBOARD_PORT = '18888' } + $script:LastRealComposeEnv
    Assert-Equal 0 @(Test-CmConfig -EnvMap $envMap -ComposeConfig $config -ComposeFile $script:LastRealComposeFile).Count
}

Test-Case 'compose: the current file passes the whole config guard, bind mounts included, and has bind mounts for the guard to check' {
    $config = Get-RealComposeConfig
    $binds = @($config.services.PSObject.Properties | ForEach-Object { foreach ($v in @($_.Value.volumes)) { if ($v -and $v.type -eq 'bind') { $v } } })
    Assert-True ($binds.Count -ge 5) "the real file binds secrets, recognition, evals (2), the baseline and the backup folder, got $($binds.Count)"
    $problems = @(Test-CmConfig -EnvMap $script:LastRealComposeEnv -ComposeConfig $config -ComposeFile $script:LastRealComposeFile)
    Assert-Equal 0 $problems.Count ($problems -join '; ')
    $noEnv = @(Test-CmConfig -EnvMap @{} -ComposeConfig $config -ComposeFile $script:LastRealComposeFile)
    Assert-True ($noEnv.Count -ge 1) 'without the .env folders the guard has nothing to allow the secrets and backup folders with'
}

Test-Case 'compose: a bind mount added to the current file outside the allow-list is caught' {
    $config = Get-RealComposeConfig
    $config.services.api.volumes = @($config.services.api.volumes) + [pscustomobject]@{ type = 'bind'; source = (Join-Path ([IO.Path]::GetPathRoot($script:LastRealComposeFile)) 'Users'); target = '/mnt/home' }
    $problems = @(Test-CmConfig -EnvMap $script:LastRealComposeEnv -ComposeConfig $config -ComposeFile $script:LastRealComposeFile)
    Assert-Equal 1 $problems.Count ($problems -join '; ')
    Assert-Match $problems[0] '/mnt/home'
}

Test-Case 'compose: the config guard catches a forbidden port set through .env' {
    $config = Get-RealComposeConfig -EnvValues @{ CM_API_PORT = '8080' }
    Assert-True (@(Test-CmConfig -EnvMap @{ CM_API_PORT = '8080' } -ComposeConfig $config).Count -ge 1)
}

Test-Case 'compose: the api is given the Open Library contact email from .env, and an empty one when .env has none' {
    $none = Get-RealComposeConfig
    $names = @($none.services.api.environment.PSObject.Properties.Name)
    Assert-True ($names -contains 'Providers__OpenLibrary__ContactEmail') 'the API binds Providers:OpenLibrary:ContactEmail from this variable'
    Assert-Equal '' "$($none.services.api.environment.Providers__OpenLibrary__ContactEmail)" 'anonymous unless .env names a contact'
    $set = Get-RealComposeConfig -EnvValues @{ CM_OPENLIBRARY_CONTACT_EMAIL = 'someone@example.test' }
    Assert-Equal 'someone@example.test' "$($set.services.api.environment.Providers__OpenLibrary__ContactEmail)"
}

Test-Case 'compose: every variable the compose file reads is documented in .env.example, except CM_TAG (the deployer passes it)' {
    $compose = Get-Content (Join-Path $PSScriptRoot '../docker-compose.yml') -Raw
    $example = Get-Content (Join-Path $PSScriptRoot '../.env.example') -Raw
    $vars = [regex]::Matches($compose, '\$\{([A-Za-z_][A-Za-z0-9_]*)') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique | Where-Object { $_ -ne 'CM_TAG' }
    $missing = @($vars | Where-Object { $example -notmatch [regex]::Escape($_) })
    Assert-True ($missing.Count -eq 0) ('missing from .env.example: ' + ($missing -join ' '))

Test-Case 'compose: postgres is pinned to an exact patch and digest, never a floating tag' {
    $config = Get-RealComposeConfig
    Assert-Match "$($config.services.postgres.image)" '^postgres:17\.\d+@sha256:[0-9a-f]{64}$'
}

Test-Case 'compose: the dashboard is pinned to an exact version and digest, never a floating tag' {
    $config = Get-RealComposeConfig
    Assert-Match "$($config.services.'aspire-dashboard'.image)" '^mcr\.microsoft\.com/dotnet/aspire-dashboard:9\.\d+\.\d+@sha256:[0-9a-f]{64}$'
}

Test-Case 'compose: the api listens where the code reads its settings (connection string name, covers folder)' {
    $config = Get-RealComposeConfig
    $envNames = @($config.services.api.environment.PSObject.Properties.Name)
    Assert-True ($envNames -contains 'ConnectionStrings__collectionmaxxing') 'the API reads ConnectionStrings:collectionmaxxing'
    Assert-True ($envNames -contains 'Catalog__CoversRoot') 'the API reads Catalog:CoversRoot'
    Assert-True (@($config.services.migrate.environment.PSObject.Properties.Name) -contains 'ConnectionStrings__collectionmaxxing')
}

}

Test-Case 'compose: the api listens where the code reads its settings (connection string name, covers folder)' {
    $config = Get-RealComposeConfig
    $envNames = @($config.services.api.environment.PSObject.Properties.Name)
    Assert-True ($envNames -contains 'ConnectionStrings__collectionmaxxing') 'the API reads ConnectionStrings:collectionmaxxing'
    Assert-True ($envNames -contains 'Catalog__CoversRoot') 'the API reads Catalog:CoversRoot'
    Assert-True (@($config.services.migrate.environment.PSObject.Properties.Name) -contains 'ConnectionStrings__collectionmaxxing')
}
