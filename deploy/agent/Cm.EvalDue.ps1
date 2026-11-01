# The eval-due check: the issue agent's watcher keeps one GitHub issue, labelled needs:eval-run, that tells the owner when the
# phone has to run the recognition evals again. Dot-sourced by Cm.Agent.ps1 (after deployer/Cm.Common.ps1 and Cm.Alerts.ps1);
# it has no side effects when loaded.
#
# WHY
#   Server changes need no phone run: the PC scores the stored readings again on every new server build. Only the phone's own
#   recognition code (CmEvalDueRecognitionPaths below) does. So the question is whether a TestFlight build exists that changed
#   that code since the build the phone last ran evals on, and the answer is an issue the owner's AI assistant can read out.
#
# THE RULE
#   * An uploaded build is a successful run of ios-build.yml on main whose `build` job succeeded (a push whose head commit says
#     [skip ios-build] skips that job, so it uploaded nothing). The build number is the run number. A scheduled run rebuilds
#     the latest release, not main, so its head commit says nothing about the code in it and it is left out.
#   * Newest build: the newest uploaded build. Phone-tested build: the newest uploaded build whose run finished before the last
#     finished phone run started. Due: files under the recognition paths changed between the two (or the phone-tested build
#     is unknown, or the file list is cut off at 300 files).
#   * At most one open needs:eval-run issue. Due and none open: open one. Due and the open one names an older build: retitle it
#     to the newest build. A finished phone run that tested the issue's build closes it with the score in a comment. An issue
#     with no marker (a person or an agent opened it by hand) closes after the next finished phone run that started after it.
#
# WHERE THE PHONE RUN IS RECORDED (src/**/Evals/, checked against a real results folder)
#   * evals\results\open-run\run.json is the PC's one run: state Open or Finishing (the phone is still sending photos, or asked
#     to finish and the PC is scoring) or Done, with openedAt (when the phone started it). A new run replaces it.
#   * evals\results\latest-inputs.json is the phone's readings of the last SCORED run: runId and readAt (= that run's
#     openedAt). It stays while a newer run is open, and a re-score on a new server build leaves it alone.
#   * latest-run.json and latest.json are rewritten by every score, including the PC re-scoring old readings, and the time in
#     latest-run.json is when it was SCORED. They cannot tell a phone run from a re-score, so only latest.json is used, for the
#     verdict in the closing comment (its runId, or regroundedFrom.runId, is the phone run).
#   * Times are read as DateTimeOffset from the raw JSON text: ConvertFrom-Json would turn them into local time.
#
# COST AND SAFETY MODEL
#   * No model. Plain PowerShell, `gh` and a few small files. A check is about five gh calls and runs at most every 30 minutes
#     (the time of the last one is in state\eval-due.json); a pass in between makes none. It runs on every watcher pass,
#     CM_AGENT_ENABLED or not, so the owner can switch the agent off to save tokens and still be asked for an eval run.
#   * It does nothing on a PC without an evals folder (CM_EVALS_DIR, default <Root>\evals) or with CM_EVALS_ENABLED off.
#   * Every gh call goes through Invoke-CmAlertGh and anything that changes GitHub is -Mutating, so without -Apply a pass only
#     reads and prints what it would do. What is posted goes through Hide-CmAgentText.
#   * A failure (gh down, a file missing its JSON, half-written or corrupt) is a WARN line and the next check in 30 minutes.
#     It is never an error: Invoke-CmEvalDueSafely swallows everything and the agent pass's result is untouched.
#Requires -Version 7.0

# The phone's own recognition code. An eval run is due when a file under one of these changed. Folders end in a slash.
$script:CmEvalDueRecognitionPaths = @('app/src/features/scan/', 'app/src/features/evals/', 'app/modules/cm-vision/')
$script:CmEvalDueLabels = @('needs:eval-run', 'needs:cory')   # the first one is what the check looks for; both go on the issue
$script:CmEvalDueWorkflow = 'ios-build.yml'
$script:CmEvalDueCheckSeconds = 1800
$script:CmEvalDueGhSeconds = 60       # a read that hangs must not hold the watcher lock for ever (a write cannot be timed: its body is on stdin)
$script:CmEvalDueRunLimit = 30        # recent workflow runs listed; the newest builds are looked at first, so this is a ceiling
$script:CmEvalDueCommitLimit = 10     # commits named in the issue body
$script:CmEvalDueFileLimit = 300      # the compare API cuts its file list here: a list this long may be missing files

