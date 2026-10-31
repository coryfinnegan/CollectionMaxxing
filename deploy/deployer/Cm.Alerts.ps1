# Alerts as GitHub issues. Dot-sourced by Cm.Deploy.ps1 (it needs Cm.Common.ps1 and the helpers there).
#
# An alert is an ALERT line in deployer.log plus a ping of <CM_HEARTBEAT_URL>/fail, which nobody hears when the URL is
# empty. So the same alert also becomes a labeled GitHub issue, once per problem rather than once per poll:
#   * Every alert has a KIND (a stable fingerprint, Get-CmAlertKinds). One open issue per kind: it is found by the `alert`
#     label plus a hidden marker in its body. A repeat adds a comment, at most one an hour, instead of a new issue.
#   * When the condition clears (Get-CmAlertClears) the deployer closes that kind's issue with a line saying what cleared it.
#   * A transient kind (Docker unreachable, the main tag moving) files nothing until it has lasted N polls in a row.
#   * What it remembers (the open issue's number, when it last wrote, today's counts, the polls in a row) lives under the
#     `alerts` key of the deployer's state file; the rest of that file is never touched. The file is read again right
#     before it is written, so a slow gh call cannot overwrite a deploy that finished in the meantime.
#   * An alert must never fail or block a deploy: every gh problem is a WARN, nothing here throws, a gh that keeps
#     failing is not asked again for 15 minutes (the alert is kept and filed when gh works), and at most 10 issue
#     writes (create, comment, close) happen a day.
#   * Everything that leaves the PC goes through Hide-CmAlertText first.
#   * The issue agent (deploy/agent) files a kind of its own the same way: it passes its definition to Invoke-CmAlertRaise,
#     which may also say who opens it (Opener), what its log is called (LogName), that it has no commits to show
#     (NoCommits) and how often it may repeat itself (RepeatSeconds).
# CM_ALERT_ISSUES=0 in .env (or the process environment) turns issue filing off; the log line and the ping stay.
#Requires -Version 7.0

$script:CmAlertRepeatSeconds = 3600     # at most one comment per kind per hour
$script:CmAlertRetrySeconds = 900       # after a gh call fails, leave that kind alone this long
$script:CmAlertWritesPerDay = 10        # create + comment + close, all kinds together
$script:CmAlertLogLines = 20            # the deployer log tail in the body
$script:CmAlertMarker = '<!-- cm-alert:{0} -->'

function Get-CmNow {
    <# The clock for alerts. Tests set $global:CmNow so an hour passes without waiting. #>
    if ($global:CmNow) { return ([DateTime]$global:CmNow).ToUniversalTime() }
    [DateTime]::UtcNow
}

function ConvertTo-CmEpoch { param([DateTime]$Utc) [DateTimeOffset]::new($Utc.ToUniversalTime()).ToUnixTimeSeconds() }

function ConvertTo-CmLong {
    <# A whole number out of the state file (JSON gives Int64, a missing entry gives $null); 0 for anything else. #>
    param($Value)
    $n = 0L
    if ($null -ne $Value -and [long]::TryParse("$Value", [ref]$n)) { return $n }
    0L
}

function Format-CmAlertTime {
    param([DateTime]$Utc, [switch]$Seconds)
    $format = if ($Seconds) { 'yyyy-MM-dd HH:mm:ss' } else { 'yyyy-MM-dd HH:mm' }
    $Utc.ToUniversalTime().ToString($format, [Globalization.CultureInfo]::InvariantCulture) + ' UTC'
}

function Get-CmAlertRepeat {
    <# Seconds between two comments on one open issue: an hour, unless the kind's definition says otherwise. #>
    param($Def)
    if ($Def -and $Def['RepeatSeconds']) { return [long]$Def['RepeatSeconds'] }
    $script:CmAlertRepeatSeconds
}

function Get-CmShortSha {
    param($Sha)
    if ("$Sha" -cmatch '^[0-9a-f]{40}$') { return "$Sha".Substring(0, 12) }
    ''
}

function Test-CmAlertIssuesEnabled {
    <# On unless CM_ALERT_ISSUES says 0, false, off or no (any case), in .env or in the process environment. #>
    param([hashtable]$EnvMap = @{}, [string]$ProcessValue = '')
    foreach ($value in @($EnvMap['CM_ALERT_ISSUES'], $ProcessValue)) {
        if ("$value".Trim() -in @('0', 'false', 'off', 'no')) { return $false }
    }
    $true
}

# ---- the kinds ---------------------------------------------------------------------------------------------------------------

