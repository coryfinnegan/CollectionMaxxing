# Checks on the production compose file that need the real `docker compose config` (the CLI, no daemon).
# Uses Get-RealComposeConfig from ops.tests.ps1, so this file must sort after it (hence the name).

if (-not (Get-Command Get-RealComposeConfig -ErrorAction SilentlyContinue)) { throw 'stack.tests.ps1 must load after ops.tests.ps1 (it needs Get-RealComposeConfig)' }

Test-Case 'compose: every service caps its json-file log at 5 files of 20 MB, so a chatty container cannot fill the disk' {
    $config = Get-RealComposeConfig
    $names = @($config.services.PSObject.Properties.Name)
    Assert-True ($names.Count -ge 5) "the compose file has its five services (found $($names -join ', '))"
    foreach ($name in $names) {
        $logging = $config.services.$name.logging
        Assert-True ($null -ne $logging) "service $name sets no logging block, so Docker's default (unbounded json-file) applies"
        Assert-Equal 'json-file' $logging.driver "service $name"
        Assert-Equal '20m' $logging.options.'max-size' "service $name"
        Assert-Equal '5' $logging.options.'max-file' "service $name"
    }
}

Test-Case 'compose: the logging block does not trip the deployer''s config guard' {
    # The guard is called the way the deployer calls it (the .env values and the compose file's path), so the
    # stack's own bind mounts are allowed and only a logging problem could fail this.
    $config = Get-RealComposeConfig
    $envMap = @{ CM_API_PORT = '5480'; CM_DASHBOARD_PORT = '18888' } + $script:LastRealComposeEnv
    Assert-Equal 0 @(Test-CmConfig -EnvMap $envMap -ComposeConfig $config -ComposeFile $script:LastRealComposeFile).Count
}
