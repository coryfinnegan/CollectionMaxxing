# A tiny test kit for the deploy scripts (no Pester: the inbox Windows Pester is v3 and CI has v5, so we depend on neither).
# Dot-sourced by Run-Tests.ps1. Every external command the scripts make goes through Invoke-Cm, which these tests
# replace with a recorder ($global:CmRunner). Nothing here touches the real Docker, GitHub, Tailscale or Task Scheduler.

$script:Passed = 0
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Test-Case {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][scriptblock]$Body)
    $global:CmCalls = [System.Collections.Generic.List[object]]::new()
    $global:CmRunner = $null
    $global:CmSleep = { param($s) }   # never really sleep
    Initialize-Cm -Apply $false
    try {
        & $Body
        $script:Passed++
    }
    catch {
        $script:Failures.Add("FAIL $Name`n     $($_.Exception.Message)")
    }
    finally {
        $global:CmRunner = $null
        $global:CmAuth0Runner = $null
    }
}

function Assert-True { param($Condition, [string]$Message = 'expected true') if (-not $Condition) { throw $Message } }
function Assert-False { param($Condition, [string]$Message = 'expected false') if ($Condition) { throw $Message } }
function Assert-Equal {
    param($Expected, $Actual, [string]$Message = '')
    if ("$Expected" -ne "$Actual") { throw "expected [$Expected] but got [$Actual] $Message" }
}
function Assert-Match { param([string]$Text, [string]$Pattern, [string]$Message = '') if ($Text -notmatch $Pattern) { throw "expected /$Pattern/ in [$Text] $Message" } }

function Get-TestSummary {
    [pscustomobject]@{ Passed = $script:Passed; Failures = $script:Failures }
}

function Add-ExternalResults {
    param([int]$Passed, [string[]]$Failed)
    $script:Passed += $Passed
    foreach ($f in $Failed) { $script:Failures.Add($f) }
}

# ---- recorder helpers -------------------------------------------------------------------------------------------

function Get-Calls { param([string]$Command = '*', [string]$Like = '*') @($global:CmCalls | Where-Object { $_.Command -like $Command -and $_.Display -like $Like }) }
function Get-MutatingCalls { @($global:CmCalls | Where-Object { $_.Mutating }) }

function Assert-NoForbiddenCalls {
    <# The calls that must never be recorded by a dry run: task registration, serve changes, compose up/run. #>
    $bad = @($global:CmCalls | Where-Object {
            $_.Display -match 'schtasks.* /Create' -or $_.Display -match 'Register-ScheduledTask' -or
            ($_.Command -eq 'tailscale' -and $_.Display -notmatch 'serve status') -or
            ($_.Display -match 'compose.* (up|run|down) ')
        })
    if ($bad.Count -gt 0) { throw "forbidden calls were made: $(($bad | ForEach-Object Display) -join ' | ')" }
}

# ---- a scripted fake machine ------------------------------------------------------------------------------------

$script:Sha1 = ('a1' * 20)   # the "old" good commit
$script:Sha2 = ('b2' * 20)   # the new commit on main
$script:Digest = @{ api = ('1' * 64); migrate = ('2' * 64); backup = ('3' * 64) }

function New-GoodComposeConfig {
    [pscustomobject]@{
        services = [pscustomobject]@{
            postgres           = [pscustomobject]@{ image = 'postgres:17' }
            api                = [pscustomobject]@{ ports = @([pscustomobject]@{ host_ip = '127.0.0.1'; published = '5480'; target = 5480 }) }
            'aspire-dashboard' = [pscustomobject]@{ ports = @([pscustomobject]@{ host_ip = '127.0.0.1'; published = '18888'; target = 18888 }) }
        }
    }
}