function Get-CmAlertKinds {
    <#
      Every kind of alert: its issue title, labels, how many polls in a row it must last before an issue is filed (After),
      one plain sentence saying what happened, what to do, and what clears it. {LOG} becomes the deployer log's path.
      Code problems an agent can fix are agent tasks. Anything that could be tampering, or that only the owner can fix, is
      needs:cory and must never be handed to an agent: an agent must not bypass the attestation or the ancestry check.
    #>
    $agent = @('alert', 'type:infra', 'agent-task', 'p1', 'sev:blocker')
    $owner = @('alert', 'type:infra', 'needs:cory', 'p1')
    $ownerLater = @('alert', 'type:infra', 'needs:cory', 'p2')
    $fixIt = 'An agent can fix this: read the log lines below (the whole log is at {LOG} on the PC), find the cause in the repo and open a pull request with the fix and a test. Do not weaken the attestation check, the ancestry check or the config guard to make the alert go away.'
    [ordered]@{
        'deploy-rolled-back'      = @{
            Title = 'Deployer: a new version was rolled back'; Labels = $agent; After = 1
            Summary = 'A new version was started but did not come up healthy (a service crashed or the wrong build answered), so the deployer put the previous version back.'
            Todo = "$fixIt Likely causes: a service that crash-loops on start (docker compose logs names it), or containers that are not the commit that was deployed."
            Cleared = 'a later deploy succeeds, or main is the running version again'
        }
        'smoke-failed'            = @{
            Title = 'Deployer: a new version failed its smoke test'; Labels = $agent; After = 1
            Summary = 'A new version started but /health/ready never answered, so the deployer put the previous version back.'
            Todo = "$fixIt Likely causes: the API fails on start with this release (a config or startup error), or its database is not ready."
            Cleared = 'a later deploy succeeds, or main is the running version again'
        }
        'migrate-failed'          = @{
            Title = 'Deployer: a database migration failed'; Labels = $agent; After = 1
            Summary = 'The database migration of a new version failed. The running version was not touched and that commit is skipped until a newer one lands on main.'
            Todo = "$fixIt Migrations must stay expand-only. The database was dumped first when a migration file changed (the dump is in the pre-migrate folder of the backup folder)."
            Cleared = 'a later deploy succeeds, or main is the running version again'
        }
        'config-guard-refused'    = @{
            Title = 'Deployer: the config guard refused to start the stack'; Labels = $agent; After = 1
            Summary = 'The config guard refused the stack because a setting or the release compose file breaks one of its safety rules (ports, binds, mounts, privileges, a missing value), so nothing was pulled or changed.'
            Todo = "$fixIt The reason below names the rule; fix the compose file or the code that produced it, never the guard. If the cause is a missing value in .env on the PC, say so in a comment and add the needs:cory label."
            Cleared = 'a later deploy succeeds, or main is the running version again'
        }
        'deployer-crashed'        = @{
            Title = 'Deployer: the deployer script crashed'; Labels = $agent; After = 3
            Summary = 'The deployer script threw an unexpected error on three polls in a row.'
            Todo = "$fixIt The error is in the reason below; the fix is in deploy/deployer."
            Cleared = 'a run finishes normally again'
        }
        'attestation-failed'      = @{
            Title = 'Deployer: an image failed its build proof (attestation)'; Labels = $owner; After = 1
            Summary = 'A new image could not be proven to come from this repository''s release workflow on main, so the deployer refused to run it and nothing was changed.'
            Todo = 'Do not bypass this check: an image without a valid proof may have been tampered with. Open the Release images run for the new commit under Actions on GitHub. If it failed or was cancelled, re-run it; the deployer retries every 2 minutes and deploys by itself once the images carry a valid attestation. If the run looks fine, check by hand with gh attestation verify and look at who pushed to main and to the image registry. Nobody, human or agent, should loosen the verification to get past this.'
            Cleared = 'a later deploy succeeds, or main is the running version again'
        }
        'downgrade-refused'       = @{
            Title = 'Deployer: refused to deploy a commit that is not newer'; Labels = $owner; After = 1
            Summary = 'The newest main image is not a descendant of the running version (older, diverged, unknown, or its order could not be checked), so the deployer refused to go backwards.'
            Todo = 'Look at the history of main: was it reset, force-pushed or reverted? If the reason says the commit could not be fetched, check the network and the clone of the repo on the PC. The deployer never goes backwards by itself; to go back on purpose use -RollbackTo (deploy/README.md).'
            Cleared = 'a later deploy succeeds, or main is the running version again'
        }
        'deployer-setup-refused'  = @{
            Title = 'Deployer: its own state or clone is unusable'; Labels = $owner; After = 1
            Summary = 'The deployer cannot trust its own state file or its clone of the repository, so it refused to deploy anything.'
            Todo = 'Read the reason below. For a lost or unreadable state file, seed it with the commit the api is running (deploy/README.md, "If the state file is lost"); never delete a state file that still has data in it. For a clone that cannot supply the compose file, make the clone on the PC clean and on main again.'
            Cleared = 'a later run gets past the state and clone checks'
        }
        'pre-migrate-dump-failed' = @{
            Title = 'Deployer: the dump before a migration failed'; Labels = $owner; After = 1
            Summary = 'The database dump that runs before a migration failed, so nothing was migrated and the running version was left alone.'
            Todo = 'The usual cause is a full or missing backup drive: free space in the folder CM_BACKUP_DIR points to and check that Postgres is up. The deployer tries again every 2 minutes by itself. The reason below has the last line the dump printed.'
            Cleared = 'a later deploy succeeds'
        }
        'deployer-stuck'          = @{
            Title = 'Deployer: a run looks stuck (the lock is held)'; Labels = $owner; After = 1
            Summary = 'A deployer run has held its lock for over 30 minutes, so it looks stuck and no new deploy can start.'
            Todo = 'In Task Manager look for a hung pwsh, docker or gh process started by the deployer and end it; the lock frees itself. The deployer never breaks the lock on its own.'
            Cleared = 'a run finishes normally again'
        }
        'backup-unhealthy'        = @{
            Title = 'Backups: the nightly backup is overdue or unhealthy'; Labels = $owner; After = 1
            Summary = 'The backup container reports unhealthy: the newest good backup is over 30 hours old, Postgres cannot be reached, or the container is not running.'
            Todo = 'Follow deploy/README.md step 8: read the backup container logs (docker compose ... logs backup), check that the backup folder CM_BACKUP_DIR exists and has space, and run backup.sh once inside the container. Until then tonight''s data is not backed up.'
            Cleared = 'the backup container reports healthy again'
        }
        'docker-unavailable'      = @{
            Title = 'Deployer: Docker or GHCR is not reachable'; Labels = $ownerLater; After = 10
            Summary = 'Docker or the GitHub container registry has not answered for 10 polls in a row (about 20 minutes), so no deploy can happen.'
            Todo = 'Start Docker Desktop (it should start when you sign in) and check the internet connection. The deployer carries on by itself as soon as both answer.'
            Cleared = 'Docker and the registry answer again'
        }
        'main-tag-moving'         = @{
            Title = 'Deployer: the main image tag keeps moving during checks'; Labels = $ownerLater; After = 10
            Summary = 'The main image tag moved while the deployer was checking it, poll after poll for 10 polls, so it never settled on a version to deploy.'
            Todo = 'One poll absorbs a release landing mid-check; ten in a row means releases arrive faster than a check takes, or something keeps moving the tag. Look at the Release images runs under Actions and at the package page for runaway runs.'
            Cleared = 'the main tag settles'
        }
    }
}

