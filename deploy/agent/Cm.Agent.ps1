# The issue agent: a scheduled watcher that turns labelled GitHub issues into one headless Claude Code run each.
# Dot-source after deployer/Cm.Common.ps1; it has no side effects when loaded.
#
# COST MODEL (read this before changing anything)
#   * A watcher pass is plain PowerShell plus `gh` calls. It never starts a model. The common pass (nothing queued) is
#     one `gh issue list` call; a pass with the switch off, the daily cap reached or a run still alive makes none.
#   * A pass that found an issue and has no token file also runs `claude auth status` once, before it claims the issue.
#     That is a local read of the CLI's sign-in: no model, no network call of its own. With a token file, or with nothing
#     queued, it is not run at all.
#   * A model only runs inside Run-AgentIssue.ps1, started detached by a pass that found an issue, one run at a time.
#
# SIGN-IN (why a long-lived token)
#   * The CLI's own sign-in is a short-lived access token plus a refresh token that lapses when the CLI sits unused, and this
#     service is idle for days at a time. So the runs use a token from `claude setup-token` (valid for about a year, it does not
#     lapse with idleness) that the owner saves once in <Root>\agent-secrets\claude-token. Run-AgentIssue gives it to the claude
#     child as CLAUDE_CODE_OAUTH_TOKEN, in that child's environment and nowhere else. The file is not in the secrets folder
#     (CM_SECRETS_DIR): compose mounts that into the api container, and no compose service mounts anything under <Root> itself.
#   * With no token file the CLI's own sign-in is used, as before. If that is signed out the watcher picks nothing and opens one
#     alert issue for the owner (needs:cory); it closes the issue once a token file exists or the CLI is signed in again, and
#     lifts the pause that failed starts left behind. A token that stops working (runs that used it fail at once) is a second
#     owner alert, agent-token-failed, which closes on the next run that gets past start-up.
#   * An alert is never allowed to stop a pass: every call into the alert code is guarded and a failure is a WARN.
#
# SAFETY MODEL
#   * Every external command goes through Invoke-Cm (see Cm.Common.ps1), so a test can prove which commands would run.
#     Anything that changes the machine or GitHub is -Mutating and only runs with -Apply; without it the pass prints
#     what it would do. The detached start and the process table have their own seams (Start-CmProcess,
#     Get-CmProcessInfo) for the same reason.
#   * Only issues authored by the repo owner are ever worked. The watcher never removes a folder it did not make.
#   * Anything posted to a public issue goes through Hide-CmAgentText first.
#Requires -Version 7.0

# The alert issues (Cm.Alerts.ps1, which needs the state reader in Cm.Deploy.ps1). Loaded from here so every script that
# dot-sources this file gets them, as Cm.Deploy.ps1 does for the deployer.
. (Join-Path $PSScriptRoot '../deployer/Cm.Deploy.ps1')

# An issue carrying any of these is somebody's: another run, a blocked one, or waiting for the owner.
$script:CmAgentSkipLabels = @('agent-working', 'agent-blocked', 'needs:cory', 'status:blocked')
$script:CmAgentSignedOutKind = 'agent-signed-out'
$script:CmAgentTokenFailedKind = 'agent-token-failed'
# `claude auth status` reads local files and answers in about a second; a copy that hangs must not hold the watcher lock.
$script:CmAgentAuthStatusSeconds = 20
$script:CmAgentLogRepeatSeconds = 3600   # a warning that would repeat on every pass is written at most this often
$script:CmAgentDefaultMaxRuns = 8
$script:CmAgentDefaultMaxMinutes = 180
# An opus run (tier:critical) gets at least this long: it builds, runs the gate, has its change verified and waits for CI more than once, and 180 minutes stopped #614 just before its merge.
$script:CmAgentOpusMinMinutes = 300
# A run that GitHub could not be asked about is retried every pass; after this many passes (an hour) the lock is dropped
# so the service cannot stay stuck behind an issue that was deleted or a gh that is not logged in.
$script:CmAgentFinishAttempts = 12
$script:CmAgentLogTailLines = 30
$script:CmAgentLogHeadLines = 10
# A non-zero exit sooner than this after the start is a failure to start (sign-in, limits, flags), not a failed piece of work.
$script:CmAgentQuickFailureSeconds = 300
$script:CmAgentPauseMinutes = 60
$script:CmAgentPauseMaxMinutes = 480
# A headless run ends the moment it replies without calling a tool, so a command that moves to the background strands it
# "waiting for a notification" (#617, #618). CLAUDE_CODE_DISABLE_BACKGROUND_TASKS removes run_in_background and stops a shell
# command that outlives the tool time limit from being moved to the background on its own. Monitor and ScheduleWakeup still
# background, so they are denied by name. The variable goes to the claude child alone, never to the machine or the user.
$script:CmAgentBackgroundTools = 'Monitor,ScheduleWakeup'
# The scheduled task the installer registers (Install-AgentWatcher.ps1); the runner wakes it when a run ends.
$script:CmAgentTaskName = 'CollectionMaxxingAgent'

function New-CmAgentResult {
    <# Exit codes: 0 fine or nothing to do, 1 a run could not be started, 2 config refused, 3 GitHub not reachable. #>
    param([string]$Outcome, [int]$Code = 0, [string]$Reason = '', [int]$Issue = 0)
    [pscustomobject]@{ Outcome = $Outcome; ExitCode = $Code; Reason = $Reason; Issue = $Issue }
}

function Get-CmAgentNow {
    if ($global:CmAgentNow) { return [DateTimeOffset](& $global:CmAgentNow) }
    [DateTimeOffset]::Now
}

function New-CmAgentContext {
    param(
        [string]$Root = 'C:\CollectionMaxxing',
        [string]$Repo = 'coryfinnegan/CollectionMaxxing',
        [string]$AgentDir = $PSScriptRoot
    )
    [pscustomobject]@{
        Root            = $Root
        Repo            = $Repo
        Owner           = ($Repo -split '/')[0]
        EnvFile         = Join-Path $Root '.env'
        StateFile       = Join-Path $Root 'state/agent-state.json'
        LockFile        = Join-Path $Root 'state/agent.lock'
        WatcherLockFile = Join-Path $Root 'state/agent-watcher.lock'
        LogFile         = Join-Path $Root 'logs/agent-watcher.log'
        RunLogDir       = Join-Path $Root 'logs/agent'
        DeployerClone   = Join-Path $Root 'repo'   # the deployer's own checkout: never a place to make worktrees
        PromptFile      = Join-Path $AgentDir 'agent-prompt.md'
        RunnerScript    = Join-Path $AgentDir 'Run-AgentIssue.ps1'
    }
}

# ---- settings from .env ------------------------------------------------------------------------------------------------

function Get-CmAgentSettings {
    <#
      On unless CM_AGENT_ENABLED says 0, false, off or no (the same words as CM_PRUNE_IMAGES). The checkout defaults to
      the owner's usual place under the home folder, resolved at run time so no account name is written in the repo.
      A number that is not a number falls back to its default; 0 runs a day pauses new runs.
    #>
    param([hashtable]$EnvMap = @{}, [string]$Root = '')
    $number = {
        param($name, $default, $min)
        $n = 0
        if ([int]::TryParse("$($EnvMap[$name])".Trim(), [ref]$n) -and $n -ge $min) { $n } else { $default }
    }
    $repo = "$($EnvMap['CM_AGENT_REPO'])".Trim()
    if (-not $repo) { $repo = [IO.Path]::Combine("$HOME", 'source', 'repos', 'CollectionMaxxing') }
    [pscustomobject]@{
        Enabled        = ("$($EnvMap['CM_AGENT_ENABLED'])".Trim().ToLowerInvariant() -notin @('0', 'false', 'off', 'no'))
        Repo           = $repo
        MaxRunsPerDay  = [int](& $number 'CM_AGENT_MAX_RUNS_PER_DAY' $script:CmAgentDefaultMaxRuns 0)
        MaxMinutes     = [int](& $number 'CM_AGENT_MAX_MINUTES' $script:CmAgentDefaultMaxMinutes 1)
        TokenFile      = Get-CmAgentTokenFile -Root $Root
        EnvMap         = $EnvMap
    }
}

function Get-CmAgentTokenFile {
    <#
      Where the long-lived Claude Code token is kept: <Root>\agent-secrets\claude-token, outside the repo. Not in the secrets
      folder (CM_SECRETS_DIR): compose mounts that read-write into the api container, so a file-read bug in the api would
      reach the token. Nothing under <Root> is mounted into any container, and the deployer's config guard allows only the
      folders .env names plus the compose file's own, so a release cannot start mounting it. '' when there is no root.
    #>
    param([string]$Root = '')
    if (-not $Root) { return '' }
    [IO.Path]::Combine($Root, 'agent-secrets', 'claude-token')
}

