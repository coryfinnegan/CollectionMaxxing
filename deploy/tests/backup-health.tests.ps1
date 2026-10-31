# The deployer's view of the backup container's health. The backup image declares a HEALTHCHECK (deploy/backup/Dockerfile.backup:
# unhealthy when Postgres is unreachable or the newest successful backup is over 30 hours old), and `docker compose ps`
# reports its status. These tests pin how the deployer reads that status, so a stale or unreachable backup is never
# mistaken for a healthy service. The shell side (the health check itself) is tested in backup.test.sh.

function Get-BackupHealthProblems {
    <# What Get-CmUnhealthyServices reports when api and aspire-dashboard are fine and the backup container has this health. #>
    param([string]$Health)
    $root = New-TestRoot
    try {
        $ctx = New-TestContext $root
        $rows = foreach ($svc in 'api', 'backup', 'aspire-dashboard') {
            [pscustomobject]@{ Service = $svc; State = 'running'; Health = $(if ($svc -eq 'backup') { $Health } else { '' }) }
        }
        @(Get-CmUnhealthyServices -Ctx $ctx -PsJson (ConvertTo-Json -InputObject @($rows)))
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'backup health: a healthy backup container passes the service check' {
    Assert-Equal 0 @(Get-BackupHealthProblems 'healthy').Count
}

Test-Case 'backup health: an unhealthy backup (stale or no database) is reported by name' {
    $problems = @(Get-BackupHealthProblems 'unhealthy')
    Assert-Equal 1 $problems.Count
    Assert-Match $problems[0] 'backup health is unhealthy'
}

Test-Case 'backup health: a backup still starting is not healthy yet (the health check must settle inside the deployer''s retries)' {
    $problems = @(Get-BackupHealthProblems 'starting')
    Assert-Equal 1 $problems.Count
    Assert-Match $problems[0] 'backup health is starting'
}

Test-Case 'backup health: the deployer checks the backup service' {
    $root = New-TestRoot
    try { Assert-True (@((New-TestContext $root).Services) -contains 'backup') 'backup is one of the services the deployer waits for' }
    finally { Remove-TestRoot $root }
}

Test-Case 'backup health: the image probes fast enough for the deployer''s service check' {
    # The deployer polls `compose ps` SmokeRetries x SmokeDelaySeconds (12 x 5 s by default) after the API answers. A
    # container reports "starting" until its first probe passes, so the first probe must come well inside that.
    $docker = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../backup/Dockerfile.backup') -Raw
    Assert-Match $docker '(?m)^HEALTHCHECK .*--interval=(\d+)s' 'the image declares a HEALTHCHECK'
    $interval = [int]([regex]::Match($docker, '(?m)^HEALTHCHECK .*--interval=(\d+)s').Groups[1].Value)
    Assert-True ($interval -le 15) "the probe interval is $interval s; the first probe must pass within the deployer's 60 s window"
    Assert-Match $docker '(?m)^HEALTHCHECK .*CMD \["/opt/cm/backup\.sh", "health"\]' 'the probe is backup.sh health'
}