function Get-CmAlertKind {
    <# The kind of a failed result: the one the deploy code named, else the one its outcome means. Manual rollbacks have none (someone is watching the console). #>
    param($Result)
    if ($Result.PSObject.Properties['Kind'] -and "$($Result.Kind)") { return "$($Result.Kind)" }
    $smoke = "$($Result.Reason)" -match 'smoke test failed'
    switch ("$($Result.Outcome)") {
        'rolled-back' { if ($smoke) { 'smoke-failed' } else { 'deploy-rolled-back' } }
        'failed' { if ($smoke) { 'smoke-failed' } else { 'deploy-rolled-back' } }
        'migrate-failed' { 'migrate-failed' }
        'dump-failed' { 'pre-migrate-dump-failed' }
        'config-error' { 'config-guard-refused' }
        'crashed' { 'deployer-crashed' }
        'stale-lock' { 'deployer-stuck' }
        'no-docker' { 'docker-unavailable' }
        'downgrade-refused' { 'downgrade-refused' }
        'refused' { 'attestation-failed' }
        default { '' }
    }
}

function Test-CmDockerReached {
    <# True when the poll got past the pull of the main image, which proves Docker and the registry answered. #>
    param($Result)
    $outcome = "$($Result.Outcome)"
    if ($outcome -in 'up-to-date', 'known-bad', 'deployed', 'migrate-failed', 'dump-failed', 'rolled-back', 'failed', 'downgrade-refused') { return $true }
    $outcome -eq 'refused' -and "$($Result.Kind)" -in 'attestation-failed', 'main-tag-moving'
}

function Get-CmAlertClears {
    <#
      What a finished poll proves is over: a hashtable of kind -> a sentence saying what cleared it.
        deployed, up-to-date    every deploy problem (a good deploy, or main is the running version again), but not backups
        known-bad               the main tag has settled
        any run that got past the main pull   Docker and the registry answer
        any run that ran to the end           the deployer is not stuck or crashing
      A failed deploy is NOT cleared by the quiet polls after it ('known-bad'): the bad commit is still main.
    #>
    param($Result)
    $outcome = "$($Result.Outcome)"
    $clears = @{}
    if ($outcome -in 'deployed', 'up-to-date') {
        $why = if ($outcome -eq 'deployed') { "the deploy of $(Get-CmShortSha $Result.Sha) succeeded" } else { 'main is the running version again, so there is nothing left to deploy' }
        foreach ($kind in (Get-CmAlertKinds).Keys) { if ($kind -ne 'backup-unhealthy') { $clears[$kind] = $why } }
    }
    if ($outcome -eq 'known-bad') { $clears['main-tag-moving'] = 'the main image tag has settled on a version' }
    if (Test-CmDockerReached -Result $Result) { $clears['docker-unavailable'] = 'Docker and GHCR answered again' }
    if ($outcome -notin 'crashed', 'stale-lock', 'busy') {
        $clears['deployer-stuck'] = 'the deployer ran to the end again'
        $clears['deployer-crashed'] = 'the deployer ran to the end again'
    }
    $clears
}

# ---- nothing secret leaves the PC --------------------------------------------------------------------------------------------

function Get-CmAlertSecretValues {
    <# Every .env value that is a secret or names this PC (a key that says password, secret, token, key, url, origin, domain or client id), plus the host of a URL value, longest first. #>
    param([hashtable]$EnvMap = @{})
    $values = [System.Collections.Generic.List[string]]::new()
    foreach ($key in $EnvMap.Keys) {
        if ("$key" -notmatch '(?i)PASSWORD|SECRET|TOKEN|KEY|URL|ORIGIN|DOMAIN|CLIENT_ID') { continue }
        $value = "$($EnvMap[$key])".Trim()
        if ($value.Length -lt 6) { continue }
        $values.Add($value)
        $uri = $null
        if ([Uri]::TryCreate($value, [UriKind]::Absolute, [ref]$uri) -and "$($uri.Host)".Length -ge 6) { $values.Add($uri.Host) }
    }
    @($values | Select-Object -Unique | Sort-Object -Property Length -Descending)
}