function Read-CmAgentToken {
    <#
      The token in the token file, or $null when there is none to use. The file holds the token on one line, as `claude
      setup-token` prints it; blank lines and # lines are skipped, and a CLAUDE_CODE_OAUTH_TOKEN= prefix (with or without
      `export`) and quotes around the value are tolerated. A value with a space in it is not a token.
    #>
    param([string]$Path)
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $line = @(Get-Content -LiteralPath $Path -ErrorAction SilentlyContinue | Where-Object { "$_".Trim() -and "$_".Trim() -notmatch '^#' } | Select-Object -First 1)
    if ($line.Count -eq 0) { return $null }
    $value = "$($line[0])".Trim() -replace '^(?:export\s+)?CLAUDE_CODE_OAUTH_TOKEN\s*=\s*', ''
    if ($value.Length -ge 2 -and (($value[0] -eq '"' -and $value[-1] -eq '"') -or ($value[0] -eq "'" -and $value[-1] -eq "'"))) { $value = $value.Substring(1, $value.Length - 2) }
    if (-not $value -or $value -match '\s') { return $null }
    $value
}

function Get-CmAgentSecrets {
    <# The .env values that must never reach a public issue comment. Values under 6 characters are left out: they would mask half the log. #>
    param([hashtable]$EnvMap = @{})
    @($EnvMap.GetEnumerator() |
        Where-Object { $_.Key -match '(PASSWORD|SECRET|TOKEN|API_KEY|_KEY$|HEARTBEAT)' -and "$($_.Value)".Length -ge 6 } |
        ForEach-Object { "$($_.Value)" })
}

function Hide-CmAgentText {
    <# For text that is about to be posted on GitHub: known secrets, terminal colour codes, token shapes and the home folder. #>
    param([string]$Text)
    $Text = Hide-CmSecrets $Text
    $Text = [regex]::Replace($Text, '\x1b\[[0-9;?]*[ -/]*[@-~]', '')
    foreach ($pattern in 'gh[pousr]_[A-Za-z0-9]{20,}', 'github_pat_[A-Za-z0-9_]{20,}', 'sk-ant-[A-Za-z0-9_\-]{10,}',
        'eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{5,}') {
        $Text = [regex]::Replace($Text, $pattern, '***')
    }
    $Text = [regex]::Replace($Text, '(?i)\b(authorization:\s*)(bearer|basic|token)\s+\S+', '$1$2 ***')
    $homeFolder = "$HOME"
    if ($homeFolder.Length -gt 3) {
        foreach ($form in @($homeFolder, ($homeFolder -replace '\\', '/'))) {
            $Text = [regex]::Replace($Text, [regex]::Escape($form), '~', 'IgnoreCase')
        }
    }
    $Text
}

# ---- seams: the process table, a detached start, the claude lookup ---------------------------------------------------------

function Get-CmProcessInfo {
    <# Id, Name and StartTime (UTC) of a running process, or $null when there is none. Tests set $global:CmProcessTable. #>
    param([int]$Id)
    if ($global:CmProcessTable) { return & $global:CmProcessTable $Id }
    if ($env:CM_TEST -eq '1') { throw 'CM_TEST=1 but no fake process table is installed; refusing to read the real one.' }
    $p = Get-Process -Id $Id -ErrorAction SilentlyContinue
    if (-not $p) { return $null }
    $started = $null
    try { $started = $p.StartTime.ToUniversalTime() } catch { }
    [pscustomobject]@{ Id = $p.Id; Name = $p.ProcessName; StartTime = $started }
}

function Start-CmProcess {
    <#
      Starts a process and returns it (it has .Id, and .WaitForExit / .ExitCode when real). Like a -Mutating Invoke-Cm
      call, without -Apply it is printed and nothing starts. Tests set $global:CmStartProcess.
      -Env is extra environment for the new process alone (a secret goes this way, never on the command line): the
      variables are set around the start and put back, and only their names are ever printed.
    #>
    param([Parameter(Mandatory)][string]$File, [string]$Arguments = '', [string]$WorkingDirectory = '', [switch]$NoNewWindow, [hashtable]$Env = @{})
    $display = (@($File, $Arguments) | Where-Object { $_ }) -join ' '
    if (-not $global:CmApply) {
        $names = if ($Env.Count -gt 0) { "  (environment: $((@($Env.Keys) | Sort-Object) -join ', '))" } else { '' }
        Write-CmLog "dry run, would start: $display$names"
        return [pscustomobject]@{ Id = 0; DryRun = $true }
    }
    if ($global:CmStartProcess) { return & $global:CmStartProcess $File $Arguments $WorkingDirectory $Env }
    if ($env:CM_TEST -eq '1') { throw "CM_TEST=1 but no fake process starter is installed; refusing to start '$display' for real." }
    $splat = @{ FilePath = $File; PassThru = $true }
    if ($Arguments) { $splat.ArgumentList = $Arguments }   # one string, passed through as the command line
    if ($WorkingDirectory) { $splat.WorkingDirectory = $WorkingDirectory }
    if ($NoNewWindow) { $splat.NoNewWindow = $true } else { $splat.WindowStyle = 'Hidden' }
    $saved = @{}
    foreach ($key in $Env.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, [string]$Env[$key])
    }
    try { $process = Start-Process @splat }
    finally { foreach ($key in $Env.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) } }
    $null = $process.Handle   # keeps the exit code readable after the process ends
    $process
}