# ---- where things are -------------------------------------------------------------------------------------------------------

function Get-CmEvalDueDir {
    <# The evals folder: CM_EVALS_DIR when .env sets it, else <Root>\evals. #>
    param($Ctx, [hashtable]$EnvMap = @{})
    $dir = "$($EnvMap['CM_EVALS_DIR'])".Trim()
    if ($dir) { return $dir }
    [IO.Path]::Combine($Ctx.Root, 'evals')
}

function Test-CmEvalDueEnabled {
    <# On unless CM_EVALS_ENABLED says 0, false, off or no (the words CM_AGENT_ENABLED uses). #>
    param([hashtable]$EnvMap = @{})
    "$($EnvMap['CM_EVALS_ENABLED'])".Trim().ToLowerInvariant() -notin @('0', 'false', 'off', 'no')
}

# ---- the phone's last run, from files --------------------------------------------------------------------------------------

function Read-CmEvalDueFields {
    <#
      Some top-level values of a JSON file as @{ Status; Values }: Status is ok, missing (no file) or bad (not JSON, half written,
      not an object); Values maps each wanted name to its string or boolean, exactly as written (a time stays text).
    #>
    param([string]$Path, [string[]]$Names)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return [pscustomobject]@{ Status = 'missing'; Values = @{} } }
    try {
        $doc = [Text.Json.JsonDocument]::Parse((Get-Content -LiteralPath $Path -Raw))
        try {
            if ($doc.RootElement.ValueKind -ne 'Object') { throw 'not an object' }
            $values = @{}
            foreach ($property in $doc.RootElement.EnumerateObject()) {
                if ($property.Name -cnotin $Names) { continue }
                $values[$property.Name] = switch ($property.Value.ValueKind) { 'String' { $property.Value.GetString() } 'True' { $true } 'False' { $false } default { $null } }
            }
        }
        finally { $doc.Dispose() }
        [pscustomobject]@{ Status = 'ok'; Values = $values }
    }
    catch { [pscustomobject]@{ Status = 'bad'; Values = @{} } }
}

function Get-CmEvalDuePhoneRun {
    <#
      The phone's run as @{ InProgress; Finished; RunId; StartedUtc }. InProgress: the PC's open run is Open or
      Finishing. Finished: some run was scored; RunId, StartedUtc (UTC DateTime) are the latest one's, taken from
      run.json (when its state is Done) and latest-inputs.json, whichever started later. No run at all is not an error. A file
      that is there but is not JSON throws, so the caller warns and tries later instead of acting on half a picture.
    #>
    param([string]$Dir)
    $results = [IO.Path]::Combine($Dir, 'results')
    $header = Read-CmEvalDueFields -Path ([IO.Path]::Combine($results, 'open-run', 'run.json')) -Names @('runId', 'openedAt', 'state')
    $kept = Read-CmEvalDueFields -Path ([IO.Path]::Combine($results, 'latest-inputs.json')) -Names @('runId', 'readAt')
    foreach ($file in @(@('open-run\run.json', $header), @('latest-inputs.json', $kept))) {
        if ($file[1].Status -eq 'bad') { throw "$($file[0]) in the evals results folder is not valid JSON (half written?)" }
    }
    $candidates = @()
    if ($header.Status -eq 'ok' -and "$($header.Values.state)" -eq 'Done') { $candidates += [pscustomobject]@{ RunId = "$($header.Values.runId)"; Started = $header.Values.openedAt } }
    if ($kept.Status -eq 'ok') { $candidates += [pscustomobject]@{ RunId = "$($kept.Values.runId)"; Started = $kept.Values.readAt } }
    $runs = foreach ($c in $candidates) {
        $started = ConvertTo-CmUtc $c.Started
        if ($started) { [pscustomobject]@{ RunId = $c.RunId; StartedUtc = $started } }
    }
    $latest = @($runs | Sort-Object StartedUtc -Descending | Select-Object -First 1)
    [pscustomobject]@{
        InProgress = ($header.Status -eq 'ok' -and "$($header.Values.state)" -in @('Open', 'Finishing'))
        Finished   = ($latest.Count -gt 0)
        RunId      = $(if ($latest.Count -gt 0) { $latest[0].RunId } else { '' })
        StartedUtc = $(if ($latest.Count -gt 0) { $latest[0].StartedUtc } else { $null })
    }
}