function Hide-CmAlertText {
    <#
      Text that is about to be posted to GitHub. Hide-CmSecrets masks the secrets this run knows; on top of that every
      sensitive .env value is masked, and so is anything shaped like a token, a password assignment, a tailnet host name,
      a home folder or an email address, in case a message carried one the deployer never saw.
    #>
    param([string]$Text, [hashtable]$EnvMap = @{})
    $t = Hide-CmSecrets "$Text"
    foreach ($value in (Get-CmAlertSecretValues -EnvMap $EnvMap)) { $t = $t.Replace($value, '***') }
    $t = [regex]::Replace($t, '(?i)\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})', '***')
    $t = [regex]::Replace($t, 'eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}(?:\.[A-Za-z0-9_-]*)?', '***')
    $t = [regex]::Replace($t, '(?i)\b(bearer|token|authorization:?)(\s+)[A-Za-z0-9._~+/=-]{12,}', '$1$2***')
    $t = [regex]::Replace($t, '(?i)(password|passwd|secret|api[_-]?key|token)(\s*[=:]\s*)\S+', '$1$2***')
    $t = [regex]::Replace($t, '(?i)\b(?:[a-z0-9-]+\.)+ts\.net\b', 'pc.tailnet.ts.net')
    $t = [regex]::Replace($t, '(?i)\b([a-z]:[\\/]+users[\\/]+)[^\\/\s]+', '$1<user>')
    $t = [regex]::Replace($t, '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}', '<email>')
    $t
}

function Get-CmAlertLogTail {
    param($Ctx, [int]$Lines = $script:CmAlertLogLines)
    try {
        if (-not (Test-Path -LiteralPath $Ctx.LogFile)) { return '(no log yet)' }
        $tail = @(Get-Content -LiteralPath $Ctx.LogFile -Tail $Lines -ErrorAction Stop | ForEach-Object {
                $line = "$_".Replace('```', "'''")
                if ($line.Length -gt 400) { $line.Substring(0, 400) + '...' } else { $line }
            })
        if ($tail.Count -eq 0) { return '(the log is empty)' }
        $tail -join "`n"
    }
    catch { '(the log could not be read)' }
}

# ---- the text of an issue and of a comment -----------------------------------------------------------------------------------

function Format-CmAlertBody {
    param($Ctx, [string]$Kind, $Def, $Result, [hashtable]$EnvMap, [DateTime]$Seen)
    $tick = [string][char]96
    $state = Read-CmState -Path $Ctx.StateFile
    $deployed = if ($state['current'] -is [System.Collections.IDictionary]) { Get-CmShortSha $state['current']['sha'] } else { '' }
    $new = Get-CmShortSha $Result.Sha
    $detail = ("$($Result.Reason)" -replace '\s+', ' ').Trim()
    if ($detail.Length -gt 600) { $detail = $detail.Substring(0, 600) + '...' }
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add($script:CmAlertMarker -f $Kind)
    $lines.Add("**What happened:** $($Def.Summary)")
    if ($detail) { $lines.Add(''); $lines.Add("> $detail") }
    $lines.Add('')
    $lines.Add('| | |')
    $lines.Add('|---|---|')
    $lines.Add("| Kind | $tick$Kind$tick |")
    $lines.Add("| When | $(Format-CmAlertTime $Seen -Seconds) |")
    if ("$($Result.Outcome)" -and "$($Result.Outcome)" -ne $Kind) { $lines.Add("| Outcome | $tick$($Result.Outcome)$tick (exit $($Result.ExitCode)) |") }
    if (-not $Def['NoCommits']) {
        $lines.Add("| Running commit | $(if ($deployed) { "$tick$deployed$tick" } else { 'none on record' }) |")
        $lines.Add("| New commit | $(if ($new) { "$tick$new$tick" } else { 'not known at this point' }) |")
    }
    $lines.Add('')
    $lines.Add("**What to do:** $($Def.Todo.Replace('{LOG}', $Ctx.LogFile))")
    $lines.Add('')
    $opener = if ($Def['Opener']) { $Def['Opener'] } else { 'The deployer' }
    $logName = if ($Def['LogName']) { $Def['LogName'] } else { 'deployer log' }
    $often = if ((Get-CmAlertRepeat $Def) -ge 86400) { 'a day' } else { 'an hour' }
    $lines.Add("$opener on the PC opened this issue. While the problem lasts it adds a comment at most once $often, and it closes this issue by itself when $($Def.Cleared). Secrets are hidden in everything it posts.")
    $lines.Add('')
    $lines.Add('<details>')
    $lines.Add("<summary>Last $($script:CmAlertLogLines) lines of the $logName</summary>")
    $lines.Add('')
    $lines.Add('```text')
    $lines.Add((Get-CmAlertLogTail -Ctx $Ctx))
    $lines.Add('```')
    $lines.Add('')
    $lines.Add('</details>')
    Hide-CmAlertText -Text ($lines -join "`n") -EnvMap $EnvMap
}

