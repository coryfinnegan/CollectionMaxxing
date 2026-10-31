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

function Get-ShellSuiteResult {
    <#
      Turns one bash suite's printed lines plus its exit code into a result object with Passed and Failed.
      A suite delivers its verdict as an exit code (each one ends ((fail == 0))), so a non-zero exit, or a
      run that never printed its SUMMARY line, is failure however many PASS lines came before.
    #>
    param([Parameter(Mandatory)][string]$Suite, [string[]]$Lines = @(), [Parameter(Mandatory)][int]$ExitCode)
    $passed = @($Lines | Where-Object { $_ -like 'PASS *' }).Count
    $failed = @($Lines | Where-Object { $_ -like 'FAIL *' })
    if ($passed -eq 0 -and $failed.Count -eq 0) { $failed = @("FAIL $Suite ran nothing:`n     $($Lines | Select-Object -Last 5 | Out-String)") }
    if ($ExitCode -ne 0) {
        $tail = ($Lines | Select-Object -Last 3) -join "`n     "
        $failed = @($failed) + @("FAIL $Suite exited with code $ExitCode, so its $passed PASS lines prove nothing:`n     $tail")
    }
    elseif (@($Lines | Where-Object { $_ -like 'SUMMARY *' }).Count -eq 0) {
        $failed = @($failed) + @("FAIL $Suite never printed its SUMMARY line (did not run to completion)")
    }
    [pscustomobject]@{ Passed = $passed; Failed = $failed }
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
        ExtraDigests            image name -> extra RepoDigests (hex) listed BEFORE the attested one (#491: the
                                :main index digest sorts before the :sha manifest digest); `gh attestation verify`
                                fails for these digests and succeeds for the attested one
        MainMovesTo             revision a second `:main` pull resolves to (the tag moved mid-run)
        ApiContainerMissing     `compose ps -q api` lists no container
        RunningRevision         revision `docker inspect` of the running api reports (default: the tag it runs)
        ApiInspectFails         make that `docker inspect` fail
        OmitCertDigest          certificate has no sourceRepositoryDigest field
        AttestRef               branch the certificate claims (default refs/heads/main)
        MigrateExit             exit code of `compose run --rm migrate`
        UnhealthyTags           CM_TAG values whose api answers 503
        BrokenServices          tag -> services `compose ps` reports as crash-looping (restarting) at that tag
        MissingServices         tag -> services `compose ps` omits entirely at that tag
        PsFails                 make `compose ps --format json` fail (the container list cannot be read)
        ComposeConfig           object returned by `docker compose config`
        PullFails               make `docker pull` fail (Docker not running)
        Graph                  commit graph: sha -> array of all its ancestors (default: Sha2 descends from Sha1)
        FetchFails              make `git fetch` fail
        AlertFails              make the failure-alert ping fail
        RunningServices         container ids that `compose ps -q --status running` lists (default: none)
        HelperFails             make the deployer's helper container (`docker run --rm`, e.g. removing the old .ipa) fail
        RepoDirty               the deployer's clone has uncommitted changes to tracked files (`git status` lists one)
        RepoBranch              the branch the clone is on (default main; $null = a detached HEAD)
        ComposeAt               sha -> text of deploy/docker-compose.yml at that commit (default: a comment naming the sha)
        MergeFails              make `git merge --ff-only` fail (the clone has diverged)
        RepoHead                the commit the clone is at (`git rev-parse HEAD`); default one fixed commit
        MergeMovesTo            a successful `git merge --ff-only` moves the clone to this commit (default: it is already up to date)
        ChangedFiles            the files `git diff --name-only <deployed> <new>` lists (default: none, so no migration changed)
        DiffFails               make that `git diff` fail
        DumpExit                exit code of the pre-migrate database dump (`compose run --rm -T --entrypoint sh backup -c ...`)
        DumpWritesTo            a backup folder: a successful dump leaves an empty pre-migrate-<sha12>-<time>.dump in <folder>/pre-migrate
        ImageRows               local images as `docker image ls` lists them: 'repository|tag|id|created' lines (default none)
        ContainerImages         what `docker ps -a --format {{.Image}}` lists (default none)
        RmFails                 image refs (repo:tag) whose `docker image rm` fails
        RmAllFail               make every `docker image rm` fail
        ListFails               make `docker image ls` fail
    #>
    param([hashtable]$Opt = @{})
    $m = @{
        Sha = $script:Sha2; Created = '2026-10-13T12:00:00.123456789Z'; Up = $script:Sha1; BadAttest = @(); AttestRef = 'refs/heads/main'
        MigrateExit = 0; UnhealthyTags = @(); BrokenServices = @{}; MissingServices = @{}; PsFails = $false
        ComposeConfig = (New-GoodComposeConfig); PullFails = $false
        Digest = $script:Digest
        ExtraDigests = @{}; MainMovesTo = $null; MainPulls = 0
        ApiContainerMissing = $false; RunningRevision = ''; ApiInspectFails = $false
    }
    $graph = @{}; $graph[$script:Sha1] = @(); $graph[$script:Sha2] = @($script:Sha1)
    $m.Graph = $graph; $m.FetchFails = $false; $m.AlertFails = $false; $m.RunningServices = @()
    $m.HelperFails = $false; $m.RepoDirty = $false; $m.RepoBranch = 'main'; $m.MergeFails = $false; $m.ComposeAt = @{}; $m.ComposeConfigFor = @{}
    $m.RepoHead = ('f0' * 20); $m.MergeMovesTo = $null; $m.ChangedFiles = @(); $m.DiffFails = $false; $m.DumpExit = 0; $m.DumpWritesTo = $null
    $m.ImageRows = [System.Collections.Generic.List[string]]::new(); $m.ContainerImages = @(); $m.RmFails = @(); $m.RmAllFail = $false; $m.ListFails = $false
    foreach ($k in $Opt.Keys) { $m[$k] = $Opt[$k] }
    if ($m.ImageRows -isnot [System.Collections.Generic.List[string]]) { $m.ImageRows = [System.Collections.Generic.List[string]]::new([string[]]@($m.ImageRows)) }
    $global:CmMachine = $m
    $global:CmRunner = {
        param($Command, $Arguments, $Env, $Mutating)
        $m = $global:CmMachine
        $display = Format-CmCommand -Command $Command -Arguments $Arguments -Env $Env
        # For a compose call, remember which file it was given and what was in it at that moment (the deployer deletes its temp copies).
        $composeFile = $null; $composeText = $null
        if ($Command -eq 'docker' -and $Arguments.Count -gt 0 -and $Arguments[0] -eq 'compose') {
            $fi = [array]::IndexOf($Arguments, '-f')
            if ($fi -ge 0) { $composeFile = $Arguments[$fi + 1]; if (Test-Path -LiteralPath $composeFile) { $composeText = (Get-Content -LiteralPath $composeFile -Raw).Trim() } }
        }
        $global:CmCalls.Add([pscustomobject]@{ Command = $Command; Arguments = $Arguments; Env = $Env; Mutating = $Mutating; Display = $display; ComposeFile = $composeFile; ComposeText = $composeText })
        $ok = { param($lines = @()) [pscustomobject]@{ ExitCode = 0; Output = @($lines) } }
        $fail = { param($code = 1) [pscustomobject]@{ ExitCode = $code; Output = @() } }
        $line = $Arguments -join ' '
        switch ($Command) {
            'docker' {
                if ($line -match '^pull') { if ($line -match ':main(\s|$)') { $m.MainPulls++ }; if ($m.PullFails) { return & $fail } return & $ok }
                if ($line -match '^image inspect (\S+)') {
                    $ref = $Matches[1]
                    $repo = $ref.Substring(0, $ref.LastIndexOf(':'))
                    $name = ($repo -split 'collectionmaxxing-')[-1]
                    $digests = @()
                    if ($m.ExtraDigests.ContainsKey($name)) { $digests += @($m.ExtraDigests[$name] | ForEach-Object { "$repo@sha256:$_" }) }
                    $digests += "$repo@sha256:$($m.Digest[$name])"
                    $rev = $m.Sha
                    if ($ref -like '*:main' -and $m.MainMovesTo -and $m.MainPulls -ge 2) { $rev = $m.MainMovesTo }
                    $json = [ordered]@{
                        RepoDigests = $digests
                        Created     = $m.Created
                        Config      = @{ Labels = @{ 'org.opencontainers.image.revision' = $rev } }
                    } | ConvertTo-Json -Depth 6 -Compress
                    return & $ok @($json)
                }
                if ($line -match '^inspect ') {
                    if ($m.ApiInspectFails) { return & $fail 1 }
                    $running = if ($m.RunningRevision) { $m.RunningRevision } elseif ("$($m.Up)" -match '^[0-9a-f]{40}$') { $m.Up } else { $m.Sha }
                    return & $ok @($running)
                }
                if ($line -match '^compose .* ps -q --status') { return & $ok @($m.RunningServices) }
                if ($line -match '^compose .* ps -q \w+') { if ($m.ApiContainerMissing) { return & $ok @() } return & $ok @('fake-api-container') }
                if ($line -match '^compose .* ps --format json') {
                    if ($m.PsFails) { return & $fail 1 }
                    $broken = @(); if ($m.BrokenServices.ContainsKey($m.Up)) { $broken = @($m.BrokenServices[$m.Up]) }
                    $missing = @(); if ($m.MissingServices.ContainsKey($m.Up)) { $missing = @($m.MissingServices[$m.Up]) }
                    $rows = @()
                    foreach ($svc in 'api', 'backup', 'aspire-dashboard') {
                        if ($missing -contains $svc) { continue }
                        $rows += [pscustomobject]@{ Service = $svc; Name = "collectionmaxxing-$svc-1"
                            State = $(if ($broken -contains $svc) { 'restarting' } else { 'running' }); Health = '' }
                    }
                    $rows += [pscustomobject]@{ Service = 'postgres'; Name = 'collectionmaxxing-postgres-1'; State = 'running'; Health = 'healthy' }
                    $rows += [pscustomobject]@{ Service = 'migrate'; Name = 'collectionmaxxing-migrate-1'; State = 'exited'; Health = ''; ExitCode = 0 }
                    return & $ok @(($rows | ConvertTo-Json -Depth 4 -Compress))
                }
                if ($line -match '^compose .* config --format json') {
                    $cfg = if ($composeText -and $m.ComposeConfigFor.ContainsKey($composeText)) { $m.ComposeConfigFor[$composeText] } else { $m.ComposeConfig }
                    return & $ok @(($cfg | ConvertTo-Json -Depth 8 -Compress))
                }
                if ($line -match '^compose .* run --rm .*--entrypoint sh backup ') {
                    # The pre-migrate dump. With DumpWritesTo it leaves an (empty) dump file where the real one would be.
                    if ($m.DumpExit -eq 0 -and $m.DumpWritesTo -and $line -match '(pre-migrate-[0-9a-f]{12}-\d{8}-\d{6}\.dump)') {
                        $dumpDir = Join-Path $m.DumpWritesTo 'pre-migrate'
                        New-Item -ItemType Directory -Force -Path $dumpDir | Out-Null
                        Set-Content -LiteralPath (Join-Path $dumpDir $Matches[1]) -Value 'dump'
                    }
                    return [pscustomobject]@{ ExitCode = $m.DumpExit; Output = @() }
                }
                if ($line -match '^compose .* run --rm migrate') { return [pscustomobject]@{ ExitCode = $m.MigrateExit; Output = @() } }
                if ($line -match '^image ls') { if ($m.ListFails) { return & $fail } return & $ok @($m.ImageRows) }
                if ($line -match '^ps -a') { return & $ok @($m.ContainerImages) }
                if ($line -match '^image rm (\S+)') {
                    $ref = $Matches[1]
                    if ($m.RmAllFail -or $m.RmFails -contains $ref) { return [pscustomobject]@{ ExitCode = 1; Output = @("Error response from daemon: conflict: unable to delete $ref (image is being used by a container)") } }
                    $gone = @($m.ImageRows | Where-Object { ($_ -split '\|')[0] + ':' + ($_ -split '\|')[1] -eq $ref })
                    foreach ($g in $gone) { [void]$m.ImageRows.Remove($g) }
                    return & $ok @("Untagged: $ref")
                }
                if ($line -match '^compose .* up -d') { $m.Up = $Env['CM_TAG']; return & $ok }
                if ($line -match '^run --rm') { if ($m.HelperFails) { return & $fail } return & $ok }
                return & $ok
            }
            'gh' {
                if ($line -match '^attestation verify oci://\S+collectionmaxxing-(\w+)@sha256:([0-9a-f]+)') {
                    if ($m.BadAttest -contains $Matches[1]) { return & $fail }
                    if (@($m.ExtraDigests[$Matches[1]]) -contains $Matches[2]) { return & $fail }
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
                    'show' {
                        # `git show <sha>:deploy/docker-compose.yml`: the compose file at that commit ($m.ComposeAt: sha -> text).
                        $sha = ("$($a[1])" -split ':')[0]
                        if ($m.ComposeAt.ContainsKey($sha)) { return & $ok @($m.ComposeAt[$sha]) }
                        return & $ok @("# compose file at $sha")
                    }
                    'merge' { if ($m.MergeFails) { return & $fail 128 } if ($m.MergeMovesTo) { $m.RepoHead = $m.MergeMovesTo } return & $ok }
                    'rev-parse' { return & $ok @($m.RepoHead) }
                    'diff' { if ($m.DiffFails) { return & $fail 128 } return & $ok @($m.ChangedFiles) }
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

function New-TestComposePath {
    <# Like production: the compose file sits in <root>/repo/deploy, where the deployer writes its per-commit copies. #>
    param([string]$Root)
    $deploy = Join-Path $Root 'repo/deploy'
    New-Item -ItemType Directory -Force -Path $deploy | Out-Null
    Join-Path $deploy 'docker-compose.yml'
}

function New-TestContext {
    param([string]$Root)
    New-CmContext -Root $Root -ComposeFile (New-TestComposePath $Root) -SmokeRetries 3 -SmokeDelaySeconds 0
}