function Resolve-CmAgentClaude {
    <# The claude to run: the one on PATH, else the native installer's folder. Resolved here so no account name is written in the repo. #>
    if ($null -ne $global:CmClaudePath) { if ($global:CmClaudePath) { return $global:CmClaudePath } else { return $null } }
    $found = Get-Command claude -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { return $found.Source }
    foreach ($name in 'claude.exe', 'claude') {
        $candidate = [IO.Path]::Combine("$HOME", '.local', 'bin', $name)
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    $null
}

# ---- is Claude Code signed in -----------------------------------------------------------------------------------------------------

function Get-CmAgentAlertKind {
    <#
      The alert issue for a signed-out Claude Code, shaped like an entry of Get-CmAlertKinds (Cm.Alerts.ps1). It is the
      owner's: nothing an agent can do signs the CLI in, so it is needs:cory and never an agent-task. It repeats itself
      once a day at most, because a signed-out service has nothing new to say.
    #>
    @{
        Title         = 'Issue agent: Claude Code is signed out'
        Labels        = @('alert', 'type:infra', 'needs:cory', 'p1')
        After         = 1
        RepeatSeconds = 86400
        Opener        = 'The issue agent''s watcher'
        LogName       = 'watcher log'
        NoCommits     = $true
        Summary       = 'Claude Code on the PC is signed out and no usable long-lived token is saved, so the issue agent cannot start a run. The watcher picked nothing up: every issue queued with agent-task is untouched and waits.'
        Todo          = 'Sign in once, for good. In a normal terminal on the PC, as the Windows account the agent runs as, run `claude setup-token` and sign in in the browser window it opens. Save the long-lived token it prints as the only line of the file `agent-secrets\claude-token`, in the folder that holds the deployer''s `.env` (normally C:\CollectionMaxxing), replacing whatever the file holds. Do not paste the token into this issue or into a comment. It lasts about a year and, unlike the CLI''s own sign-in, does not lapse while the agent sits idle. A command that saves it without showing it is in deploy/README.md under "The issue agent". The next watcher pass after that picks the queued issues up by itself, and this issue closes.'
        Cleared       = 'Claude Code is signed in again (a token file is saved, or the CLI says it is signed in)'
    }
}

function Get-CmAgentTokenAlertKind {
    <#
      The alert issue for a saved token that stopped working: runs that used it failed within minutes of starting. Like the
      signed-out alert it is the owner's (needs:cory, never an agent-task) and repeats once a day at most.
    #>
    @{
        Title         = 'Issue agent: runs fail at start-up with the saved Claude Code token'
        Labels        = @('alert', 'type:infra', 'needs:cory', 'p1')
        After         = 1
        RepeatSeconds = 86400
        Opener        = 'The issue agent''s watcher'
        LogName       = 'watcher log'
        NoCommits     = $true
        Summary       = 'Runs that use the saved Claude Code token have failed within minutes of starting. The usual cause is a token that has expired (they last about a year) or been revoked. After each failure the issue agent pauses and tries again later.'
        Todo          = 'First read the log lines in the comment on the queued issue. If they name something else, such as a usage limit, the token is fine and this issue closes by itself once a run gets past start-up. If they say the sign-in failed, run `claude setup-token` again on the PC, as the Windows account the agent runs as, and save the new token as the only line of the file `agent-secrets\claude-token`, in the folder that holds the deployer''s `.env` (normally C:\CollectionMaxxing), replacing what is there. Do not paste the token into this issue or into a comment. A new token file lifts the pause by itself.'
        Cleared       = 'a run gets past start-up again'
    }
}

function Write-CmAgentLogHourly {
    <#
      A warning that would otherwise be written on every 5-minute pass: written at most once an hour. The time of the last
      one is kept in the state file under Key, so it holds with alert issues switched off and when the alert book cannot
      be read. Writes the state file only when it logs.
    #>
    param($Ctx, [string]$Key, [string]$Message)
    $now = (Get-CmAgentNow).ToUnixTimeSeconds()
    $state = Read-CmState -Path $Ctx.StateFile
    if ($now - [long]$state[$Key] -lt $script:CmAgentLogRepeatSeconds) { return }
    Write-CmLog $Message 'WARN'
    $state[$Key] = $now
    Write-CmState -Path $Ctx.StateFile -State $state
}

function Set-CmAgentAlert {
    <# Opens (or repeats) an alert issue. An alert must never stop a pass, so a failure here is a WARN, as Add-CmAlertIssue does for the deployer. #>
    param($Ctx, $Settings, [string]$Kind, $Def, [string]$Reason)
    if (-not (Test-CmAlertIssuesEnabled -EnvMap $Settings.EnvMap -ProcessValue "$env:CM_ALERT_ISSUES")) { return }
    try {
        $result = [pscustomobject]@{ Outcome = $Kind; ExitCode = 2; Reason = $Reason; Kind = $Kind; Sha = '' }
        Invoke-CmAlertRaise -Ctx $Ctx -Kind $Kind -Def $Def -Result $result -EnvMap $Settings.EnvMap
    }
    catch { Write-CmLog "could not file the alert issue for '$Kind' ($($_.Exception.Message)); the pass carries on" 'WARN' }
}

function Clear-CmAgentAlert {
    <# Closes an alert issue this service opened. Guarded like Set-CmAgentAlert. #>
    param($Ctx, $Settings, [string]$Kind, [string]$Why)
    if (-not (Test-CmAlertIssuesEnabled -EnvMap $Settings.EnvMap -ProcessValue "$env:CM_ALERT_ISSUES")) { return }
    try { Invoke-CmAlertClear -Ctx $Ctx -Why @{ $Kind = $Why } }
    catch { Write-CmLog "could not close the alert issue for '$Kind' ($($_.Exception.Message)); the pass carries on" 'WARN' }
}

function Test-CmAgentAlertOpen {
    <# True while the book of alerts remembers an issue (or one still to be filed) of this kind. #>
    param($Ctx, [string]$Kind)
    (Get-CmAlertBook -Ctx $Ctx).Kinds.ContainsKey($Kind)
}

function Get-CmAgentSignIn {
    <#
      Whether a run can sign in, as @{ State; Why; Malformed; Problem }: State is 'in' (a token file is there, or `claude auth
      status` says loggedIn), 'out' (no usable token and the CLI says it is not signed in) or 'unknown' (no claude, it
      printed no JSON, or it did not answer in time: the caller carries on as it did before this check existed).
      Malformed is true for a token file that exists but holds no usable token; Problem says in words why there is no token.
      `claude auth status` reads only the CLI's own sign-in and cannot tell whether a token is still good, so with a token
      it is not asked. It exits 1 when signed out and still prints the JSON.
    #>
    param($Ctx, $Settings)
    $none = { param($state, $why = '') [pscustomobject]@{ State = $state; Why = $why; Malformed = $malformed; Problem = $problem } }
    if (Read-CmAgentToken -Path $Settings.TokenFile) { $malformed = $false; $problem = ''; return & $none 'in' 'a token file is saved' }
    $malformed = [bool]($Settings.TokenFile -and (Test-Path -LiteralPath $Settings.TokenFile -PathType Leaf))
    $problem = if ($malformed) { 'the token file exists but holds no usable token (it needs one line with the token and no spaces in it)' } else { 'no token file is saved' }
    $claude = Resolve-CmAgentClaude
    if (-not $claude) { return & $none 'unknown' }
    $r = Invoke-Cm -Command $claude -Arguments @('auth', 'status') -TimeoutSeconds $script:CmAgentAuthStatusSeconds
    $text = (@($r.Output) -join "`n")
    $from = $text.IndexOf('{'); $to = $text.LastIndexOf('}')
    $loggedIn = $null
    if ($from -ge 0 -and $to -gt $from) {
        try { $loggedIn = ($text.Substring($from, $to - $from + 1) | ConvertFrom-Json).loggedIn } catch { }
    }
    if ($loggedIn -eq $true) { return & $none 'in' 'Claude Code is signed in again' }
    if ($loggedIn -eq $false) { return & $none 'out' }
    if ($r.ExitCode -eq 124) {
        Write-CmAgentLogHourly -Ctx $Ctx -Key 'authHangLogUnix' -Message "claude auth status did not answer within $($script:CmAgentAuthStatusSeconds) seconds, so the sign-in is not checked and the pass carries on"
    }
    & $none 'unknown'
}

function Confirm-CmAgentSignIn {
    <#
      Looks at the sign-in and keeps the alert issue in step with it: opens it (once per problem) while Claude Code is
      signed out, closes it when it is not and lifts the pause and streak that failed starts left behind (they were most
      likely the missing sign-in). Returns $false only when it is signed out, and then no run may start.
    #>
    param($Ctx, $Settings)
    $kind = $script:CmAgentSignedOutKind
    $sign = Get-CmAgentSignIn -Ctx $Ctx -Settings $Settings
    if ($sign.State -eq 'out') {
        Write-CmAgentLogHourly -Ctx $Ctx -Key 'signedOutLogUnix' -Message "Claude Code is signed out and $($sign.Problem), so no issue is picked up until it is signed in; run claude setup-token (deploy/README.md, ""The issue agent"")"
        Set-CmAgentAlert -Ctx $Ctx -Settings $Settings -Kind $kind -Def (Get-CmAgentAlertKind) -Reason "claude auth status says Claude Code is not signed in, and $($sign.Problem)."
        return $false
    }
    if ($sign.Malformed) {
        Write-CmAgentLogHourly -Ctx $Ctx -Key 'tokenFileLogUnix' -Message "the token file exists but holds no usable token (it needs one line with the token and no spaces in it), so the CLI's own sign-in is used"
    }
    if ($sign.State -eq 'in' -and (Test-CmAgentAlertOpen -Ctx $Ctx -Kind $kind)) {
        Clear-CmAgentAlert -Ctx $Ctx -Settings $Settings -Kind $kind -Why $sign.Why
        Reset-CmAgentBackoff -Ctx $Ctx
    }
    $true
}

function Update-CmAgentTokenSeen {
    <#
      A token file that is new, or was replaced (its modified time differs from the one last seen), ends the pause and the
      streak of failed starts: they were most likely the missing or expired sign-in this file replaces. The time last seen
      is kept in the state file; nothing is written when it is unchanged.
    #>
    param($Ctx, $Settings)
    if (-not (Read-CmAgentToken -Path $Settings.TokenFile)) { return }
    $stamp = [DateTimeOffset]::new((Get-Item -LiteralPath $Settings.TokenFile).LastWriteTimeUtc).ToUnixTimeSeconds()
    $state = Read-CmState -Path $Ctx.StateFile
    if ([long]$state.tokenSeenUnix -eq $stamp) { return }
    $lifted = [long]$state.pauseUntilUnix -gt 0 -or [int]$state.quickFailures -gt 0
    $state.tokenSeenUnix = $stamp
    if ($state.ContainsKey('quickFailures')) { $state.quickFailures = 0 }
    [void]$state.Remove('pauseUntilUnix')
    Write-CmState -Path $Ctx.StateFile -State $state
    if ($lifted) { Write-CmLog 'a new token file was found, so the pause after failed starts is lifted' }
}

# ---- choosing the issue ---------------------------------------------------------------------------------------------------------

function Get-CmAgentLabelNames {
    param($Item)
    @(@($Item.labels) | ForEach-Object { if ($_ -is [string]) { $_ } else { $_.name } } | Where-Object { $_ })
}

function Get-CmAgentRunMinutes {
    <# How long a run may take: CM_AGENT_MAX_MINUTES, and for an opus run at least CmAgentOpusMinMinutes. #>
    param($Settings, [string]$Model)
    if ($Model -eq 'opus') { return [int][math]::Max([int]$Settings.MaxMinutes, $script:CmAgentOpusMinMinutes) }
    [int]$Settings.MaxMinutes
}

function Get-CmAgentLockMinutes {
    <# The limit a running run is judged by: the one it started with, or CM_AGENT_MAX_MINUTES for a lock written before runs kept their own. #>
    param($Lock, $Settings)
    if ($Lock.maxMinutes) { return [int]$Lock.maxMinutes }
    [int]$Settings.MaxMinutes
}

function Get-CmAgentModel {
    <#
      tier:critical gets opus, everything else sonnet, tier:mechanical included: a run is headless in --permission-mode auto,
      and haiku gets no auto approvals there, so its every edit and command waits for a person and the run ends blocked
      (issue #603, 2026-10-31). A clash goes to the stronger model.
    #>
    param([string[]]$Labels = @())
    if ($Labels -contains 'tier:critical') { return 'opus' }
    'sonnet'
}

function Get-CmAgentPriority {
    <# p1 (highest) to p5; no p label counts as p3. With several, the highest wins. #>
    param([string[]]$Labels = @())
    $best = $null
    foreach ($label in $Labels) {
        if ($label -match '^p([1-5])$') { $p = [int]$Matches[1]; if ($null -eq $best -or $p -lt $best) { $best = $p } }
    }
    if ($null -eq $best) { 3 } else { $best }
}

function Select-CmAgentIssue {
    <#
      From the `gh issue list` rows: only agent-task issues authored by the owner (the repo is public; guard-issues.yml
      closes everyone else's, and this checks anyway) that carry none of the skip labels. Highest priority first,
      then oldest, then lowest number. Returns the chosen row or $null.
    #>
    param($Issues, [string]$Owner)
    $rows = foreach ($issue in @($Issues)) {
        if ($null -eq $issue) { continue }
        $labels = Get-CmAgentLabelNames $issue
        if ($labels -notcontains 'agent-task') { continue }
        if ("$($issue.author.login)" -ine $Owner) { continue }
        if (@($labels | Where-Object { $_ -in $script:CmAgentSkipLabels }).Count -gt 0) { continue }
        $created = ConvertTo-CmUtc $issue.createdAt
        [pscustomobject]@{
            Number   = [int]$issue.number
            Title    = "$($issue.title)"
            Labels   = $labels
            Priority = Get-CmAgentPriority $labels
            Created  = if ($created) { $created } else { [DateTime]::MaxValue }
            Model    = Get-CmAgentModel $labels
        }
    }
    @($rows) | Sort-Object -Property Priority, Created, Number | Select-Object -First 1
}

# ---- GitHub ---------------------------------------------------------------------------------------------------------------------

function Set-CmIssueLabels {
    <# One `gh issue edit`. Only labels that are known to be there are passed to --remove-label. #>
    param($Ctx, [int]$Issue, [string[]]$Add = @(), [string[]]$Remove = @())
    if (@($Add).Count -eq 0 -and @($Remove).Count -eq 0) { return $true }
    $arguments = @('issue', 'edit', "$Issue", '--repo', $Ctx.Repo)
    foreach ($label in $Add) { $arguments += @('--add-label', $label) }
    foreach ($label in $Remove) { $arguments += @('--remove-label', $label) }
    $r = Invoke-Cm -Command 'gh' -Arguments $arguments -Mutating
    $r.ExitCode -eq 0
}

function Add-CmIssueComment {
    <# The body goes in on standard input: no quoting problems, no length limit, and nothing secret on a command line. #>
    param($Ctx, [int]$Issue, [string]$Body)
    $r = Invoke-Cm -Command 'gh' -Arguments @('issue', 'comment', "$Issue", '--repo', $Ctx.Repo, '--body-file', '-') -Mutating -InputText (Hide-CmAgentText $Body)
    $r.ExitCode -eq 0
}

function Get-CmAgentIssueState {
    <# State (OPEN or CLOSED) and label names of one issue, or $null when GitHub could not be asked. #>
    param($Ctx, [int]$Issue)
    $r = Invoke-Cm -Command 'gh' -Arguments @('issue', 'view', "$Issue", '--repo', $Ctx.Repo, '--json', 'state,labels')
    if ($r.ExitCode -ne 0) { return $null }
    try { $o = ($r.Output -join "`n") | ConvertFrom-Json } catch { return $null }
    if ($null -eq $o) { return $null }
    [pscustomobject]@{ State = "$($o.state)".ToUpperInvariant(); Labels = @(Get-CmAgentLabelNames $o) }
}

function Get-CmAgentMergedPr {
    <#
      The pull request from the run's branch that merged after the run started, or $null. An older merge of the same
      branch name (an issue that was reopened) does not count. Also $null when GitHub cannot be asked: then nothing is
      cleaned up.
    #>
    param($Ctx, [string]$Branch, [long]$SinceUnix = 0)
    $r = Invoke-Cm -Command 'gh' -Arguments @('pr', 'list', '--repo', $Ctx.Repo, '--head', $Branch, '--state', 'all', '--json', 'number,state,mergedAt,url')
    if ($r.ExitCode -ne 0) { return $null }
    try { $prs = ($r.Output -join "`n") | ConvertFrom-Json } catch { return $null }
    @($prs | Where-Object { $_ -and $_.mergedAt -and ([DateTimeOffset]::new((ConvertTo-CmUtc $_.mergedAt)).ToUnixTimeSeconds() -ge $SinceUnix - 60) }) | Select-Object -First 1
}

# ---- paths of one run -------------------------------------------------------------------------------------------------------

function Get-CmAgentRunPaths {
    param($Ctx, [string]$RepoDir, [int]$Issue, [DateTimeOffset]$Now)
    $stamp = $Now.ToString('yyyyMMdd-HHmmss')
    [pscustomobject]@{
        Branch   = "agent/issue-$Issue"
        Worktree = [IO.Path]::Combine($RepoDir, '.claude', 'worktrees', "agent-issue-$Issue")
        Log      = Join-Path $Ctx.RunLogDir "issue-$Issue-$stamp.log"
        Prompt   = Join-Path $Ctx.RunLogDir "issue-$Issue-$stamp.prompt.md"
        Exit     = Join-Path $Ctx.RunLogDir "issue-$Issue-$stamp.exit.json"
    }
}

function Expand-CmAgentPrompt {
    param([string]$TemplatePath, [int]$Issue, [string]$Branch)
    (Get-Content -LiteralPath $TemplatePath -Raw).Replace('{issue}', "$Issue").Replace('{branch}', $Branch)
}

function Get-CmAgentRunnerArguments {
    <#
      The command line of conhost.exe: a headless console running the runner, so no window ever appears. The token file is
      named by its path; the runner reads it, so the token itself is on no command line.
    #>
    param([string]$Pwsh, [string]$RunnerScript, [int]$Issue, [string]$Model, [string]$Claude, $Paths, [int]$MaxMinutes, [string]$TokenFile = '')
    $token = if ($TokenFile) { " -TokenFile `"$TokenFile`"" } else { '' }
    "--headless `"$Pwsh`" -NoProfile -ExecutionPolicy Bypass -File `"$RunnerScript`" -Issue $Issue -Model $Model -Claude `"$Claude`" " +
    "-PromptFile `"$($Paths.Prompt)`" -LogFile `"$($Paths.Log)`" -ExitFile `"$($Paths.Exit)`" -WorkDir `"$($Paths.Worktree)`"$token -MaxMinutes $MaxMinutes"
}

function Get-CmAgentPwsh {
    [IO.Path]::Combine($PSHOME, $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' }))
}

function Get-CmAgentTaskAction {
    <# The /TR text of the scheduled task. schtasks allows 261 characters. #>
    param([string]$Pwsh, [string]$Script, [string]$Root)
    "conhost.exe --headless `"$Pwsh`" -NoProfile -ExecutionPolicy Bypass -File `"$Script`" -Root `"$Root`" -Apply"
}

# ---- the lock and the day's count ---------------------------------------------------------------------------------------------

function Read-CmAgentLock {
    param($Ctx)
    if (-not (Test-Path -LiteralPath $Ctx.LockFile)) { return $null }
    $lock = Read-CmState -Path $Ctx.LockFile
    if (-not ($lock.ContainsKey('issue') -and $lock.ContainsKey('pid') -and $lock.ContainsKey('startedUnix'))) {
        Write-CmLog "the agent lock at $($Ctx.LockFile) cannot be read, so it is cleared (no issue can be marked blocked for it)" 'WARN'
        Remove-CmAgentLock -Ctx $Ctx
        return $null
    }
    $lock
}

function Remove-CmAgentLock {
    param($Ctx)
    if ($global:CmApply -and (Test-Path -LiteralPath $Ctx.LockFile)) { Remove-Item -LiteralPath $Ctx.LockFile -Force -ErrorAction SilentlyContinue }
}

function Get-CmAgentRunStatus {
    <# 'running' (alive, inside its time limit), 'timeout' (alive, past it) or 'exited' (its process is gone). #>
    param($Lock, [int]$MaxMinutes)
    $now = Get-CmAgentNow
    $started = [long]$Lock.startedUnix
    $info = Get-CmProcessInfo -Id ([int]$Lock.pid)
    $alive = $false
    if ($info) {
        $alive = $true
        if ($info.StartTime) {
            # An id that now belongs to a process started well after the run did is somebody else's (ids are reused).
            $utc = ConvertTo-CmUtc $info.StartTime
            $processStarted = [DateTimeOffset]::new($utc).ToUnixTimeSeconds()
            if ($processStarted -gt $started + 120) { $alive = $false }
        }
    }
    if (-not $alive) { return 'exited' }
    if (($now.ToUnixTimeSeconds() - $started) / 60.0 -gt $MaxMinutes) { return 'timeout' }
    'running'
}

# ---- starting a run -----------------------------------------------------------------------------------------------------------

function Test-CmAgentSetup {
    <# A problem with the machine that would make a run pointless, as text, or $null. Checked before an issue is claimed. #>
    param($Ctx, $Settings)
    $repo = $Settings.Repo
    if (Test-CmPathUnder -Path $repo -Root $Ctx.DeployerClone) {
        return "CM_AGENT_REPO ($repo) is the deployer's own clone or inside it; set CM_AGENT_REPO in .env to your own checkout"
    }
    if (-not (Test-Path -LiteralPath $repo -PathType Container)) { return "the checkout '$repo' does not exist; set CM_AGENT_REPO in .env" }
    if (-not (Test-Path -LiteralPath (Join-Path $repo '.git'))) { return "'$repo' is not a git checkout (it has no .git); set CM_AGENT_REPO in .env" }
    if (-not (Test-Path -LiteralPath $Ctx.PromptFile)) { return "the prompt template $($Ctx.PromptFile) is missing" }
    if (-not (Resolve-CmAgentClaude)) { return 'claude was not found on PATH or in the .local\bin folder of this account; install Claude Code for it' }
    $null
}

function New-CmAgentWorktree {
    <# Makes (or reuses) the run's worktree from the origin/main the caller just fetched. Returns @{ Ok; Reused; Error }. A leftover from an earlier run is reused untouched. #>
    param($Ctx, [string]$RepoDir, $Paths)
    $fail = { param($text) [pscustomobject]@{ Ok = $false; Reused = $false; Error = $text } }
    if (Test-Path -LiteralPath $Paths.Worktree) {
        if (-not (Test-Path -LiteralPath (Join-Path $Paths.Worktree '.git'))) {
            return & $fail "$($Paths.Worktree) exists but is not a git worktree; remove that folder and queue the issue again"
        }
        Write-CmLog "reusing the worktree left by an earlier run at $($Paths.Worktree)"
        return [pscustomobject]@{ Ok = $true; Reused = $true; Error = '' }
    }
    [void](Invoke-Cm -Command 'git' -Arguments @('-C', $RepoDir, 'worktree', 'prune') -Mutating)   # forget registrations whose folder is gone
    $branch = Invoke-Cm -Command 'git' -Arguments @('-C', $RepoDir, 'rev-parse', '--verify', '--quiet', "refs/heads/$($Paths.Branch)")
    $add = if ($branch.ExitCode -eq 0) { @('-C', $RepoDir, 'worktree', 'add', $Paths.Worktree, $Paths.Branch) }
    else { @('-C', $RepoDir, 'worktree', 'add', $Paths.Worktree, '-b', $Paths.Branch, 'origin/main') }
    $r = Invoke-Cm -Command 'git' -Arguments $add -Mutating
    if ($r.ExitCode -ne 0) { return & $fail "git worktree add failed (exit $($r.ExitCode)): $(@($r.Output | Select-Object -Last 3) -join ' ')" }
    [pscustomobject]@{ Ok = $true; Reused = $false; Error = '' }
}

function Set-CmAgentBlocked {
    <# Marks an issue blocked and says why. Used by every path that gives up on a run. #>
    param($Ctx, [int]$Issue, [string[]]$Remove, [string]$Body)
    [void](Set-CmIssueLabels -Ctx $Ctx -Issue $Issue -Add @('agent-blocked') -Remove $Remove)
    [void](Add-CmIssueComment -Ctx $Ctx -Issue $Issue -Body $Body)
}

function Start-CmAgentRun {
    param($Ctx, $Settings, $Pick, [hashtable]$State)
    $issue = $Pick.Number
    $problem = Test-CmAgentSetup -Ctx $Ctx -Settings $Settings
    if ($problem) {
        Write-CmLog "not starting the agent for issue #${issue}: $problem" 'ERROR'
        return New-CmAgentResult 'config-error' 2 $problem $issue
    }
    $repoDir = $Settings.Repo
    $claude = Resolve-CmAgentClaude
    $now = Get-CmAgentNow
    $paths = Get-CmAgentRunPaths -Ctx $Ctx -RepoDir $repoDir -Issue $issue -Now $now

    # Fetch before claiming: a network blip then costs nothing on the issue and the next pass simply tries again.
    $fetch = Invoke-Cm -Command 'git' -Arguments @('-C', $repoDir, 'fetch', 'origin') -Mutating
    if ($fetch.ExitCode -ne 0) {
        Write-CmLog "git fetch origin failed (exit $($fetch.ExitCode)) in $repoDir; issue #$issue is left alone and the next pass tries again" 'WARN'
        return New-CmAgentResult 'fetch-failed' 3 'git fetch origin failed' $issue
    }
    # Claim: from here on no other pass picks this issue.
    if (-not (Set-CmIssueLabels -Ctx $Ctx -Issue $issue -Add @('agent-working'))) {
        Write-CmLog "could not add agent-working to issue #$issue; not starting" 'WARN'
        return New-CmAgentResult 'claim-failed' 3 'gh issue edit failed' $issue
    }
    [void](Add-CmIssueComment -Ctx $Ctx -Issue $issue -Body ("Picked up at $($now.ToString('yyyy-MM-dd HH:mm zzz')) by the background agent (model: $($Pick.Model)). " +
            "It works in the branch ``$($paths.Branch)`` and will comment here when it opens a pull request or gets stuck."))
    # Counted now, so a crash after this point does not hand out a free run.
    $today = [int]$now.ToString('yyyyMMdd')
    $State.runs = $(if ([int]$State.day -eq $today) { [int]$State.runs } else { 0 }) + 1
    $State.day = $today
    Write-CmState -Path $Ctx.StateFile -State $State

    $fail = {
        param($reason)
        Write-CmLog "could not start the agent for issue #${issue}: $reason" 'ERROR'
        Set-CmAgentBlocked -Ctx $Ctx -Issue $issue -Remove @('agent-working') -Body ("The background agent could not start: $reason`n`nNothing was run. Remove ``agent-blocked`` to queue it again.")
        New-CmAgentResult 'start-failed' 1 $reason $issue
    }
    $worktree = New-CmAgentWorktree -Ctx $Ctx -RepoDir $repoDir -Paths $paths
    if (-not $worktree.Ok) { return & $fail "the worktree could not be made: $($worktree.Error)" }

    $prompt = Expand-CmAgentPrompt -TemplatePath $Ctx.PromptFile -Issue $issue -Branch $paths.Branch
    if ($global:CmApply) {
        New-Item -ItemType Directory -Force -Path $Ctx.RunLogDir | Out-Null
        [IO.File]::WriteAllText($paths.Prompt, $prompt, [Text.UTF8Encoding]::new($false))
    }
    $tokenFile = if (Read-CmAgentToken -Path $Settings.TokenFile) { $Settings.TokenFile } else { '' }
    $minutes = Get-CmAgentRunMinutes -Settings $Settings -Model $Pick.Model
    $runner = Get-CmAgentRunnerArguments -Pwsh (Get-CmAgentPwsh) -RunnerScript $Ctx.RunnerScript -Issue $issue -Model $Pick.Model -Claude $claude -Paths $paths -MaxMinutes $minutes -TokenFile $tokenFile
    try { $process = Start-CmProcess -File 'conhost.exe' -Arguments $runner }
    catch { return & $fail "the run could not be launched ($($_.Exception.Message))" }
    if ($process.DryRun) { return New-CmAgentResult 'dry-run' 0 '' $issue }

    Write-CmState -Path $Ctx.LockFile -State @{
        issue = $issue; pid = [int]$process.Id; startedUnix = $now.ToUnixTimeSeconds(); model = $Pick.Model
        repoDir = $repoDir; worktree = $paths.Worktree; branch = $paths.Branch; log = $paths.Log; exitFile = $paths.Exit; attempts = 0
        usedToken = [bool]$tokenFile   # a run that fails at once with the saved token is the token's alert, not the sign-in's
        maxMinutes = $minutes          # the limit this run started with; the watcher judges it by this, not by today's setting
    }
    Write-CmLog "started the agent for issue #$issue (model $($Pick.Model)); process $($process.Id), log $($paths.Log)"
    New-CmAgentResult 'started' 0 '' $issue
}

# ---- closing out a run --------------------------------------------------------------------------------------------------------

function Format-CmAgentLogLines {
    <# Log lines as the inside of a fenced block: long lines cut, no fence of its own in them, a size cap. #>
    param([string[]]$Lines = @())
    if (@($Lines).Count -eq 0) { return '(the log is empty)' }
    $cut = @($Lines | ForEach-Object { if ($_.Length -gt 400) { $_.Substring(0, 400) + ' ...' } else { $_ } })
    $text = ($cut -join "`n") -replace '```', "'''"
    if ($text.Length -gt 8000) { $text = '...' + $text.Substring($text.Length - 8000) }
    $text
}

function Get-CmAgentLogTail {
    param([string]$Path, [int]$Lines = $script:CmAgentLogTailLines)
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return '(the log file is missing)' }
    Format-CmAgentLogLines -Lines @(Get-Content -LiteralPath $Path -Tail $Lines)
}

function Get-CmAgentLogHead {
    param([string]$Path, [int]$Lines = $script:CmAgentLogHeadLines)
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return '(the log file is missing)' }
    Format-CmAgentLogLines -Lines @(Get-Content -LiteralPath $Path -TotalCount $Lines)
}

function Reset-CmAgentBackoff {
    <# A run that got past the quick-failure window shows the sign-in and limits work, so the streak and any pause end. Writes nothing when there is nothing to reset. #>
    param($Ctx)
    $state = Read-CmState -Path $Ctx.StateFile
    if ([int]$state.quickFailures -eq 0 -and -not $state.ContainsKey('pauseUntilUnix')) { return }
    $state.quickFailures = 0
    [void]$state.Remove('pauseUntilUnix')
    Write-CmState -Path $Ctx.StateFile -State $state
}

function Confirm-CmAgentRunStarted {
    <# A run got past the start-up window: the sign-in and limits work, so the streak and pause end and a token alert, if one is open, closes. #>
    param($Ctx, $Settings)
    Reset-CmAgentBackoff -Ctx $Ctx
    if (Test-CmAgentAlertOpen -Ctx $Ctx -Kind $script:CmAgentTokenFailedKind) {
        Clear-CmAgentAlert -Ctx $Ctx -Settings $Settings -Kind $script:CmAgentTokenFailedKind -Why 'a run got past start-up again'
    }
}

function Test-CmAgentAuthFailure {
    <# True when the first lines a run printed (not the runner's own === lines) look like a failed sign-in. #>
    param([string]$LogPath)
    if (-not $LogPath -or -not (Test-Path -LiteralPath $LogPath)) { return $false }
    $head = @(Get-Content -LiteralPath $LogPath -TotalCount 40 | Where-Object { $_ -notmatch '^===' }) -join "`n"
    $head -match '(?i)authenticat|oauth|unauthori[sz]ed|\b401\b|(expired|revoked|invalid).{0,40}(token|credential|session)|not (signed|logged) in|/login'
}

function Register-CmAgentQuickFailure {
    <#
      A run that exited non-zero within CmAgentQuickFailureSeconds is almost certainly the machine, not the issue
      (sign-in expired, usage limit, a flag refused). So the issue goes back in the queue instead of being blocked, the
      service pauses (60 minutes, doubling per failure in a row, at most 8 hours), and the attempt is not counted against
      the day's cap; otherwise one broken sign-in would block every queued issue in turn.
      When the run had the saved token, the failure may be the token (expired or revoked): the owner gets one alert if the
      log says the sign-in failed, or if it is the second failure in a row.
    #>
    param($Ctx, $Settings, $Lock, [int]$ExitCode, [string[]]$QueueLabels)
    $issue = [int]$Lock.issue
    $now = Get-CmAgentNow
    $state = Read-CmState -Path $Ctx.StateFile
    $failures = [int]$state.quickFailures + 1
    $minutes = [math]::Min($script:CmAgentPauseMaxMinutes, $script:CmAgentPauseMinutes * [math]::Pow(2, [math]::Min($failures - 1, 10)))
    $until = $now.AddMinutes($minutes)
    $startedDay = [int][DateTimeOffset]::FromUnixTimeSeconds([long]$Lock.startedUnix).ToOffset($now.Offset).ToString('yyyyMMdd')
    if ([int]$state.day -eq $startedDay -and [int]$state.runs -gt 0) { $state.runs = [int]$state.runs - 1 }
    $state.quickFailures = $failures
    $state.pauseUntilUnix = $until.ToUnixTimeSeconds()
    Write-CmState -Path $Ctx.StateFile -State $state
    [void](Set-CmIssueLabels -Ctx $Ctx -Issue $issue -Remove $QueueLabels)
    [void](Add-CmIssueComment -Ctx $Ctx -Issue $issue -Body (@(
                "The agent couldn't start (exit $ExitCode; first log lines below, secrets hidden). It will try again after $($until.ToString('yyyy-MM-dd HH:mm zzz')).",
                '',
                '```text',
                (Get-CmAgentLogHead -Path "$($Lock.log)"),
                '```'
            ) -join "`n"))
    Write-CmLog "the agent run for issue #$issue failed within $($script:CmAgentQuickFailureSeconds / 60) minutes (exit code $ExitCode); the issue is back in the queue and new runs are paused until $($until.ToString('yyyy-MM-dd HH:mm zzz')) ($failures quick failure(s) in a row)" 'WARN'
    if ($Lock.usedToken -eq $true -and ($failures -ge 2 -or (Test-CmAgentAuthFailure -LogPath "$($Lock.log)"))) {
        Set-CmAgentAlert -Ctx $Ctx -Settings $Settings -Kind $script:CmAgentTokenFailedKind -Def (Get-CmAgentTokenAlertKind) `
            -Reason "A run for issue #$issue that used the saved token exited with code $ExitCode within $($script:CmAgentQuickFailureSeconds / 60) minutes of starting ($failures failed start(s) in a row)."
    }
}

function Remove-CmAgentWorktree {
    <# Removes a finished run's worktree and branch. Only the exact folder and branch this service names for the issue; anything else is refused. #>
    param([string]$RepoDir, [string]$Worktree, [string]$Branch, [int]$Issue)
    $expected = [IO.Path]::Combine($RepoDir, '.claude', 'worktrees', "agent-issue-$Issue")
    if ((ConvertTo-CmPathKey $Worktree) -ne (ConvertTo-CmPathKey $expected) -or $Branch -ne "agent/issue-$Issue") {
        Write-CmLog "refusing to remove '$Worktree' (branch '$Branch'): it is not the worktree this service makes for issue #$Issue" 'WARN'
        return
    }
    if (Test-Path -LiteralPath $expected) {
        $r = Invoke-Cm -Command 'git' -Arguments @('-C', $RepoDir, 'worktree', 'remove', '--force', $expected) -Mutating
        if ($r.ExitCode -ne 0) { Write-CmLog "could not remove the worktree $expected (exit $($r.ExitCode)); left in place" 'WARN' }
    }
    $b = Invoke-Cm -Command 'git' -Arguments @('-C', $RepoDir, 'branch', '-D', $Branch) -Mutating
    if ($b.ExitCode -ne 0) { Write-CmLog "could not delete the local branch $Branch (exit $($b.ExitCode)); left in place" 'WARN' }
}

function Get-CmAgentBlockedBody {
    param($Lock, $Exit, [string]$Status, [bool]$Merged, [int]$MaxMinutes, [bool]$WorktreeRemoved)
    $now = Get-CmAgentNow
    $started = [DateTimeOffset]::FromUnixTimeSeconds([long]$Lock.startedUnix).ToOffset($now.Offset)
    $ranMinutes = [math]::Max(0, [math]::Round(($now.ToUnixTimeSeconds() - [long]$Lock.startedUnix) / 60.0))
    $timedOut = ($Status -eq 'timeout') -or ($Exit.timedOut -eq $true)
    $code = if ($null -ne $Exit.exitCode) { [int]$Exit.exitCode } else { $null }
    if ($Merged) { $why = 'its pull request merged, but the issue is still open (the PR may be missing `Closes #N`), so it would be picked up again' }
    elseif ($timedOut) { $why = "it ran longer than $MaxMinutes minutes and was stopped" }
    elseif ($null -ne $code -and $code -ne 0) { $why = "the run ended with exit code $code" }
    elseif ($null -ne $code) { $why = 'the run ended cleanly (exit code 0) but did not merge a pull request or report that it was blocked' }
    else { $why = 'the run ended without finishing and no exit code was recorded (the process was killed, or the PC restarted)' }
    $exitText = if ($null -ne $code) { "exit code $code" } elseif ($timedOut) { 'stopped by the time limit' } else { 'no exit code recorded' }
    $worktreeText = if ($WorktreeRemoved) { 'removed (its pull request merged)' } else { "kept at ``$($Lock.worktree)`` with whatever the run left in it; the next run reuses it" }
    @(
        "The background agent stopped without finishing this issue: $why.",
        '',
        "- Started: $($started.ToString('yyyy-MM-dd HH:mm zzz')), ran about $ranMinutes minutes ($exitText)",
        "- Branch: ``$($Lock.branch)``",
        "- Worktree: $worktreeText",
        "- Full log: ``$($Lock.log)``",
        '',
        "Last $script:CmAgentLogTailLines lines of the log (secrets hidden):",
        '',
        '```text',
        (Get-CmAgentLogTail -Path "$($Lock.log)"),
        '```',
        '',
        'To run it again, remove `agent-blocked`; `agent-task` is still on the issue.'
    ) -join "`n"
}

function Complete-CmAgentRun {
    <#
      Closes out a run whose process is gone (or was just killed for its time limit). Returns $true when the lock was
      cleared, $false when GitHub could not be asked and the next pass should try again.
        issue closed         the run finished: queue labels come off; the worktree goes if its PR merged
        open, agent-blocked  the agent reported itself: nothing more to say
        open, non-zero exit  within 5 minutes of the start, nothing merged: back in the queue, the service pauses
                             (Register-CmAgentQuickFailure)
        open, anything else  it did not finish: blocked, with the exit code and the log tail
      Every outcome except the quick failure ends the streak of quick failures.
    #>
    param($Ctx, $Settings, $Lock, [string]$Status)
    $issue = [int]$Lock.issue
    $branch = "$($Lock.branch)"
    $repoDir = "$($Lock.repoDir)"
    $view = Get-CmAgentIssueState -Ctx $Ctx -Issue $issue
    if (-not $view) {
        $attempts = [int]$Lock.attempts + 1
        if ($attempts -ge $script:CmAgentFinishAttempts) {
            Write-CmLog "gave up closing out the run for issue #$issue after $attempts tries because GitHub could not be asked; the lock is dropped, so check the issue by hand" 'ERROR'
            Remove-CmAgentLock -Ctx $Ctx
            return $true
        }
        $Lock.attempts = $attempts
        Write-CmState -Path $Ctx.LockFile -State $Lock
        Write-CmLog "could not read issue #$issue from GitHub to close out its run (try $attempts of $script:CmAgentFinishAttempts); will try again next pass" 'WARN'
        return $false
    }
    $exit = Read-CmState -Path "$($Lock.exitFile)"
    $merged = $null -ne (Get-CmAgentMergedPr -Ctx $Ctx -Branch $branch -SinceUnix ([long]$Lock.startedUnix))
    $queueLabels = @($view.Labels | Where-Object { $_ -in @('agent-working', 'agent-review') })
    # Only a run that ended by itself with a non-zero code and a recorded end time can be judged quick; a run that was
    # stopped for its time limit, or whose end nobody saw, is not.
    $exitCode = if ($null -ne $exit.exitCode -and $exit.timedOut -ne $true) { [int]$exit.exitCode } else { $null }
    $ranSeconds = if ($null -ne $exit.endedUnix) { [long]$exit.endedUnix - [long]$Lock.startedUnix } else { $null }
    $quickFailure = ($null -ne $exitCode) -and ($exitCode -ne 0) -and ($null -ne $ranSeconds) -and ($ranSeconds -lt $script:CmAgentQuickFailureSeconds)

    if ($view.State -eq 'CLOSED') {
        [void](Set-CmIssueLabels -Ctx $Ctx -Issue $issue -Remove $queueLabels)
        if ($merged) { Remove-CmAgentWorktree -RepoDir $repoDir -Worktree "$($Lock.worktree)" -Branch $branch -Issue $issue }
        Write-CmLog "issue #$issue is closed; its agent run is finished$(if ($merged) { ' and its pull request merged' })"
        Confirm-CmAgentRunStarted -Ctx $Ctx -Settings $Settings
    }
    elseif ($view.Labels -contains 'agent-blocked' -and $view.Labels -notcontains 'agent-working') {
        if ($merged) { Remove-CmAgentWorktree -RepoDir $repoDir -Worktree "$($Lock.worktree)" -Branch $branch -Issue $issue }
        Write-CmLog "the agent blocked issue #$issue itself; its worktree is kept"
        Confirm-CmAgentRunStarted -Ctx $Ctx -Settings $Settings
    }
    elseif ($quickFailure -and -not $merged) {
        Register-CmAgentQuickFailure -Ctx $Ctx -Settings $Settings -Lock $Lock -ExitCode $exitCode -QueueLabels $queueLabels
    }
    else {
        if ($merged) { Remove-CmAgentWorktree -RepoDir $repoDir -Worktree "$($Lock.worktree)" -Branch $branch -Issue $issue }
        $body = Get-CmAgentBlockedBody -Lock $Lock -Exit $exit -Status $Status -Merged $merged -MaxMinutes (Get-CmAgentLockMinutes -Lock $Lock -Settings $Settings) -WorktreeRemoved $merged
        Set-CmAgentBlocked -Ctx $Ctx -Issue $issue -Remove $queueLabels -Body $body
        Write-CmLog "the agent run for issue #$issue did not finish ($Status); the issue is marked agent-blocked" 'WARN'
        Confirm-CmAgentRunStarted -Ctx $Ctx -Settings $Settings
    }
    Remove-CmAgentLock -Ctx $Ctx
    $true
}

# ---- one watcher pass -----------------------------------------------------------------------------------------------------------

function Invoke-CmAgentWatch {
    <#
      One pass of the scheduled watcher. Returns @{ Outcome; ExitCode; Reason; Issue }.
        disabled / busy / running / paused / cap / idle   exit 0, and (except `cap`, once a day) nothing is written
        started                                  an agent was started for Issue
        dry-run                                  without -Apply: what would have started
        finish-pending                           an earlier run could not be closed out yet; nothing new starts
        signed-out 2                             Claude Code is signed out and no usable token file is saved: nothing was picked up
        config-error 2, gh-failed 3, fetch-failed 3, start-failed 1, claim-failed 3, crashed 1
    #>
    param($Ctx)
    $envMap = Read-CmEnvFile -Path $Ctx.EnvFile
    $settings = Get-CmAgentSettings -EnvMap $envMap -Root $Ctx.Root
    if (-not $settings.Enabled) { return New-CmAgentResult 'disabled' 0 }
    # The token is a secret like any other: whatever the watcher posts or logs has it hidden, whatever shape it has.
    Initialize-Cm -Apply $global:CmApply -LogFile $Ctx.LogFile -Secrets (@(Get-CmAgentSecrets $envMap) + @(Read-CmAgentToken -Path $settings.TokenFile))
    $held = $null
    if ($global:CmApply) {
        # Two passes at once would both pick the same issue; the second leaves at once.
        $held = Enter-CmLock -Path $Ctx.WatcherLockFile
        if (-not $held) { return New-CmAgentResult 'busy' 0 }
    }
    try { return Invoke-CmAgentWatchCore -Ctx $Ctx -Settings $settings }
    catch {
        $message = "the agent watcher crashed: $($_.Exception.Message)"
        Write-CmLog $message 'ERROR'
        return New-CmAgentResult 'crashed' 1 $message
    }
    finally { Exit-CmLock $held }
}

function Invoke-CmAgentWatchCore {
    param($Ctx, $Settings)
    $lock = Read-CmAgentLock -Ctx $Ctx
    if ($lock) {
        $limit = Get-CmAgentLockMinutes -Lock $lock -Settings $Settings
        $status = Get-CmAgentRunStatus -Lock $lock -MaxMinutes $limit
        if ($status -eq 'running') { return New-CmAgentResult 'running' 0 }
        if ($status -eq 'timeout') {
            Write-CmLog "the agent run for issue #$($lock.issue) is past its $limit minute limit; stopping process $($lock.pid) and everything it started" 'WARN'
            [void](Invoke-Cm -Command 'taskkill' -Arguments @('/PID', "$($lock.pid)", '/T', '/F') -Mutating)
        }
        $closed = @(Complete-CmAgentRun -Ctx $Ctx -Settings $Settings -Lock $lock -Status $status) | Select-Object -Last 1
        if (-not $closed) { return New-CmAgentResult 'finish-pending' 0 }
    }

    # A token file that is new, or was replaced, ends the pause that failed starts left behind (before the pause is looked at).
    Update-CmAgentTokenSeen -Ctx $Ctx -Settings $Settings
    # While the signed-out alert is open the sign-in is looked at on every pass (a local call, no model), so the alert closes
    # as soon as the token is saved even with nothing queued. Otherwise it is looked at only once an issue is about to be claimed.
    $signInChecked = $false
    if (Test-CmAgentAlertOpen -Ctx $Ctx -Kind $script:CmAgentSignedOutKind) {
        $signInChecked = $true
        if (-not (Confirm-CmAgentSignIn -Ctx $Ctx -Settings $Settings)) { return New-CmAgentResult 'signed-out' 2 'Claude Code is signed out and no usable token file is saved' }
    }

    $state = Read-CmState -Path $Ctx.StateFile   # after the steps above: they may have written this file
    # After runs that failed at once the service is paused (Register-CmAgentQuickFailure): no call until the time is up.
    if ([long]$state.pauseUntilUnix -gt (Get-CmAgentNow).ToUnixTimeSeconds()) { return New-CmAgentResult 'paused' 0 }
    $today = [int](Get-CmAgentNow).ToString('yyyyMMdd')
    $runsToday = if ([int]$state.day -eq $today) { [int]$state.runs } else { 0 }
    if ($runsToday -ge $Settings.MaxRunsPerDay) {
        if ([int]$state.capNotedDay -ne $today) {
            Write-CmLog "the daily cap of $($Settings.MaxRunsPerDay) runs is reached; no more agents start today (CM_AGENT_MAX_RUNS_PER_DAY in .env)"
            $state.day = $today
            $state.runs = $runsToday
            $state.capNotedDay = $today
            Write-CmState -Path $Ctx.StateFile -State $state
        }
        return New-CmAgentResult 'cap' 0
    }

    $list = Invoke-Cm -Command 'gh' -Arguments @('issue', 'list', '--repo', $Ctx.Repo, '--state', 'open', '--label', 'agent-task', '--limit', '100', '--json', 'number,title,labels,author,createdAt')
    if ($list.ExitCode -ne 0) {
        Write-CmLog "gh issue list failed (exit $($list.ExitCode)): $(@($list.Output | Select-Object -First 2) -join ' ')" 'WARN'
        return New-CmAgentResult 'gh-failed' 3 'gh issue list failed'
    }
    try { $rows = @(($list.Output -join "`n") | ConvertFrom-Json | Where-Object { $null -ne $_ }) }
    catch {
        Write-CmLog 'gh issue list printed something that is not JSON' 'WARN'
        return New-CmAgentResult 'gh-failed' 3 'gh issue list printed something that is not JSON'
    }
    $pick = Select-CmAgentIssue -Issues $rows -Owner $Ctx.Owner
    if (-not $pick) { return New-CmAgentResult 'idle' 0 }
    if (-not $signInChecked) {
        if (-not (Confirm-CmAgentSignIn -Ctx $Ctx -Settings $Settings)) { return New-CmAgentResult 'signed-out' 2 'Claude Code is signed out and no usable token file is saved' $pick.Number }
        $state = Read-CmState -Path $Ctx.StateFile   # the check may have written its hourly log note
    }
    Write-CmLog "picked issue #$($pick.Number) '$($pick.Title)' (p$($pick.Priority), model $($pick.Model))"
    Start-CmAgentRun -Ctx $Ctx -Settings $Settings -Pick $pick -State $state
}

# ---- the runner (what the watcher starts) ----------------------------------------------------------------------------------------

function Get-CmAgentShellLine {
    <# The one line cmd.exe runs: claude, the prompt file on its standard input (a long prompt does not survive a command line), output appended to the log. #>
    param([string]$Claude, [string]$Model, [string]$PromptFile, [string]$LogFile)
    "`"$Claude`" -p --permission-mode auto --model $Model --disallowedTools `"$script:CmAgentBackgroundTools`" < `"$PromptFile`" >> `"$LogFile`" 2>&1"
}

function Invoke-CmAgentRunner {
    <#
      Runs claude for one issue to the end and records how it ended in the exit file, which the next watcher pass reads.
      Enforces the time limit itself: while an agent runs, Task Scheduler may not start the watcher again.
      With a -TokenFile that holds a token, the claude child gets it as CLAUDE_CODE_OAUTH_TOKEN, in its environment and
      nowhere else (not on a command line, not in the log, not in the exit file); without one claude uses the CLI's own sign-in.
    #>
    param([string]$Claude, [string]$Model, [string]$PromptFile, [string]$LogFile, [string]$ExitFile, [string]$WorkDir, [int]$MaxMinutes, [int]$Issue, [string]$TokenFile = '', [string]$WatcherTask = $script:CmAgentTaskName)
    $token = Read-CmAgentToken -Path $TokenFile
    Initialize-Cm -Apply $true -Secrets @($token)
    $dir = Split-Path -Parent $LogFile
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $began = Get-CmAgentNow
    Add-Content -LiteralPath $LogFile -Encoding utf8 -Value ('=== {0} agent for issue #{1} starting (model {2}, limit {3} minutes) ===' -f $began.ToString('yyyy-MM-dd HH:mm:ss'), $Issue, $Model, $MaxMinutes)
    Add-Content -LiteralPath $LogFile -Encoding utf8 -Value $(if ($token) { '=== sign-in: the long-lived token from the token file ===' } else { '=== sign-in: the CLI''s own ===' })
    $line = Get-CmAgentShellLine -Claude $Claude -Model $Model -PromptFile $PromptFile -LogFile $LogFile
    $shell = if ($env:ComSpec) { $env:ComSpec } else { 'cmd.exe' }
    $childEnv = @{ CLAUDE_CODE_DISABLE_BACKGROUND_TASKS = '1' }
    if ($token) { $childEnv.CLAUDE_CODE_OAUTH_TOKEN = $token }
    $process = Start-CmProcess -File $shell -Arguments "/d /s /c `"$line`"" -WorkingDirectory $WorkDir -NoNewWindow -Env $childEnv
    $limitMs = [int][math]::Min([double][int]::MaxValue, [double]$MaxMinutes * 60000)
    $finished = $process.WaitForExit($limitMs)
    $code = $null
    if ($finished) {
        $code = [int]$process.ExitCode
        Add-Content -LiteralPath $LogFile -Encoding utf8 -Value "=== ended with exit code $code ==="
    }
    else {
        [void](Invoke-Cm -Command 'taskkill' -Arguments @('/PID', "$($process.Id)", '/T', '/F') -Mutating)
        Add-Content -LiteralPath $LogFile -Encoding utf8 -Value "=== stopped after the $MaxMinutes minute limit ==="
    }
    Write-CmState -Path $ExitFile -State @{ exitCode = $code; timedOut = (-not $finished); endedUnix = (Get-CmAgentNow).ToUnixTimeSeconds() }
    Start-CmAgentWatcher -TaskName $WatcherTask -LogFile $LogFile
    if ($null -ne $code) { $code } else { 124 }
}

function Start-CmAgentWatcher {
    <#
      Wakes the watcher task as the last thing a run does, so the next queued issue does not wait for the 5-minute poll. This
      is an ordinary pass and nothing more: the watcher's own lock turns away a second pass that overlaps it, the exit file is
      already written for it to read, and the daily cap, the pause after a run that failed at once and the kill switch are all
      decided inside the pass. A run that is somehow still alive makes the pass exit at once, and then the poll picks up as before.
      Only a task named like the installer's (CollectionMaxxingAgent, optionally with a suffix) is ever started; an empty name
      switches this off. Whatever happens here is noted in the run's log and never changes how the run ended.
    #>
    param([string]$TaskName, [string]$LogFile)
    if (-not $TaskName) { return }
    $note = {
        param($text)
        try { Add-Content -LiteralPath $LogFile -Encoding utf8 -Value "=== $text ===" } catch { }
    }
    if ($TaskName -notmatch '^CollectionMaxxingAgent') { & $note "not starting the watcher task ${TaskName}: only a task named CollectionMaxxingAgent is ever started"; return }
    try {
        $r = Invoke-Cm -Command 'schtasks' -Arguments @('/Run', '/TN', $TaskName) -Mutating -TimeoutSeconds 30
        if ($r.ExitCode -eq 0) { & $note "started the watcher task $TaskName" }
        else { & $note "could not start the watcher task $TaskName (exit $($r.ExitCode)); the next 5-minute pass picks up the next issue" }
    }
    catch { & $note "could not start the watcher task $TaskName ($($_.Exception.Message)); the next 5-minute pass picks up the next issue" }
}