function Format-CmAlertComment {
    param([string]$Kind, $Result, [hashtable]$EnvMap, [DateTime]$Now, [long]$Count)
    $tick = [string][char]96
    $times = if ($Count -eq 1) { '1 time today' } else { "$Count times today" }
    $text = "Again at $(Format-CmAlertTime $Now), $times."
    if ("$($Result.Outcome)" -and "$($Result.Outcome)" -ne $Kind) { $text += " Latest outcome: $tick$($Result.Outcome)$tick (exit $($Result.ExitCode))" }
    $new = Get-CmShortSha $Result.Sha
    if ($new) { $text += " for $tick$new$tick" }
    $detail = ("$($Result.Reason)" -replace '\s+', ' ').Trim()
    if ($detail.Length -gt 600) { $detail = $detail.Substring(0, 600) + '...' }
    if ($detail) { $text += "`n`n> $detail" }
    Hide-CmAlertText -Text $text -EnvMap $EnvMap
}

# ---- what the deployer remembers: the `alerts` key of its state file ---------------------------------------------------------

function Get-CmAlertBook {
    <#
      The alert bookkeeping out of the state file: Kinds (kind -> issue, lastAt, retryAt, day, count, streak) and Writes
      (day, count, capLogged). Persist is false when the state file exists but cannot be read; the deployer then keeps
      nothing between polls and never writes the file (it would destroy what is in it).
    #>
    param($Ctx)
    $read = Read-CmStateStrict -Path $Ctx.StateFile
    $alerts = if ($read.Ok -and $read.State['alerts'] -is [System.Collections.IDictionary]) { $read.State['alerts'] } else { @{} }
    $kinds = if ($alerts['kinds'] -is [System.Collections.IDictionary]) { $alerts['kinds'] } else { @{} }
    $writes = if ($alerts['writes'] -is [System.Collections.IDictionary]) { $alerts['writes'] } else { @{} }
    [pscustomobject]@{ Persist = $read.Ok; Kinds = $kinds; Writes = $writes; Dirty = $false }
}

function Save-CmAlertBook {
    <# Reads the state file again and changes only its `alerts` key, so a deploy that finished while gh was working is not overwritten. #>
    param($Ctx, $Book)
    if (-not $Book.Persist -or -not $Book.Dirty) { return }
    $read = Read-CmStateStrict -Path $Ctx.StateFile
    if (-not $read.Ok) { return }
    $state = $read.State
    $state['alerts'] = @{ kinds = $Book.Kinds; writes = $Book.Writes }
    Write-CmState -Path $Ctx.StateFile -State $state
    $Book.Dirty = $false
}

function Get-CmAlertEntry {
    param($Book, [string]$Kind)
    if (-not $Book.Kinds.ContainsKey($Kind)) { $Book.Kinds[$Kind] = @{} }
    $Book.Kinds[$Kind]
}

function Test-CmAlertWriteBudget {
    <# True while fewer than the daily cap of issue writes were made today (UTC). Says so in the log once a day when it runs out. #>
    param($Book, [DateTime]$Now)
    $today = $Now.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
    if ("$($Book.Writes['day'])" -ne $today) {
        $Book.Writes['day'] = $today; $Book.Writes['count'] = 0; $Book.Writes['capLogged'] = $false; $Book.Dirty = $true
    }
    if ((ConvertTo-CmLong $Book.Writes['count']) -lt $script:CmAlertWritesPerDay) { return $true }
    if (-not $Book.Writes['capLogged']) {
        Write-CmLog "the cap of $($script:CmAlertWritesPerDay) issue writes a day is reached, so no more alert issues or comments are written until tomorrow (UTC); the log lines and pings go on" 'WARN'
        $Book.Writes['capLogged'] = $true; $Book.Dirty = $true
    }
    $false
}

function Add-CmAlertWrite {
    param($Book)
    $Book.Writes['count'] = (ConvertTo-CmLong $Book.Writes['count']) + 1
    $Book.Dirty = $true
}

# ---- gh ----------------------------------------------------------------------------------------------------------------------

function Invoke-CmAlertGh {
    <# One gh call. Never throws: a call that blows up comes back as a failed result. Writes only run in apply mode. #>
    param([string[]]$Arguments, [string]$InputText = '', [switch]$Write)
    $ghEnv = @{ GH_PROMPT_DISABLED = '1' }
    try {
        if ($Write) { Invoke-Cm -Mutating -Command 'gh' -Arguments $Arguments -Env $ghEnv -InputText $InputText }
        else { Invoke-Cm -Command 'gh' -Arguments $Arguments -Env $ghEnv }
    }
    catch { [pscustomobject]@{ ExitCode = 127; Output = @("$($_.Exception.Message)") } }
}

function Get-CmAlertFirstLine {
    <# The first line gh printed, secrets hidden, short: enough to say in a WARN why a call failed. #>
    param($Result)
    $line = @(@($Result.Output) | Where-Object { "$_".Trim() } | Select-Object -First 1)
    $text = (Hide-CmSecrets "$($line -join '')").Trim()
    if ($text.Length -gt 200) { $text = $text.Substring(0, 200) }
    $text
}