function Get-CmEvalDueScore {
    <#
      What the closing comment says about the score, from latest.json: @{ Verdict; Failing; PhotosRun; PhotosInSet }, or $null when
      the file is missing, is not JSON, or scores another run (its runId, or regroundedFrom.runId when the PC re-scored the phone's
      readings, is the phone run's). A WARN says why; the issue still closes, without the score.
    #>
    param([string]$Dir, [string]$RunId)
    $path = [IO.Path]::Combine($Dir, 'results', 'latest.json')
    try {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'latest.json is missing' }
        $summary = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ("$($summary.runId)" -ne $RunId -and "$($summary.regroundedFrom.runId)" -ne $RunId) { throw 'latest.json scores another run' }
        [pscustomobject]@{
            Verdict     = "$($summary.gate.verdict)"
            Failing     = @(@($summary.gate.measures) | Where-Object { $_ -and "$($_.status)" -eq 'fail' }).Count
            PhotosRun   = [int]$summary.photosRun
            PhotosInSet = [int]$summary.photosInSet
        }
    }
    catch {
        Write-CmLog "eval-due: the score of phone run $RunId is not in the comment ($($_.Exception.Message))" 'WARN'
        $null
    }
}

# ---- GitHub: builds and changes --------------------------------------------------------------------------------------------

function Invoke-CmEvalDueGh {
    <# A read-only gh call that prints JSON. Returns what it parsed ($null for no output); throws, saying what it was after, when gh failed. #>
    param([string[]]$Arguments, [string]$What)
    $r = try { Invoke-Cm -Command 'gh' -Arguments $Arguments -Env @{ GH_PROMPT_DISABLED = '1' } -TimeoutSeconds $script:CmEvalDueGhSeconds }
    catch { [pscustomobject]@{ ExitCode = 127; Output = @("$($_.Exception.Message)") } }
    if ($r.ExitCode -ne 0) { throw "gh could not give $What ($(Get-CmAlertFirstLine $r))" }
    $text = (@($r.Output) -join "`n").Trim()
    if (-not $text) { return $null }
    try { $text | ConvertFrom-Json } catch { throw "gh printed something that is not JSON for $What" }
}

function Get-CmEvalDueBuilds {
    <# The finished, successful ios-build runs on main (newest first) that could be uploads. Whether one is, is Test-CmEvalDueUploaded. #>
    param($Ctx)
    $runs = @(Invoke-CmEvalDueGh -What 'the iOS builds' -Arguments @('run', 'list', '--repo', $Ctx.Repo, '--workflow', $script:CmEvalDueWorkflow, '--branch', 'main',
            '--limit', "$($script:CmEvalDueRunLimit)", '--json', 'number,headSha,updatedAt,status,conclusion,databaseId,event'))
    @($runs | Where-Object { $_ -and "$($_.status)" -eq 'completed' -and "$($_.conclusion)" -eq 'success' -and "$($_.event)" -ne 'schedule' } | ForEach-Object {
            [pscustomobject]@{ Number = [int]$_.number; Sha = "$($_.headSha)"; RunId = [long]$_.databaseId; FinishedUtc = (ConvertTo-CmUtc $_.updatedAt); Uploaded = $null }
        } | Sort-Object Number -Descending)
}

function Test-CmEvalDueUploaded {
    <# True when the run's `build` job concluded success (it is skipped by [skip ios-build]). One gh call per run, remembered on the build. #>
    param($Ctx, $Build)
    if ($null -eq $Build.Uploaded) {
        $jobs = @(Invoke-CmEvalDueGh -What "the jobs of build $($Build.Number)" -Arguments @('api', "repos/$($Ctx.Repo)/actions/runs/$($Build.RunId)/jobs", '--jq', '[.jobs[] | {name: .name, conclusion: .conclusion}]'))
        $Build.Uploaded = (@($jobs | Where-Object { $_ -and "$($_.name)" -eq 'build' -and "$($_.conclusion)" -eq 'success' }).Count -gt 0)
    }
    $Build.Uploaded
}

function Find-CmEvalDueBuild {
    <# The newest uploaded build, or with -Before (a UTC time) the newest one whose run finished before that. $null when there is none. #>
    param($Ctx, $Builds, $Before = $null)
    foreach ($build in @($Builds)) {
        if ($null -ne $Before -and $build.FinishedUtc -ge $Before) { continue }
        if (Test-CmEvalDueUploaded -Ctx $Ctx -Build $build) { return $build }
    }
    $null
}

