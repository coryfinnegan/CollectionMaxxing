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
    $global:CmNow = $null             # the real clock, unless a test sets one
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
        # The issue agent's seams (deploy/agent): the detached start, the process table, the clock and the claude lookup.
        $global:CmStartProcess = $null
        $global:CmProcessTable = $null
        $global:CmAgentNow = $null
        $global:CmClaudePath = $null
        $global:CmNow = $null
        # The shelf reader's seams (Cm.ShelfReader.ps1): the pinned files, the background download, free space and hashing.
        $global:CmShelfReaderFiles = $null
        $global:CmStartDetached = $null
        $global:CmDetachedStarts = $null
        $global:CmFreeBytes = $null
        $global:CmFileHash = $null
        $global:CmFindDownload = $null
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
    # A FAIL line keeps the lines printed right after it (up to 12, until the next PASS, FAIL or SUMMARY line): they are
    # often the real error, such as the stderr of a docker command that could not start. Dropping them hid it.
    $failed = @()
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -notlike 'FAIL *') { continue }
        $text = $Lines[$i]
        for ($j = $i + 1; $j -lt $Lines.Count -and $j -le $i + 12 -and $Lines[$j] -notmatch '^(PASS|FAIL|SUMMARY) '; $j++) { $text += "`n     $($Lines[$j])" }
        $failed += $text
    }
    if ($passed -eq 0 -and $failed.Count -eq 0) { $failed = @("FAIL $Suite ran nothing:`n     $($Lines | Select-Object -Last 5 | Out-String)") }
    if ($ExitCode -ne 0) {
        $tail = ($Lines | Select-Object -Last 12) -join "`n     "
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

function Get-TestReaderGpuLog {
    <# The start log of a reader that uses the card, verbatim from the PC (#802): at this log level no line says so. #>
    @('warn: LLAMA_ARG_HOST environment variable is set, but will be overwritten by command line argument --host'
        'warn: LLAMA_ARG_PORT environment variable is set, but will be overwritten by command line argument --port'
        '0.00.222.462 I srv  llama_server: initializing ...'
        '0.00.222.512 I cmn  common_param: common_params_print_info: verbosity = 3 (adjust with the `-lv N` CLI arg)'
        '0.00.222.722 I srv  init_listene: The UI is disabled'
        '0.00.222.858 W srv  server_setup: security: no API key is set and CORS allows all origins (see https://github.com/ggml-org/llama.cpp/pull/25655)'
        "0.00.242.934 I srv    load_model: loading model '/models/Qwen3VL-8B-Instruct-Q4_K_M.gguf'"
        '2.04.388.631 I cmn          init: llama threadpool init, n_threads = 16'
        "2.10.549.999 I srv    load_model: loaded multimodal model, '/models/mmproj-Qwen3VL-8B-Instruct-F16.gguf'"
        "2.10.803.278 I srv    load_model: initializing, n_slots = 1, n_ctx_slot = 12288, kv_unified = 'false'"
        '2.10.812.293 I srv  server_run_i: model loaded'
        '2.10.812.309 I srv  server_on_li: listening on http://0.0.0.0:8431')
}

function Get-TestReaderCpuLog {
    <# The start log of a reader whose CUDA could not start inside Docker, so it runs on the processor, verbatim from the PC (#802). #>
    @('warn: LLAMA_ARG_HOST environment variable is set, but will be overwritten by command line argument --host'
        'warn: LLAMA_ARG_PORT environment variable is set, but will be overwritten by command line argument --port'
        '0.00.235.033 E ggml_cuda_init: failed to initialize CUDA: named symbol not found'
        'warning: no usable GPU found, --gpu-layers option will be ignored'
        'warning: one possible reason is that llama.cpp was compiled without GPU support'
        'warning: consult docs/build.md for compilation instructions'
        '0.00.249.706 I srv  llama_server: initializing ...'
        "0.00.268.125 I srv    load_model: loading model '/models/Qwen3VL-8B-Instruct-Q4_K_M.gguf'"
        '3.39.208.595 I srv  server_run_i: model loaded'
        '3.39.208.669 I srv  server_on_li: listening on http://0.0.0.0:8431')
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
        Issues                  the fake GitHub: a List of issues (number, title, body, labels, state, comments, updatedAt) that
                                `gh issue list|create|comment|close` read and change; pass one list to every poll of a test
                                so the issues carry over
        GhFails                 make every `gh issue ...` call fail (offline, not logged in)
        GhThrows                make every `gh issue ...` call throw
        PullThrows              make `docker pull` throw (an unexpected crash in the deployer)
        BackupHealth            what the backup container's health reports: healthy (default), unhealthy, starting, none (no health check)
        BackupContainerMissing  `compose ps -q backup` lists no container
        BackupPsFails           make `compose ps -q backup` fail
        BackupInspectFails      make the `docker inspect` of the backup container fail
        Gpu                     what `nvidia-smi -L` says: $true lists one card, 'empty' answers with no card, $false (default) is not found
        ReaderHealth            the shelf-reader container: missing (default), healthy, starting, unhealthy, restarting, exited
        ReaderStartsAs          what `up -d --no-deps shelf-reader` leaves it as (default healthy)
        ReaderUpFails           make `up -d --no-deps shelf-reader` fail (no GPU access in Docker, say)
        ReaderPullFails         make the pull of the llama.cpp image fail
        ReaderImagePresent      the llama.cpp image is already local (default no; a good pull makes it so)
        ApiUnhealthyWithReader  the api answers 503 while it has the reader's endpoint
        ApiEndpoint             (read back) the endpoint the api was last started with; `docker inspect --format {{json .Config.Env}}` reports it
        ReaderThrows            make the reader's `compose ps` throw (an unexpected crash inside the reader step)
        ReaderId, ReaderStartedAt  the running reader container's id and start time (`docker inspect`); each new start changes both
        ReaderLabelHash         the running container's com.docker.compose.config-hash label (default cfg-hash-1)
        ComposeHash             what `compose config --hash shelf-reader` prints for the file at the deployed commit (default cfg-hash-1)
        ComposeHashFails        make `compose config --hash shelf-reader` fail
        ReaderInspectFails      make the `docker inspect` of the reader container fail
        ReaderLog               the current container's start log (`docker logs`; default: llama.cpp finding the card)
        ReaderLogOnStart        the log every container that `up` starts gets (default: the card found)
        ReaderLogFails          make `docker logs` of the reader fail
        ReaderUpKeepsHash       `up` recreates the container but its label keeps the old hash (compose disagreeing with itself)
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
    $m.Issues = [System.Collections.Generic.List[object]]::new(); $m.GhFails = $false; $m.GhThrows = $false; $m.PullThrows = $false
    $m.BackupHealth = 'healthy'; $m.BackupContainerMissing = $false; $m.BackupPsFails = $false; $m.BackupInspectFails = $false
    $m.Gpu = $false; $m.ReaderHealth = 'missing'; $m.ReaderStartsAs = 'healthy'; $m.ReaderUpFails = $false; $m.ReaderPullFails = $false
    $m.ReaderImagePresent = $false; $m.ApiUnhealthyWithReader = $false; $m.ApiEndpoint = ''; $m.ReaderThrows = $false
    $m.ReaderId = 'reader-c0'; $m.ReaderStartedAt = '2026-11-12T08:00:00.123456789Z'; $m.ReaderStarts = 0
    $m.ReaderLabelHash = 'cfg-hash-1'; $m.ComposeHash = 'cfg-hash-1'; $m.ComposeHashFails = $false; $m.ReaderInspectFails = $false
    $m.ReaderLog = @(Get-TestReaderGpuLog); $m.ReaderLogOnStart = @(Get-TestReaderGpuLog); $m.ReaderLogFails = $false; $m.ReaderUpKeepsHash = $false
    foreach ($k in $Opt.Keys) { $m[$k] = $Opt[$k] }
    if ($m.ImageRows -isnot [System.Collections.Generic.List[string]]) { $m.ImageRows = [System.Collections.Generic.List[string]]::new([string[]]@($m.ImageRows)) }
    $global:CmMachine = $m
    $global:CmRunner = {
        param($Command, $Arguments, $Env, $Mutating, $InputText, $TimeoutSeconds)
        $m = $global:CmMachine
        $display = Format-CmCommand -Command $Command -Arguments $Arguments -Env $Env
        # For a compose call, remember which file it was given and what was in it at that moment (the deployer deletes its temp copies).
        $composeFile = $null; $composeText = $null
        if ($Command -eq 'docker' -and $Arguments.Count -gt 0 -and $Arguments[0] -eq 'compose') {
            $fi = [array]::IndexOf($Arguments, '-f')
            if ($fi -ge 0) { $composeFile = $Arguments[$fi + 1]; if (Test-Path -LiteralPath $composeFile) { $composeText = (Get-Content -LiteralPath $composeFile -Raw).Trim() } }
        }
        $global:CmCalls.Add([pscustomobject]@{ Command = $Command; Arguments = $Arguments; Env = $Env; Mutating = $Mutating; Display = $display; ComposeFile = $composeFile; ComposeText = $composeText; InputText = $InputText; TimeoutSeconds = [int]$TimeoutSeconds })
        $ok = { param($lines = @()) [pscustomobject]@{ ExitCode = 0; Output = @($lines) } }
        $fail = { param($code = 1) [pscustomobject]@{ ExitCode = $code; Output = @() } }
        $line = $Arguments -join ' '
        switch ($Command) {
            'nvidia-smi' {
                if ("$($m.Gpu)" -eq 'empty') { return & $ok }
                if ($m.Gpu) { return & $ok @('GPU 0: NVIDIA GeForce RTX 4070 (UUID: GPU-00000000-0000-0000-0000-000000000000)') }
                return & $fail 127
            }
            'docker' {
                # The shelf reader (llama.cpp image, the gpu profile's shelf-reader service).
                if ($line -match '^pull .*ggml-org/llama\.cpp') { if ($m.ReaderPullFails) { return & $fail } $m.ReaderImagePresent = $true; return & $ok }
                if ($line -match '^image inspect \S*ggml-org/llama\.cpp') { if ($m.ReaderImagePresent) { return & $ok @('{}') } return & $fail }
                if ($line -match '^compose .* ps -a --format json shelf-reader$') {
                    if ($m.ReaderThrows) { throw 'docker compose exploded' }
                    if ($m.ReaderHealth -eq 'missing') { return & $ok }
                    $state = if ($m.ReaderHealth -in 'healthy', 'starting', 'unhealthy') { 'running' } else { $m.ReaderHealth }
                    $health = if ($m.ReaderHealth -in 'healthy', 'starting', 'unhealthy') { $m.ReaderHealth } else { '' }
                    return & $ok @(([pscustomobject]@{ ID = $m.ReaderId; Service = 'shelf-reader'; Name = 'collectionmaxxing-shelf-reader-1'; State = $state; Health = $health } | ConvertTo-Json -Compress))
                }
                if ($line -match '^compose .* up -d --no-deps shelf-reader$') {
                    if ($m.ReaderUpFails) { return [pscustomobject]@{ ExitCode = 1; Output = @('Error response from daemon: could not select device driver "nvidia" with capabilities: [[gpu]]') } }
                    # Like compose: a running container whose definition is unchanged is left as it is; otherwise a new start.
                    if ($m.ReaderHealth -in 'missing', 'exited', 'stopped', 'restarting' -or $m.ReaderLabelHash -ne $m.ComposeHash) {
                        $m.ReaderStarts++
                        $m.ReaderId = "reader-c$($m.ReaderStarts)"
                        $m.ReaderStartedAt = ([DateTime](Get-CmNow)).ToUniversalTime().AddSeconds($m.ReaderStarts).ToString('o', [Globalization.CultureInfo]::InvariantCulture)
                        if (-not $m.ReaderUpKeepsHash) { $m.ReaderLabelHash = $m.ComposeHash }
                        $m.ReaderLog = @($m.ReaderLogOnStart)
                        $m.ReaderHealth = $m.ReaderStartsAs
                    }
                    return & $ok
                }
                if ($line -match '^compose .* config --hash shelf-reader$') { if ($m.ComposeHashFails) { return & $fail } return & $ok @("shelf-reader $($m.ComposeHash)") }
                if ($line -match '^inspect \S+ --format \{\{\.State\.StartedAt\}\}') { if ($m.ReaderInspectFails) { return & $fail } return & $ok @("$($m.ReaderStartedAt)|$($m.ReaderLabelHash)") }
                if ($line -match '^logs ') {
                    if ($m.ReaderLogFails) { return & $fail }
                    # Like docker: only the lines written between --since and --until. The log starts in the container's first
                    # second (the PC prints the CUDA marker 0.235 s in), so a --since past that second loses it.
                    $startSec = [DateTimeOffset]::new((ConvertTo-CmUtc $m.ReaderStartedAt)).ToUnixTimeSeconds()
                    if ($line -match '--since (\d+)' -and [long]$Matches[1] -gt $startSec) { return & $ok }
                    if ($line -match '--until (\d+)' -and [long]$Matches[1] -lt $startSec + 1) { return & $ok }
                    return & $ok @($m.ReaderLog)
                }
                if ($line -match '^compose .* stop shelf-reader$') { if ($m.ReaderHealth -ne 'missing') { $m.ReaderHealth = 'exited' } return & $ok }
                if ($line -match '^pull') { if ($m.PullThrows) { throw 'docker exploded' }; if ($line -match ':main(\s|$)') { $m.MainPulls++ }; if ($m.PullFails) { return & $fail } return & $ok }
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
                if ($line -match '^inspect \S+ --format \{\{json \.Config\.Env\}\}$') {
                    # The running api's environment: what the shelf reader step reads to know whether it has the endpoint.
                    if ($m.ApiInspectFails) { return & $fail 1 }
                    return & $ok @((ConvertTo-Json -InputObject @('ASPNETCORE_HTTP_PORTS=5480', "ShelfReader__Endpoint=$($m.ApiEndpoint)") -Compress))
                }
                if ($line -match '^inspect \S+ --format \{\{json \.State\.Health\}\}') {
                    # The backup container's health (what its HEALTHCHECK, backup.sh health, last reported).
                    if ($m.BackupInspectFails) { return & $fail 1 }
                    if ($m.BackupHealth -eq 'none') { return & $ok @('null') }
                    $said = if ($m.BackupHealth -eq 'unhealthy') { 'unhealthy: the newest successful backup is 31 h old (limit 30 h)' } else { 'healthy: the newest successful backup is 3 h old' }
                    $health = [ordered]@{ Status = $m.BackupHealth; FailingStreak = 0; Log = @(@{ Start = '2026-10-31T11:59:00Z'; ExitCode = 0; Output = "$said`n" }) }
                    return & $ok @(($health | ConvertTo-Json -Depth 5 -Compress))
                }
                if ($line -match '^inspect ') {
                    if ($m.ApiInspectFails) { return & $fail 1 }
                    $running = if ($m.RunningRevision) { $m.RunningRevision } elseif ("$($m.Up)" -match '^[0-9a-f]{40}$') { $m.Up } else { $m.Sha }
                    return & $ok @($running)
                }
                if ($line -match '^compose .* ps -q --status') { return & $ok @($m.RunningServices) }
                if ($line -match '^compose .* ps -q backup$') {
                    if ($m.BackupPsFails) { return & $fail 1 }
                    if ($m.BackupContainerMissing) { return & $ok @() }
                    return & $ok @('fake-backup-container')
                }
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
                    if ($m.ReaderHealth -ne 'missing') { $rows += [pscustomobject]@{ Service = 'shelf-reader'; Name = 'collectionmaxxing-shelf-reader-1'; State = $(if ($m.ReaderHealth -in 'healthy', 'starting', 'unhealthy') { 'running' } else { $m.ReaderHealth }); Health = $(if ($m.ReaderHealth -in 'healthy', 'starting', 'unhealthy') { $m.ReaderHealth } else { '' }) } }
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
                if ($line -match '^compose .* up -d') {
                    $m.Up = $Env['CM_TAG']
                    if ($line -match ' api( |$)') { $m.ApiEndpoint = "$($Env['CM_SHELF_READER_ENDPOINT'])" }
                    return & $ok
                }
                if ($line -match '^run --rm') { if ($m.HelperFails) { return & $fail } return & $ok }
                return & $ok
            }
            'gh' {
                if ($line -match '^issue ') {
                    # The fake GitHub: $m.Issues is the repository's issue list. Bodies and comments arrive on standard input.
                    if ($m.GhThrows) { throw 'gh exploded' }
                    if ($m.GhFails) { return [pscustomobject]@{ ExitCode = 1; Output = @('error connecting to api.github.com') } }
                    $stamp = (Get-CmNow).ToString('o')
                    if ($line -match '^issue list ') {
                        $rows = @($m.Issues | Where-Object { $_.state -eq 'open' -and $_.labels -contains 'alert' } | ForEach-Object { [ordered]@{ number = $_.number; body = $_.body; updatedAt = $_.updatedAt } })
                        return & $ok @((ConvertTo-Json -InputObject $rows -Depth 4 -Compress))
                    }
                    if ($line -match '^issue create ') {
                        $labels = @(); $title = ''
                        for ($i = 0; $i -lt $Arguments.Count - 1; $i++) {
                            if ($Arguments[$i] -eq '--label') { $labels += $Arguments[$i + 1] }
                            if ($Arguments[$i] -eq '--title') { $title = $Arguments[$i + 1] }
                        }
                        $number = 101 + $m.Issues.Count
                        $m.Issues.Add([pscustomobject]@{ number = $number; title = $title; body = "$InputText"; labels = $labels; state = 'open'
                                comments = [System.Collections.Generic.List[string]]::new(); closeComment = ''; updatedAt = $stamp })
                        return & $ok @("https://github.com/coryfinnegan/CollectionMaxxing/issues/$number")
                    }
                    if ($line -match '^issue (comment|close) (\d+) ') {
                        $verb = $Matches[1]; $number = [int]$Matches[2]
                        $issue = @($m.Issues | Where-Object { $_.number -eq $number })[0]
                        if (-not $issue) { return [pscustomobject]@{ ExitCode = 1; Output = @('GraphQL: Could not resolve to an Issue') } }
                        if ($verb -eq 'comment') { $issue.comments.Add("$InputText"); $issue.updatedAt = $stamp; return & $ok @("https://github.com/coryfinnegan/CollectionMaxxing/issues/$number#issuecomment-1") }
                        if ($issue.state -eq 'closed') { return & $ok @("! Issue #$number is already closed") }
                        $at = [array]::IndexOf($Arguments, '--comment')
                        if ($at -ge 0) { $issue.closeComment = "$($Arguments[$at + 1])" }
                        $issue.state = 'closed'; $issue.updatedAt = $stamp
                        return & $ok @("Closed issue #$number")
                    }
                    return & $ok
                }
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
                $code = if ($m.UnhealthyTags -contains $m.Up -or ($m.ApiUnhealthyWithReader -and $m.ApiEndpoint)) { '503' } else { '200' }
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