function Find-CmAlertIssue {
    <# The open issue of this kind (the `alert` label plus the marker in its body), the oldest if there are several. Ok is false when gh could not say. #>
    param($Ctx, [string]$Kind)
    $r = Invoke-CmAlertGh -Arguments @('issue', 'list', '--repo', $Ctx.Repo, '--label', 'alert', '--state', 'open', '--limit', '100', '--json', 'number,body,updatedAt')
    if ($r.ExitCode -ne 0) { return [pscustomobject]@{ Ok = $false; Why = (Get-CmAlertFirstLine $r); Number = 0; UpdatedAt = $null } }
    # gh may print a notice on stderr, which the runner merges into the output: parse from the first [ to the last ].
    $text = (@($r.Output) -join "`n")
    $from = $text.IndexOf('['); $to = $text.LastIndexOf(']')
    if ($from -lt 0 -or $to -lt $from) { return [pscustomobject]@{ Ok = $false; Why = 'gh did not return a list of issues'; Number = 0; UpdatedAt = $null } }
    try { $rows = @($text.Substring($from, $to - $from + 1) | ConvertFrom-Json) }
    catch { return [pscustomobject]@{ Ok = $false; Why = 'gh returned a list of issues that cannot be read'; Number = 0; UpdatedAt = $null } }
    $marker = $script:CmAlertMarker -f $Kind
    $hit = @($rows | Where-Object { "$($_.body)".Contains($marker) } | Sort-Object -Property { [int]$_.number } | Select-Object -First 1)
    if ($hit.Count -eq 0) { return [pscustomobject]@{ Ok = $true; Why = ''; Number = 0; UpdatedAt = $null } }
    [pscustomobject]@{ Ok = $true; Why = ''; Number = [int]$hit[0].number; UpdatedAt = (ConvertTo-CmUtc $hit[0].updatedAt) }
}

# ---- raising an alert --------------------------------------------------------------------------------------------------------

function Invoke-CmAlertRaise {
    <#
      One failing poll of one kind. Counts it; once a transient kind has lasted long enough, and at most once an hour per
      kind, looks for the open issue and comments on it, or opens one. Everything it learns is saved, whichever way it ends.
      An alert that could not be written (gh down, the daily cap) is remembered as `pending` and tried again by
      Invoke-CmAlertRetryPending, so a failure that happens once (a rolled-back deploy is followed by quiet polls) still
      reaches GitHub when gh works again; SeenAt (a retry) is when it first happened.
    #>
    param($Ctx, [string]$Kind, $Result, [hashtable]$EnvMap, [long]$SeenAt = 0, $Def = $null)
    $def = if ($Def) { $Def } else { (Get-CmAlertKinds)[$Kind] }
    if (-not $def) { Write-CmLog "no alert issue was filed: '$Kind' is not a kind of alert the deployer knows" 'WARN'; return }
    $retry = $SeenAt -gt 0
    $now = Get-CmNow
    $nowSec = ConvertTo-CmEpoch $now
    $seen = if ($retry) { [DateTimeOffset]::FromUnixTimeSeconds($SeenAt).UtcDateTime } else { $now }
    $book = Get-CmAlertBook -Ctx $Ctx
    $entry = Get-CmAlertEntry -Book $book -Kind $Kind
    $today = $now.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
    if (-not $retry) {
        if ("$($entry['day'])" -ne $today) { $entry['day'] = $today; $entry['count'] = 0 }
        $entry['count'] = (ConvertTo-CmLong $entry['count']) + 1
        $entry['streak'] = (ConvertTo-CmLong $entry['streak']) + 1
    }
    $book.Dirty = $true
    $defer = {
        # Not written this time: keep what is needed to write it later (only while no issue is known for this kind).
        if ((ConvertTo-CmLong $entry['issue']) -le 0) {
            $why = Hide-CmAlertText -Text ("$($Result.Reason)" -replace '\s+', ' ').Trim() -EnvMap $EnvMap
            if ($why.Length -gt 600) { $why = $why.Substring(0, 600) }
            $entry['pending'] = @{ outcome = "$($Result.Outcome)"; code = [int]$Result.ExitCode; reason = $why; sha = "$($Result.Sha)"; at = $(if ($retry) { $SeenAt } else { $nowSec }) }
        }
    }
    try {
        if (-not $retry -and $entry['streak'] -lt $def.After) {
            if ($entry['streak'] -eq 1) { Write-CmLog "no GitHub issue yet for '$Kind': it is filed only if the problem lasts $($def.After) polls in a row" }
            return
        }
        $lastAt = ConvertTo-CmLong $entry['lastAt']
        if ($lastAt -gt 0 -and ($nowSec - $lastAt) -lt (Get-CmAlertRepeat $def)) { return }
        if ($nowSec -lt (ConvertTo-CmLong $entry['retryAt'])) { & $defer; return }
        if (-not (Test-CmAlertWriteBudget -Book $book -Now $now)) { & $defer; return }

        $found = Find-CmAlertIssue -Ctx $Ctx -Kind $Kind
        if (-not $found.Ok) {
            Write-CmLog "could not look up the alert issue for '$Kind' on GitHub ($($found.Why)); the log line is still the alert, and the deployer tries again in 15 minutes" 'WARN'
            $entry['retryAt'] = $nowSec + $script:CmAlertRetrySeconds
            & $defer
            return
        }
        # Without a readable state file nothing is remembered between polls, so the issue's own last-update time keeps this to one comment an hour.
        if ($found.Number -gt 0 -and -not $book.Persist -and $found.UpdatedAt -and ($now - $found.UpdatedAt).TotalSeconds -lt (Get-CmAlertRepeat $def)) { return }

        if ($found.Number -gt 0) {
            $text = Format-CmAlertComment -Kind $Kind -Result $Result -EnvMap $EnvMap -Now $now -Count (ConvertTo-CmLong $entry['count'])
            $w = Invoke-CmAlertGh -Write -Arguments @('issue', 'comment', "$($found.Number)", '--repo', $Ctx.Repo, '--body-file', '-') -InputText $text
            $number = $found.Number
            $did = "added a comment to alert issue #$number"
        }
        else {
            $body = Format-CmAlertBody -Ctx $Ctx -Kind $Kind -Def $def -Result $Result -EnvMap $EnvMap -Seen $seen
            $arguments = @('issue', 'create', '--repo', $Ctx.Repo, '--title', $def.Title, '--body-file', '-')
            foreach ($label in $def.Labels) { $arguments += @('--label', $label) }
            $w = Invoke-CmAlertGh -Write -Arguments $arguments -InputText $body
            $number = 0
            if ($w.ExitCode -eq 0 -and ((@($w.Output) -join ' ') -match '/issues/(\d+)')) { $number = [int]$Matches[1] }
            $did = if ($number -gt 0) { "opened alert issue #$number" } else { 'opened an alert issue' }
        }
        if ($w.ExitCode -ne 0) {
            Write-CmLog "could not write the alert issue for '$Kind' on GitHub ($(Get-CmAlertFirstLine $w)); the log line is still the alert, and the deployer tries again in 15 minutes" 'WARN'
            $entry['retryAt'] = $nowSec + $script:CmAlertRetrySeconds
            & $defer
            return
        }
        Add-CmAlertWrite -Book $book
        $entry['lastAt'] = $nowSec
        $entry['retryAt'] = 0
        [void]$entry.Remove('pending')
        if ($number -gt 0) { $entry['issue'] = $number }
        Write-CmLog "$did for '$Kind'"
    }
    finally { Save-CmAlertBook -Ctx $Ctx -Book $book }
}