function Get-CmEvalDueChange {
    <#
      Whether recognition changed between two builds, as @{ Due; Why; Range }. Without a phone-tested build there is nothing to
      compare: due. A compare that lists as many files as the API will give may be cut off: due. Range is the shas between the
      two builds, for naming the commits that did it.
    #>
    param($Ctx, $Tested, $Newest)
    $compare = Invoke-CmEvalDueGh -What "the changes from build $($Tested.Number) to build $($Newest.Number)" -Arguments @('api', "repos/$($Ctx.Repo)/compare/$($Tested.Sha)...$($Newest.Sha)",
        '--jq', '{files: [(.files // [])[].filename], commits: [(.commits // [])[].sha]}')
    $files = @($compare.files | Where-Object { $_ })
    $range = @($compare.commits | Where-Object { $_ })
    if ($files.Count -ge $script:CmEvalDueFileLimit) { return [pscustomobject]@{ Due = $true; Why = 'the list of changed files is cut off'; Range = $range } }
    $hit = @($files | Where-Object { $file = $_; @($script:CmEvalDueRecognitionPaths | Where-Object { $file.StartsWith($_, [StringComparison]::Ordinal) }).Count -gt 0 })
    [pscustomobject]@{ Due = ($hit.Count -gt 0); Why = $(if ($hit.Count -gt 0) { 'recognition files changed' } else { 'no recognition file changed' }); Range = $range }
}

function Get-CmEvalDueCommits {
    <#
      The first lines of up to CmEvalDueCommitLimit of the commits between the two builds that touched a recognition path, newest
      first. One gh call per path asks which commits touched it; Range keeps only those between the builds. A failure is a WARN
      and an empty list: the issue is due either way.
    #>
    param($Ctx, $Newest, $Range)
    try {
        $subjects = @{}
        foreach ($path in $script:CmEvalDueRecognitionPaths) {
            $rows = @(Invoke-CmEvalDueGh -What "the commits that touched $path" -Arguments @('api', "repos/$($Ctx.Repo)/commits?sha=$($Newest.Sha)&path=$($path.TrimEnd('/'))&per_page=100",
                    '--jq', '[.[] | {sha: .sha, subject: (.commit.message | split("\n")[0])}]'))
            foreach ($row in $rows) { if ($row -and "$($row.sha)" -in @($Range)) { $subjects["$($row.sha)"] = "$($row.subject)".Trim() } }
        }
        $ordered = @(@($Range) | Where-Object { $subjects.ContainsKey("$_") })
        [array]::Reverse($ordered)
        @($ordered | Select-Object -First $script:CmEvalDueCommitLimit | ForEach-Object {
                $line = ($subjects["$_"] -replace '[\x00-\x1f]', ' ')
                if ($line.Length -gt 120) { $line = $line.Substring(0, 117) + '...' }
                $line
            })
    }
    catch {
        Write-CmLog "eval-due: the commits behind the change are left out of the issue ($($_.Exception.Message))" 'WARN'
        @()
    }
}

# ---- the issue ----------------------------------------------------------------------------------------------------------------

function Get-CmEvalDueMarker {
    <# The hidden marker in an issue body, @{ Build; Sha }, or $null for an issue made by hand. The last one counts. #>
    param([string]$Body)
    $found = [regex]::Matches("$Body", '<!--\s*eval-due\s+build=(\d{1,9})\s+sha=([0-9a-fA-F]+)\s*-->')
    if ($found.Count -eq 0) { return $null }
    $last = $found[$found.Count - 1]
    [pscustomobject]@{ Build = [int]$last.Groups[1].Value; Sha = $last.Groups[2].Value }
}

function Format-CmEvalDueTitle {
    param([int]$Build)
    "[Owner] Run evals on TestFlight build $Build"
}

function Format-CmEvalDueBody {
    <# Plain words an AI assistant can read out, then the marker. $Tested is $null when no phone run is known. #>
    param($Newest, $Tested, [string]$Why, [string[]]$Commits = @())
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("The phone needs to run the recognition evals on TestFlight build $($Newest.Number).")
    $lines.Add('')
    if ($Tested) {
        $lines.Add("What changed in recognition since build $($Tested.Number), the last build the phone ran evals on:")
        $named = @($Commits | Where-Object { $_ })
        if ($named.Count -gt 0) { foreach ($c in $named) { $lines.Add("- $c") } }
        else { $lines.Add("- (the commits are not listed here; $Why)") }
    }
    else { $lines.Add($Why) }
    $lines.Add('')
    $lines.Add('What to do:')
    $lines.Add("1. Install build $($Newest.Number) from TestFlight. It updates by itself once Apple has processed it, usually within half an hour.")
    $lines.Add('2. On the phone open Settings > Recognition > Run evals, and let it finish.')
    $lines.Add('')
    $lines.Add('The PC scores it, and this issue closes itself.')
    $lines.Add('')
    $lines.Add("<!-- eval-due build=$($Newest.Number) sha=$($Newest.Sha) -->")
    $lines -join "`n"
}