function New-FakeMachine {
    <#
      Installs $global:CmRunner. Options (all optional):
        Sha, Created            the commit and build time of the "main" image
        Up                      tag the stack currently runs
        BadAttest               image names whose `gh attestation verify` fails (exit 1)
        OmitCertDigest          certificate has no sourceRepositoryDigest field
        AttestRef               branch the certificate claims (default refs/heads/main)
        MigrateExit             exit code of `compose run --rm migrate`
        UnhealthyTags           CM_TAG values whose api answers 503
        ComposeConfig           object returned by `docker compose config`
        PullFails               make `docker pull` fail (Docker not running)
        Graph                  commit graph: sha -> array of all its ancestors (default: Sha2 descends from Sha1)
        FetchFails              make `git fetch` fail
        AlertFails              make the failure-alert ping fail
        RunningServices         container ids that `compose ps -q --status running` lists (default: none)
        HelperFails             make the deployer's helper container (`docker run --rm`, e.g. removing the old .ipa) fail
        RepoDirty               the deployer's clone has uncommitted changes to tracked files (`git status` lists one)
        RepoBranch              the branch the clone is on (default main; $null = a detached HEAD)
        MergeFails              make `git merge --ff-only` fail (the clone has diverged)
    #>
    param([hashtable]$Opt = @{})
    $m = @{
        Sha = $script:Sha2; Created = '2026-10-13T12:00:00.123456789Z'; Up = $script:Sha1; BadAttest = @(); AttestRef = 'refs/heads/main'
        MigrateExit = 0; UnhealthyTags = @(); ComposeConfig = (New-GoodComposeConfig); PullFails = $false
        Digest = $script:Digest
    }
    $graph = @{}; $graph[$script:Sha1] = @(); $graph[$script:Sha2] = @($script:Sha1)
    $m.Graph = $graph; $m.FetchFails = $false; $m.AlertFails = $false; $m.RunningServices = @()
    $m.HelperFails = $false; $m.RepoDirty = $false; $m.RepoBranch = 'main'; $m.MergeFails = $false
    foreach ($k in $Opt.Keys) { $m[$k] = $Opt[$k] }
    $global:CmMachine = $m
    $global:CmRunner = {
        param($Command, $Arguments, $Env, $Mutating)
        $m = $global:CmMachine
        $display = Format-CmCommand -Command $Command -Arguments $Arguments -Env $Env
        $global:CmCalls.Add([pscustomobject]@{ Command = $Command; Arguments = $Arguments; Env = $Env; Mutating = $Mutating; Display = $display })
        $ok = { param($lines = @()) [pscustomobject]@{ ExitCode = 0; Output = @($lines) } }
        $fail = { param($code = 1) [pscustomobject]@{ ExitCode = $code; Output = @() } }
        $line = $Arguments -join ' '
        switch ($Command) {
            'docker' {
                if ($line -match '^pull') { if ($m.PullFails) { return & $fail } return & $ok }
                if ($line -match '^image inspect (\S+)') {
                    $ref = $Matches[1]
                    $repo = $ref.Substring(0, $ref.LastIndexOf(':'))
                    $name = ($repo -split 'collectionmaxxing-')[-1]
                    $json = [ordered]@{
                        RepoDigests = @("$repo@sha256:$($m.Digest[$name])")
                        Created     = $m.Created
                        Config      = @{ Labels = @{ 'org.opencontainers.image.revision' = $m.Sha } }
                    } | ConvertTo-Json -Depth 6 -Compress
                    return & $ok @($json)
                }
                if ($line -match '^compose .* ps -q') { return & $ok @($m.RunningServices) }
                if ($line -match '^compose .* config --format json') { return & $ok @(($m.ComposeConfig | ConvertTo-Json -Depth 8 -Compress)) }
                if ($line -match '^compose .* run --rm migrate') { return [pscustomobject]@{ ExitCode = $m.MigrateExit; Output = @() } }
                if ($line -match '^compose .* up -d') { $m.Up = $Env['CM_TAG']; return & $ok }
                if ($line -match '^run --rm') { if ($m.HelperFails) { return & $fail } return & $ok }
                return & $ok
            }
            'gh' {
                if ($line -match '^attestation verify oci://\S+collectionmaxxing-(\w+)@') {
                    if ($m.BadAttest -contains $Matches[1]) { return & $fail }
                    $cert = @{ sourceRepositoryRef = $m.AttestRef; sourceRepositoryDigest = $m.Sha; sourceRepositoryURI = 'https://github.com/coryfinnegan/CollectionMaxxing' }
                    if ($m.OmitCertDigest) { $cert.Remove('sourceRepositoryDigest') }
                    return & $ok @((@(@{ verificationResult = @{ signature = @{ certificate = $cert } } }) | ConvertTo-Json -Depth 8 -Compress))
                }
                return & $ok
            }
            'git' {
                # A little commit graph: $m.Graph maps a commit to every ancestor of it. A commit that is not a key is unknown.
                $a = @($Arguments)
                if ($a[0] -eq '-C') { $a = @($a | Select-Object -Skip 2) }
                switch ($a[0]) {
                    'fetch' { if ($m.FetchFails) { return & $fail 128 } return & $ok }
                    'cat-file' {
                        $sha = "$($a[2])" -replace '\^\{commit\}$', ''
                        if ($m.Graph.ContainsKey($sha)) { return & $ok } else { return & $fail 128 }
                    }
                    'merge-base' {
                        $x = $a[2]; $y = $a[3]
                        if ($x -eq $y -or @($m.Graph[$y]) -contains $x) { return & $ok } else { return & $fail 1 }
                    }
                    'status' { if ($m.RepoDirty) { return & $ok @(' M deploy/deployer/Cm.Deploy.ps1') } return & $ok }
                    'symbolic-ref' { if ($null -eq $m.RepoBranch) { return & $fail 1 } return & $ok @($m.RepoBranch) }
                    'merge' { if ($m.MergeFails) { return & $fail 128 } return & $ok }
                    default { return & $ok }
                }
            }
            'curl' {
                if ($line -match '/fail$') {
                    # The failure alert (a ping of <heartbeat url>/fail with the reason as the body).
                    if ($m.AlertFails) { return & $fail 22 }
                    return & $ok @('')
                }
                $code = if ($m.UnhealthyTags -contains $m.Up) { '503' } else { '200' }
                return & $ok @($code)
            }
            default { return & $ok }
        }
    }
    $m
}