function Invoke-CmAlertRetryPending {
    <# Writes the alerts that could not be written earlier (see Invoke-CmAlertRaise) once their wait is over. The kind this poll just raised is left to that raise. #>
    param($Ctx, [hashtable]$EnvMap, [string]$Except = '')
    $book = Get-CmAlertBook -Ctx $Ctx
    $nowSec = ConvertTo-CmEpoch (Get-CmNow)
    $due = @(foreach ($kind in @($book.Kinds.Keys)) {
            $pending = $book.Kinds[$kind]['pending']
            if ($kind -ne $Except -and $pending -is [System.Collections.IDictionary] -and $nowSec -ge (ConvertTo-CmLong $book.Kinds[$kind]['retryAt'])) {
                [pscustomobject]@{ Kind = $kind; Pending = $pending }
            }
        })
    foreach ($item in $due) {
        $p = $item.Pending
        $result = New-CmResult "$($p['outcome'])" ([int](ConvertTo-CmLong $p['code'])) "$($p['reason'])" -Kind $item.Kind -Sha "$($p['sha'])"
        Invoke-CmAlertRaise -Ctx $Ctx -Kind $item.Kind -Result $result -EnvMap $EnvMap -SeenAt (ConvertTo-CmLong $p['at'])
    }
}

function Add-CmAlertIssue {
    <# Files (or updates) the issue for a failed result. Never throws: an alert must not fail or block a deploy. #>
    param($Ctx, $Result, [hashtable]$EnvMap)
    try {
        $kind = Get-CmAlertKind -Result $Result
        if (-not $kind) { return }
        if (-not (Test-CmAlertIssuesEnabled -EnvMap $EnvMap -ProcessValue "$env:CM_ALERT_ISSUES")) { return }
        Invoke-CmAlertRaise -Ctx $Ctx -Kind $kind -Result $Result -EnvMap $EnvMap
    }
    catch { Write-CmLog "could not file the alert issue ($($_.Exception.Message)); the log line is still the alert" 'WARN' }
}

# ---- clearing ----------------------------------------------------------------------------------------------------------------

function Invoke-CmAlertClear {
    <#
      Closes the open issue of every kind in Why (kind -> what cleared it) that the deployer opened or commented on, with
      a one-line comment. A kind that only had a count of polls in a row just forgets it. A close that fails stays
      remembered and is tried again after 15 minutes; one the daily cap holds back waits for tomorrow.
    #>
    param($Ctx, [hashtable]$Why)
    if ($Why.Count -eq 0) { return }
    $book = Get-CmAlertBook -Ctx $Ctx
    if ($book.Kinds.Count -eq 0) { return }
    $now = Get-CmNow
    $nowSec = ConvertTo-CmEpoch $now
    try {
        foreach ($kind in @($Why.Keys)) {
            if (-not $book.Kinds.ContainsKey($kind)) { continue }
            $entry = $book.Kinds[$kind]
            $number = ConvertTo-CmLong $entry['issue']
            if ($number -le 0) { [void]$book.Kinds.Remove($kind); $book.Dirty = $true; continue }
            if ($nowSec -lt (ConvertTo-CmLong $entry['retryAt'])) { continue }
            if (-not (Test-CmAlertWriteBudget -Book $book -Now $now)) { continue }
            $reason = ("$($Why[$kind])" -replace '["`\r\n]', ' ').Trim()
            $comment = "Resolved at $(Format-CmAlertTime $now): $reason. Closing this issue; it opens again if the problem comes back."
            $w = Invoke-CmAlertGh -Write -Arguments @('issue', 'close', "$number", '--repo', $Ctx.Repo, '--comment', $comment)
            if ($w.ExitCode -ne 0) {
                Write-CmLog "could not close alert issue #$number for '$kind' on GitHub ($(Get-CmAlertFirstLine $w)); the deployer tries again in 15 minutes" 'WARN'
                $entry['retryAt'] = $nowSec + $script:CmAlertRetrySeconds
                $book.Dirty = $true
                continue
            }
            Add-CmAlertWrite -Book $book
            [void]$book.Kinds.Remove($kind)
            $book.Dirty = $true
            Write-CmLog "closed alert issue #$number for '$kind': $reason"
        }
    }
    finally { Save-CmAlertBook -Ctx $Ctx -Book $book }
}