function Get-CmEvalDueIssues {
    <# The open needs:eval-run issues written by the repo owner: @{ Number; CreatedUtc; Marker }. Throws when gh cannot say. #>
    param($Ctx)
    $rows = @(Invoke-CmEvalDueGh -What 'the open eval issues' -Arguments @('issue', 'list', '--repo', $Ctx.Repo, '--state', 'open', '--label', $script:CmEvalDueLabels[0],
            '--limit', '20', '--json', 'number,title,body,createdAt,author'))
    @($rows | Where-Object { $_ -and "$($_.author.login)" -ieq $Ctx.Owner } | Sort-Object { [int]$_.number } | ForEach-Object {
            [pscustomobject]@{ Number = [int]$_.number; CreatedUtc = (ConvertTo-CmUtc $_.createdAt); Marker = (Get-CmEvalDueMarker "$($_.body)") }
        })
}

function Write-CmEvalDueGh {
    <# One gh call that changes GitHub (so, only with -Apply). $true when it worked; otherwise a WARN says what failed. #>
    param([string[]]$Arguments, [string]$InputText = '', [string]$Doing)
    $r = Invoke-CmAlertGh -Write -Arguments $Arguments -InputText $InputText
    if ($r.ExitCode -ne 0) { Write-CmLog "eval-due: could not $Doing on GitHub ($(Get-CmAlertFirstLine $r))" 'WARN' }
    $r.ExitCode -eq 0
}

function Get-CmEvalDueVerb {
    <# A log line says what happened with -Apply and what would happen without it. #>
    param([string]$Did, [string]$Would)
    if ($global:CmApply) { $Did } else { $Would }
}

function Format-CmEvalDueDoneComment {
    <# One line (it goes on the command line): the run, the score when it is known, and the report. No photos, no other paths. #>
    param($Run, $Score, [string]$ReportPath)
    $text = "The phone's eval run finished, so this is done. Run $($Run.RunId) started $($Run.StartedUtc.ToString('yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture)) UTC"
    if ($Score) {
        if ($Score.PhotosInSet -gt 0) { $text += ": $($Score.PhotosRun) of $($Score.PhotosInSet) photos run" } else { $text += ':' }
        $text += ", gate verdict $($Score.Verdict), $($Score.Failing) failing measure$(if ($Score.Failing -ne 1) { 's' })"
    }
    $text += ". Report: $ReportPath"
    $text -replace '["\r\n]', ' '
}

# ---- the check -----------------------------------------------------------------------------------------------------------------