function New-TestRoot {
    <# A throwaway C:\CollectionMaxxing stand-in with a .env that has the fake values the guard needs. #>
    $root = Join-Path ([IO.Path]::GetTempPath()) ("cm-ops1-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    Set-Content -LiteralPath (Join-Path $root '.env') -Value @(
        '# throwaway test values', 'POSTGRES_PASSWORD=test-only-password', 'CM_BACKUP_DIR=/tmp/cm-test-backups',
        'CM_API_PORT=5480', 'CM_DASHBOARD_PORT=18888', 'CM_HEARTBEAT_URL=https://hc.example/test-uuid',
        'CM_SECRETS_DIR=/tmp/cm-test-secrets',
        'CM_PUBLIC_ORIGIN=https://pc.tailnet.ts.net', 'CM_AUTH0_DOMAIN=test.us.auth0.com', 'CM_AUTH0_WEB_CLIENT_ID=test-web', 'CM_AUTH0_WEB_CLIENT_SECRET=test-secret', 'CM_AUTH0_MANAGEMENT_CLIENT_ID=test-mgmt', 'CM_AUTH0_MANAGEMENT_CLIENT_SECRET=test-secret')
    $root
}

function Remove-TestRoot {
    param([string]$Root)
    # Only ever deletes folders this kit created.
    if ($Root -like '*cm-ops1-test-*' -and (Test-Path -LiteralPath $Root)) { Remove-Item -LiteralPath $Root -Recurse -Force }
}

function Set-TestState {
    param([string]$Root, [hashtable]$State)
    $dir = Join-Path $Root 'state'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $State | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $dir 'deployer-state.json')
}

function Get-TestState {
    param([string]$Root)
    $p = Join-Path $Root 'state/deployer-state.json'
    if (Test-Path -LiteralPath $p) { try { Get-Content -LiteralPath $p -Raw | ConvertFrom-Json -AsHashtable } catch { @{ unreadable = $true } } } else { @{} }
}

function New-TestContext {
    param([string]$Root)
    New-CmContext -Root $Root -ComposeFile 'compose.test.yml' -SmokeRetries 3 -SmokeDelaySeconds 0
}