function Reset-CmAlertStreaks {
    <# A transient kind that was not seen on this poll starts counting again from zero, unless its issue is already open (Invoke-CmAlertClear closes that one). #>
    param($Ctx, [string]$Except = '')
    $book = Get-CmAlertBook -Ctx $Ctx
    if ($book.Kinds.Count -eq 0) { return }
    $kinds = Get-CmAlertKinds
    foreach ($kind in @($book.Kinds.Keys)) {
        if ($kind -eq $Except) { continue }
        if (-not $kinds.Contains($kind) -or $kinds[$kind].After -le 1) { continue }
        if ((ConvertTo-CmLong $book.Kinds[$kind]['issue']) -gt 0) { continue }
        [void]$book.Kinds.Remove($kind)
        $book.Dirty = $true
    }
    Save-CmAlertBook -Ctx $Ctx -Book $book
}

# ---- backups -----------------------------------------------------------------------------------------------------------------

function Get-CmBackupHealth {
    <#
      What the backup container's HEALTHCHECK (backup.sh health) says, read with docker inspect: Known is false when it
      cannot be told (docker failed, no health check, still starting), Healthy and Detail otherwise. A backup container
      that is not running at all is unhealthy: there will be no nightly backup. Read only.
    #>
    param($Ctx)
    $unknown = [pscustomobject]@{ Known = $false; Healthy = $true; Detail = '' }
    $ps = Invoke-Cm -Command 'docker' -Env @{ CM_TAG = 'backup-health' } -Arguments (Get-CmComposeArgs $Ctx @('ps', '-q', 'backup'))
    if ($ps.ExitCode -ne 0) { return $unknown }
    $id = "$(@($ps.Output | Where-Object { "$_".Trim() } | Select-Object -First 1))".Trim()
    if (-not $id) { return [pscustomobject]@{ Known = $true; Healthy = $false; Detail = 'the backup container is not running' } }
    $r = Invoke-Cm -Command 'docker' -Arguments @('inspect', $id, '--format', '{{json .State.Health}}')
    if ($r.ExitCode -ne 0) { return $unknown }
    $text = (@($r.Output) -join "`n").Trim()
    if (-not $text -or $text -eq 'null') { return $unknown }
    try { $health = $text | ConvertFrom-Json } catch { return $unknown }
    switch ("$($health.Status)") {
        'healthy' { return [pscustomobject]@{ Known = $true; Healthy = $true; Detail = '' } }
        'unhealthy' {
            $said = ''
            if ($health.Log) { $said = ("$(@($health.Log)[-1].Output)" -replace '\s+', ' ').Trim() }
            return [pscustomobject]@{ Known = $true; Healthy = $false; Detail = $(if ($said) { $said } else { 'the backup container reports unhealthy' }) }
        }
        default { return $unknown }
    }
}

function Update-CmBackupAlert {
    <# Raises backup-unhealthy while the backup container is unhealthy and clears it when it is healthy again. A container that is still starting changes nothing. #>
    param($Ctx, [hashtable]$EnvMap)
    $health = Get-CmBackupHealth -Ctx $Ctx
    if (-not $health.Known) { return }
    if ($health.Healthy) { Invoke-CmAlertClear -Ctx $Ctx -Why @{ 'backup-unhealthy' = 'the backup container reports healthy again' }; return }
    Invoke-CmAlertRaise -Ctx $Ctx -Kind 'backup-unhealthy' -Result (New-CmResult 'backup-unhealthy' 0 $health.Detail -Kind 'backup-unhealthy') -EnvMap $EnvMap
}

# ---- after every poll ----------------------------------------------------------------------------------------------------------

function Complete-CmAlertChecks {
    <#
      Runs after each real poll that actually ran (not a busy one, not a manual rollback): closes the issues this poll
      proves are over, restarts the count of transient problems it did not see, and on a poll that ended well reads the
      backup container's health. A quiet poll with nothing open and a healthy backup makes no gh call at all. Never throws.
    #>
    param($Ctx, $Result)
    try {
        if ("$($Result.Outcome)" -eq 'busy') { return }
        $envMap = Initialize-CmRun -Ctx $Ctx
        if (-not (Test-CmAlertIssuesEnabled -EnvMap $envMap -ProcessValue "$env:CM_ALERT_ISSUES")) { return }
        $seen = if ($Result.ExitCode -ne 0) { Get-CmAlertKind -Result $Result } else { '' }
        Reset-CmAlertStreaks -Ctx $Ctx -Except $seen
        Invoke-CmAlertClear -Ctx $Ctx -Why (Get-CmAlertClears -Result $Result)
        Invoke-CmAlertRetryPending -Ctx $Ctx -EnvMap $envMap -Except $seen
        if ($Result.ExitCode -eq 0 -and "$($Result.Outcome)" -in 'deployed', 'up-to-date', 'known-bad') {
            Update-CmBackupAlert -Ctx $Ctx -EnvMap $envMap
        }
    }
    catch { Write-CmLog "could not update the alert issues ($($_.Exception.Message)); the deploy is not affected" 'WARN' }
}