function Invoke-CmEvalDue {
    <#
      One eval-due check (see the header). Returns nothing. Throws when something it needs cannot be read; Invoke-CmEvalDueSafely
      turns that into a WARN. Does nothing at all, without a single call, when this PC keeps no evals, when the last check was
      under CmEvalDueCheckSeconds ago, or when nothing is due.
    #>
    param($Ctx, [hashtable]$EnvMap = @{})
    if (-not (Test-CmEvalDueEnabled -EnvMap $EnvMap)) { return }
    $dir = Get-CmEvalDueDir -Ctx $Ctx -EnvMap $EnvMap
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) { return }

    $stateFile = [IO.Path]::Combine($Ctx.Root, 'state', 'eval-due.json')
    $state = Read-CmState -Path $stateFile
    $now = (Get-CmAgentNow).ToUnixTimeSeconds()
    $age = $now - [long]$state.checkedUnix
    if ($age -ge 0 -and $age -lt $script:CmEvalDueCheckSeconds) { return }
    # Written before the calls: a check that fails is not tried again for the same 30 minutes, so a gh outage costs one WARN each.
    $state.checkedUnix = $now
    Write-CmState -Path $stateFile -State $state

    $run = Get-CmEvalDuePhoneRun -Dir $dir
    $builds = Get-CmEvalDueBuilds -Ctx $Ctx
    $newest = Find-CmEvalDueBuild -Ctx $Ctx -Builds $builds
    if (-not $newest) { return }   # no upload among the recent runs: nothing to ask the owner to install
    $tested = if ($run.Finished) { Find-CmEvalDueBuild -Ctx $Ctx -Builds $builds -Before $run.StartedUtc } else { $null }

    # Close what a finished run answered. A run still in progress answers nothing yet.
    $open = [System.Collections.Generic.List[object]]::new()
    foreach ($issue in @(Get-CmEvalDueIssues -Ctx $Ctx)) {
        $answered = $false
        if ($run.Finished -and -not $run.InProgress) {
            $answered = if ($issue.Marker) { $null -ne $tested -and $tested.Number -ge $issue.Marker.Build } else { $issue.CreatedUtc -and $run.StartedUtc -gt $issue.CreatedUtc }
        }
        if ($answered) {
            $score = Get-CmEvalDueScore -Dir $dir -RunId $run.RunId
            $report = [IO.Path]::Combine($dir, 'results', 'report.md')
            $comment = Hide-CmAgentText (Format-CmEvalDueDoneComment -Run $run -Score $score -ReportPath $report)
            if (Write-CmEvalDueGh -Arguments @('issue', 'close', "$($issue.Number)", '--repo', $Ctx.Repo, '--comment', $comment) -Doing "close issue #$($issue.Number)") {
                Write-CmLog "eval-due: $(Get-CmEvalDueVerb 'closed' 'would close') issue #$($issue.Number), the phone's run $($run.RunId) answered it"
                continue
            }
        }
        $open.Add($issue)
    }

    # Is an eval due, and does it need a new issue or only a new build number on the open one?
    if ($tested -and $tested.Number -ge $newest.Number) { return }
    $older = @($open | Where-Object { $_.Marker -and $_.Marker.Build -lt $newest.Number } | Select-Object -First 1)
    if ($open.Count -gt 0 -and $older.Count -eq 0) { return }   # one is open and already names the newest build, or was opened by hand
    $range = @()
    if ($run.Finished -and -not $tested) { $why = "The build the phone last ran evals on is not among the recent TestFlight builds, so every change since is untested." }
    elseif (-not $tested) { $why = 'No finished phone eval run is on the PC yet, so recognition has never been scored.' }
    else {
        $change = Get-CmEvalDueChange -Ctx $Ctx -Tested $tested -Newest $newest
        if (-not $change.Due) { return }
        $why = $change.Why
        $range = $change.Range
    }
    $commits = @(if ($tested) { Get-CmEvalDueCommits -Ctx $Ctx -Newest $newest -Range $range })
    $body = Hide-CmAgentText (Format-CmEvalDueBody -Newest $newest -Tested $tested -Why $why -Commits $commits)
    $title = Format-CmEvalDueTitle -Build $newest.Number

    if ($open.Count -eq 0) {
        $arguments = @('issue', 'create', '--repo', $Ctx.Repo, '--title', $title, '--body-file', '-')
        foreach ($label in $script:CmEvalDueLabels) { $arguments += @('--label', $label) }
        if (Write-CmEvalDueGh -Arguments $arguments -InputText $body -Doing 'open the eval issue') { Write-CmLog "eval-due: $(Get-CmEvalDueVerb 'opened' 'would open') an issue asking for an eval run on build $($newest.Number) ($why)" }
        return
    }
    $number = $older[0].Number
    if (Write-CmEvalDueGh -Arguments @('issue', 'edit', "$number", '--repo', $Ctx.Repo, '--title', $title, '--body-file', '-') -InputText $body -Doing "retitle issue #$number") {
        [void](Write-CmEvalDueGh -Arguments @('issue', 'comment', "$number", '--repo', $Ctx.Repo, '--body-file', '-') -InputText "Build $($newest.Number) changes recognition too, so this issue now asks for an eval run on build $($newest.Number)." -Doing "comment on issue #$number")
        Write-CmLog "eval-due: issue #$number $(Get-CmEvalDueVerb 'now asks' 'would now ask') for build $($newest.Number) ($why)"
    }
}

function Invoke-CmEvalDueSafely {
    <# What the watcher calls: Invoke-CmEvalDue that can neither throw nor add to the pass's output. A failure is a WARN. #>
    param($Ctx, [hashtable]$EnvMap = @{})
    try { [void](Invoke-CmEvalDue -Ctx $Ctx -EnvMap $EnvMap) }
    catch {
        $message = "$($_.Exception.Message)"
        try { Write-CmLog "eval-due: the check stopped ($message); it tries again in $([int]($script:CmEvalDueCheckSeconds / 60)) minutes" 'WARN' } catch { }   # a log that cannot be written must not stop the pass either
    }
}
