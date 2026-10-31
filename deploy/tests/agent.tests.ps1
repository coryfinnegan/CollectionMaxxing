# Tests for the issue agent (deploy/agent): which issue the watcher picks, the one-run-at-a-time lock, the daily cap and
# the kill switch, how a run is started, how a finished or stuck run is closed out, the runner, and the installer.
# Nothing here calls the real gh, git, claude or Task Scheduler, starts a real process or reads the real process table:
# external commands go through Invoke-Cm ($global:CmRunner), a detached start through Start-CmProcess
# ($global:CmStartProcess) and the process table through Get-CmProcessInfo ($global:CmProcessTable). Dot-sourced by
# Run-Tests.ps1, so the same file must pass on Linux (CI) and Windows.

$script:AgentDir = (Resolve-Path (Join-Path $PSScriptRoot '../agent')).Path
$script:AgentSlug = 'coryfinnegan/CollectionMaxxing'
$script:AgentNow = [DateTimeOffset]::new(2026, 10, 31, 14, 5, 0, [TimeSpan]::FromHours(-4))
$script:AgentPromptFile = Join-Path $script:AgentDir 'agent-prompt.md'

# ---- a scripted fake machine ---------------------------------------------------------------------------------------------

function New-AgentIssue {
    param([int]$Number, [string[]]$Labels = @('agent-task'), [string]$Author = 'coryfinnegan', [string]$Created = '2026-10-30T10:00:00Z')
    @{ number = $Number; title = "Issue $Number"; labels = @($Labels | ForEach-Object { @{ name = $_ } }); author = @{ login = $Author }; createdAt = $Created }
}

function New-AgentMachine {
    <#
      Installs the fakes. Options (all optional):
        Issues          what `gh issue list` returns (New-AgentIssue)
        IssueView       issue number -> @{ state; labels } that `gh issue view` returns (default: open, agent-working)
        Prs             branch -> pull requests `gh pr list --head` returns (default none)
        ListExit, ViewExit, EditExit, CommentExit, FetchExit, WorktreeAddExit    exit codes of those commands
        BranchExists    the local branch agent/issue-N already exists
        StartThrows     the detached start throws (claude or pwsh missing)
        Claude          what the claude lookup finds ('' = nothing)
        TaskExists      `schtasks /Query` finds the task
        Procs           process id -> @{ Id; Name; StartTime } the process table lists
    #>
    param([hashtable]$Opt = @{})
    $m = @{
        Now = $script:AgentNow; Issues = @(); IssueView = @{}; Prs = @{}
        ListExit = 0; ViewExit = 0; EditExit = 0; CommentExit = 0; FetchExit = 0; WorktreeAddExit = 0
        BranchExists = $false; StartThrows = $false; NextProcess = 4242; Procs = @{}; Claude = 'C:\fake\claude.exe'; TaskExists = $false
    }
    foreach ($k in $Opt.Keys) { $m[$k] = $Opt[$k] }
    $global:CmCalls.Clear()
    $global:CmAgentMachine = $m
    $global:CmAgentNow = { $global:CmAgentMachine.Now }
    $global:CmClaudePath = $m.Claude
    $global:CmProcessTable = { param($id) $global:CmAgentMachine.Procs[$id] }
    $global:CmStartProcess = {
        param($File, $Arguments, $WorkingDirectory)
        $m = $global:CmAgentMachine
        $global:CmCalls.Add([pscustomobject]@{ Command = 'start'; Arguments = @($Arguments); Env = @{}; Mutating = $true; Display = "$File $Arguments"; InputText = ''; File = $File; WorkingDirectory = $WorkingDirectory })
        if ($m.StartThrows) { throw 'The system cannot find the file specified' }
        $id = [int]$m.NextProcess
        $m.NextProcess = $id + 1
        $m.Procs[$id] = [pscustomobject]@{ Id = $id; Name = 'conhost'; StartTime = $m.Now.UtcDateTime }
        [pscustomobject]@{ Id = $id }
    }
    $global:CmRunner = {
        param($Command, $Arguments, $Env, $Mutating, $InputText)
        $m = $global:CmAgentMachine
        $display = Format-CmCommand -Command $Command -Arguments $Arguments -Env $Env
        $global:CmCalls.Add([pscustomobject]@{ Command = $Command; Arguments = @($Arguments); Env = $Env; Mutating = $Mutating; Display = $display; InputText = $InputText })
        $ok = { param($lines = @()) [pscustomobject]@{ ExitCode = 0; Output = @($lines) } }
        $exit = { param($code) [pscustomobject]@{ ExitCode = $code; Output = @("fake failure $code") } }
        $line = $Arguments -join ' '
        switch ($Command) {
            'gh' {
                if ($line -match '^issue list') {
                    if ($m.ListExit) { return & $exit $m.ListExit }
                    return & $ok @((ConvertTo-Json -InputObject @($m.Issues) -Depth 6 -Compress))
                }
                if ($line -match '^issue view (\d+)') {
                    if ($m.ViewExit) { return & $exit $m.ViewExit }
                    $view = $m.IssueView[[int]$Matches[1]]
                    if (-not $view) { $view = @{ state = 'OPEN'; labels = @('agent-task', 'agent-working') } }
                    $json = [ordered]@{ state = $view.state; labels = @($view.labels | ForEach-Object { @{ name = $_ } }) }
                    return & $ok @(($json | ConvertTo-Json -Depth 5 -Compress))
                }
                if ($line -match '^pr list .*--head (\S+)') {
                    $prs = $m.Prs[$Matches[1]]
                    return & $ok @((ConvertTo-Json -InputObject @($prs | Where-Object { $_ }) -Depth 5 -Compress))
                }
                if ($line -match '^issue edit') { return & $exit $m.EditExit }
                if ($line -match '^issue comment') { return & $exit $m.CommentExit }
                return & $ok
            }
            'git' {
                $a = @($Arguments)
                if ($a[0] -eq '-C') { $a = @($a | Select-Object -Skip 2) }
                switch ($a[0]) {
                    'fetch' { return & $exit $m.FetchExit }
                    'rev-parse' { return & $exit $(if ($m.BranchExists) { 0 } else { 1 }) }
                    'worktree' {
                        if ($a[1] -eq 'add') {
                            if ($m.WorktreeAddExit) { return & $exit $m.WorktreeAddExit }
                            New-Item -ItemType Directory -Force -Path $a[2] | Out-Null
                            Set-Content -LiteralPath (Join-Path $a[2] '.git') -Value 'gitdir: elsewhere'
                        }
                        if ($a[1] -eq 'remove' -and (Test-Path -LiteralPath $a[-1])) { Remove-Item -LiteralPath $a[-1] -Recurse -Force }
                        return & $ok
                    }
                    default { return & $ok }
                }
            }
            'taskkill' { [void]$m.Procs.Remove([int]$Arguments[1]); return & $ok }
            'schtasks' { if ($Arguments[0] -eq '/Query') { return & $exit $(if ($m.TaskExists) { 0 } else { 1 }) } return & $ok }
            default { return & $ok }
        }
    }
    $m
}

function Get-AgentCall { param([string]$Command, [string]$Pattern) @($global:CmCalls | Where-Object { $_.Command -eq $Command -and ($_.Arguments -join ' ') -match $Pattern }) }
function Get-AgentStarts { @($global:CmCalls | Where-Object { $_.Command -eq 'start' }) }

function Invoke-TestWatch {
    <#
      Runs one watcher pass against a throwaway root (with the owner's checkout in <root>/owner-checkout and the
      deployer's own clone, which the agent must never use, at <root>/repo). -LockSpec leaves a lock from an earlier run:
      Issue, ProcessId, MinutesAgo, Alive, ExitCode (omit = no exit file), LogLines, Attempts, NoWorktree.
    #>
    param(
        [hashtable]$Machine = @{},
        [string[]]$EnvLines = @(),
        [hashtable]$State = $null,
        [hashtable]$LockSpec = $null,
        [switch]$DryRun,
        [scriptblock]$Setup = $null,
        [scriptblock]$Inspect = $null
    )
    $root = New-TestRoot
    try {
        $repo = Join-Path $root 'owner-checkout'
        New-Item -ItemType Directory -Force -Path (Join-Path $repo '.git') | Out-Null
        Add-Content -LiteralPath (Join-Path $root '.env') -Value (@("CM_AGENT_REPO=$repo") + $EnvLines)
        $m = New-AgentMachine $Machine
        $paths = @{ Repo = $repo }
        if ($State) {
            New-Item -ItemType Directory -Force -Path (Join-Path $root 'state') | Out-Null
            $State | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'state/agent-state.json')
        }
        if ($LockSpec) {
            $issue = [int]$LockSpec.Issue
            $dir = Join-Path $root 'logs/agent'
            New-Item -ItemType Directory -Force -Path $dir, (Join-Path $root 'state') | Out-Null
            $paths.Log = Join-Path $dir "issue-$issue-20261031-134500.log"
            $paths.ExitFile = Join-Path $dir "issue-$issue-20261031-134500.exit.json"
            $paths.Worktree = [IO.Path]::Combine($repo, '.claude', 'worktrees', "agent-issue-$issue")
            if ($LockSpec.LogLines) { Set-Content -LiteralPath $paths.Log -Value $LockSpec.LogLines }
            $started = $script:AgentNow.AddMinutes(-[double]$LockSpec.MinutesAgo)
            if ($LockSpec.ContainsKey('ExitCode')) {
                $exitData = @{ exitCode = $LockSpec.ExitCode; timedOut = $false }
                if ($LockSpec.ContainsKey('DurationSeconds')) { $exitData.endedUnix = $started.ToUnixTimeSeconds() + [long]$LockSpec.DurationSeconds }
                $exitData | ConvertTo-Json | Set-Content -LiteralPath $paths.ExitFile
            }
            if (-not $LockSpec.NoWorktree) {
                New-Item -ItemType Directory -Force -Path $paths.Worktree | Out-Null
                Set-Content -LiteralPath (Join-Path $paths.Worktree '.git') -Value 'gitdir: elsewhere'
            }
            $lock = @{
                issue = $issue; pid = [int]$LockSpec.ProcessId; started = $started.ToString('o'); startedUnix = $started.ToUnixTimeSeconds()
                repoDir = $repo; worktree = $paths.Worktree; branch = "agent/issue-$issue"; log = $paths.Log; exitFile = $paths.ExitFile
                model = 'sonnet'; attempts = $(if ($LockSpec.Attempts) { [int]$LockSpec.Attempts } else { 0 })
            }
            $lock | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'state/agent.lock')
            if ($LockSpec.Alive) { $m.Procs[[int]$LockSpec.ProcessId] = [pscustomobject]@{ Id = [int]$LockSpec.ProcessId; Name = 'conhost'; StartTime = $started.UtcDateTime.AddSeconds(2) } }
        }
        if ($Setup) { & $Setup $root $repo }
        Initialize-Cm -Apply (-not $DryRun)
        $ctx = New-CmAgentContext -Root $root -Repo $script:AgentSlug -AgentDir $script:AgentDir
        $all = @(Invoke-CmAgentWatch -Ctx $ctx 6>&1)
        $host_ = ($all | Where-Object { $_ -is [System.Management.Automation.InformationRecord] } | ForEach-Object { "$_" }) -join "`n"
        $result = $all | Where-Object { $_ -isnot [System.Management.Automation.InformationRecord] } | Select-Object -Last 1
        $logPath = Join-Path $root 'logs/agent-watcher.log'
        $statePath = Join-Path $root 'state/agent-state.json'
        $lockPath = Join-Path $root 'state/agent.lock'
        [pscustomobject]@{
            Result = $result; Root = $root; Repo = $repo; Paths = $paths; Machine = $m; Screen = $host_
            Log = $(if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Raw } else { '' })
            State = $(if (Test-Path -LiteralPath $statePath) { Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable } else { @{} })
            Lock = $(if (Test-Path -LiteralPath $lockPath) { Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable } else { $null })
            Inspected = $(if ($Inspect) { & $Inspect $root $repo } else { $null })
        }
    }
    finally { Remove-TestRoot $root }
}

# ---- which issue is picked ---------------------------------------------------------------------------------------------

Test-Case 'pick: the oldest queued issue goes first when priorities tie' {
    $r = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 5 -Created '2026-10-30T12:00:00Z'), (New-AgentIssue 3 -Created '2026-10-29T08:00:00Z'), (New-AgentIssue 4 -Created '2026-10-30T09:00:00Z')) }
    Assert-Equal 'started' $r.Result.Outcome
    Assert-Equal 3 $r.Result.Issue
}

Test-Case 'pick: p1 before p3 before an issue with no p label (which counts as p3), then oldest first' {
    $issues = @(
        (New-AgentIssue 10 @('agent-task', 'p3') -Created '2026-10-30T09:00:00Z'),
        (New-AgentIssue 11 @('agent-task') -Created '2026-10-29T09:00:00Z'),
        (New-AgentIssue 12 @('agent-task', 'p1') -Created '2026-10-31T09:00:00Z'),
        (New-AgentIssue 13 @('agent-task', 'p5') -Created '2026-10-01T09:00:00Z'))
    Assert-Equal 12 (Invoke-TestWatch -Machine @{ Issues = $issues }).Result.Issue 'p1 wins though it is the newest'
    $noP1 = @($issues | Where-Object { $_.number -ne 12 })
    Assert-Equal 11 (Invoke-TestWatch -Machine @{ Issues = $noP1 }).Result.Issue 'no label = p3, and it is older than the p3 one'
    $onlyLow = @($issues | Where-Object { $_.number -in 13, 10 })
    Assert-Equal 10 (Invoke-TestWatch -Machine @{ Issues = $onlyLow }).Result.Issue 'p3 beats p5'
}

Test-Case 'pick: an issue by anyone but the owner is ignored, even with agent-task on it' {
    $r = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 1 -Author 'someone-else'), (New-AgentIssue 2 -Author 'COLLECTIONMAXXING-BOT')) }
    Assert-Equal 'idle' $r.Result.Outcome
    Assert-Equal 0 (Get-AgentStarts).Count
    Assert-Equal 0 (Get-MutatingCalls).Count
    $own = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 1 -Author 'someone-else'), (New-AgentIssue 2 -Author 'CoryFinnegan')) }
    Assert-Equal 2 $own.Result.Issue 'the owner is matched without regard to case'
}

Test-Case 'pick: agent-working, agent-blocked, needs:cory and status:blocked are each skipped' {
    foreach ($label in 'agent-working', 'agent-blocked', 'needs:cory', 'status:blocked') {
        $r = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 1 @('agent-task', $label)), (New-AgentIssue 2 @('agent-task', 'p5'))) }
        Assert-Equal 2 $r.Result.Issue "an issue labelled $label is skipped"
        $none = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 1 @('agent-task', $label))) }
        Assert-Equal 'idle' $none.Result.Outcome "$label alone leaves nothing to do"
    }
}

Test-Case 'pick: an issue without agent-task is not work, whatever the list says' {
    $r = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 1 @('type:bug', 'p1'))) }
    Assert-Equal 'idle' $r.Result.Outcome
}

Test-Case 'idle: nothing to do costs exactly one gh call, starts nothing and writes nothing' {
    $r = Invoke-TestWatch -Machine @{ Issues = @() }
    Assert-Equal 'idle' $r.Result.Outcome
    Assert-Equal 0 $r.Result.ExitCode
    Assert-Equal 1 $global:CmCalls.Count 'one API call and nothing else'
    Assert-Equal 'gh' $global:CmCalls[0].Command
    Assert-Match ($global:CmCalls[0].Arguments -join ' ') '^issue list --repo coryfinnegan/CollectionMaxxing --state open --label agent-task --limit \d+ --json number,title,labels,author,createdAt$'
    Assert-Equal 0 (Get-AgentStarts).Count
    Assert-Equal '' $r.Log 'a quiet pass leaves the log alone'
    Assert-Equal 0 $r.State.Count 'and the state file'
    Assert-Equal $null $r.Lock
}

Test-Case 'idle: a failing gh is a warning and exit 3, and nothing is claimed' {
    $r = Invoke-TestWatch -Machine @{ ListExit = 1; Issues = @((New-AgentIssue 1)) }
    Assert-Equal 'gh-failed' $r.Result.Outcome
    Assert-Equal 3 $r.Result.ExitCode
    Assert-Match $r.Log 'WARN .*gh issue list'
    Assert-Equal 0 (Get-MutatingCalls).Count
}

# ---- the switch, the cap, the lock ------------------------------------------------------------------------------------------

Test-Case 'kill switch: CM_AGENT_ENABLED=0 (or false, off, no) exits before any call' {
    foreach ($off in '0', 'false', 'OFF', 'no') {
        $r = Invoke-TestWatch -EnvLines @("CM_AGENT_ENABLED=$off") -Machine @{ Issues = @((New-AgentIssue 1)) }
        Assert-Equal 'disabled' $r.Result.Outcome "CM_AGENT_ENABLED=$off"
        Assert-Equal 0 $r.Result.ExitCode
        Assert-Equal 0 $global:CmCalls.Count 'not even gh issue list'
        Assert-Equal '' $r.Log
    }
}

Test-Case 'kill switch: unset or 1 leaves the agent on' {
    Assert-Equal 'started' (Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 1)) }).Result.Outcome
    Assert-Equal 'started' (Invoke-TestWatch -EnvLines @('CM_AGENT_ENABLED=1') -Machine @{ Issues = @((New-AgentIssue 1)) }).Result.Outcome
}

Test-Case 'cap: the eighth run of a day is the last by default, and the ninth makes no call at all' {
    $seventh = Invoke-TestWatch -State @{ day = 20261031; runs = 7 } -Machine @{ Issues = @((New-AgentIssue 1)) }
    Assert-Equal 'started' $seventh.Result.Outcome
    Assert-Equal 8 $seventh.State.runs
    $global:CmCalls.Clear()
    $eighth = Invoke-TestWatch -State @{ day = 20261031; runs = 8 } -Machine @{ Issues = @((New-AgentIssue 1)) }
    Assert-Equal 'cap' $eighth.Result.Outcome
    Assert-Equal 0 $global:CmCalls.Count 'the cap is checked before gh is asked anything'
    Assert-Match $eighth.Log 'daily cap of 8'
}

Test-Case 'cap: CM_AGENT_MAX_RUNS_PER_DAY changes it, the log says so once a day, and a new day starts at zero' {
    $first = Invoke-TestWatch -EnvLines @('CM_AGENT_MAX_RUNS_PER_DAY=2') -State @{ day = 20261031; runs = 2 }
    Assert-Equal 'cap' $first.Result.Outcome
    Assert-Equal 20261031 $first.State.capNotedDay
    $again = Invoke-TestWatch -EnvLines @('CM_AGENT_MAX_RUNS_PER_DAY=2') -State $first.State
    Assert-Equal 'cap' $again.Result.Outcome
    Assert-Equal '' $again.Log 'said once, not every 5 minutes'
    $nextDay = Invoke-TestWatch -EnvLines @('CM_AGENT_MAX_RUNS_PER_DAY=2') -State @{ day = 20261030; runs = 2 } -Machine @{ Issues = @((New-AgentIssue 1)) }
    Assert-Equal 'started' $nextDay.Result.Outcome
    Assert-Equal 20261031 $nextDay.State.day
    Assert-Equal 1 $nextDay.State.runs
    Assert-Equal 'cap' (Invoke-TestWatch -EnvLines @('CM_AGENT_MAX_RUNS_PER_DAY=0') -Machine @{ Issues = @((New-AgentIssue 1)) }).Result.Outcome '0 pauses new runs'
}

Test-Case 'lock: a run that is alive and inside its time limit makes the watcher exit at once' {
    $r = Invoke-TestWatch -LockSpec @{ Issue = 7; ProcessId = 3100; MinutesAgo = 20; Alive = $true } -Machine @{ Issues = @((New-AgentIssue 8)) }
    Assert-Equal 'running' $r.Result.Outcome
    Assert-Equal 0 $r.Result.ExitCode
    Assert-Equal 0 $global:CmCalls.Count 'no gh call while a run is alive'
    Assert-Equal 7 $r.Lock.issue 'the lock stays'
    Assert-Equal '' $r.Log
}

Test-Case 'lock: a lock whose process is gone is cleared and its issue is marked blocked, and the worktree is kept' {
    $log = 1..5 | ForEach-Object { "agent output line $_" }
    $r = Invoke-TestWatch -LockSpec @{ Issue = 7; ProcessId = 3100; MinutesAgo = 20; Alive = $false; LogLines = $log } -Inspect {
        param($root, $repo) Test-Path -LiteralPath ([IO.Path]::Combine($repo, '.claude', 'worktrees', 'agent-issue-7', '.git'))
    }
    Assert-Equal $null $r.Lock 'the lock is gone'
    $edit = Get-AgentCall 'gh' '^issue edit 7 '
    Assert-Equal 1 $edit.Count
    Assert-Match ($edit[0].Arguments -join ' ') '--add-label agent-blocked'
    Assert-Match ($edit[0].Arguments -join ' ') '--remove-label agent-working'
    $comment = Get-AgentCall 'gh' '^issue comment 7 '
    Assert-Equal 1 $comment.Count
    Assert-Match $comment[0].InputText 'no exit code'
    Assert-Match $comment[0].InputText 'agent output line 5'
    Assert-Equal 0 (Get-AgentCall 'git' 'worktree remove').Count
    Assert-True $r.Inspected 'the worktree is kept for inspection'
}

Test-Case 'lock: a process id that now belongs to a younger process is not the agent' {
    $r = Invoke-TestWatch -LockSpec @{ Issue = 7; ProcessId = 3100; MinutesAgo = 60 } -Setup {
        $global:CmAgentMachine.Procs[3100] = [pscustomobject]@{ Id = 3100; Name = 'notepad'; StartTime = $script:AgentNow.UtcDateTime.AddMinutes(-5) }
    }
    Assert-Equal $null $r.Lock 'treated as gone: the process started long after the run did'
    Assert-Equal 0 (Get-AgentCall 'taskkill' '.').Count 'and nobody else''s process is killed'
}

Test-Case 'lock: a run past its time limit is killed with its whole process tree, then marked blocked' {
    $r = Invoke-TestWatch -LockSpec @{ Issue = 7; ProcessId = 3100; MinutesAgo = 181; Alive = $true } -EnvLines @('CM_AGENT_MAX_MINUTES=180')
    $kill = Get-AgentCall 'taskkill' '.'
    Assert-Equal 1 $kill.Count
    Assert-Equal '/PID 3100 /T /F' ($kill[0].Arguments -join ' ')
    Assert-True $kill[0].Mutating
    Assert-Equal $null $r.Lock
    $comment = Get-AgentCall 'gh' '^issue comment 7 '
    Assert-Match $comment[0].InputText '180 minutes'
    $edit = Get-AgentCall 'gh' '^issue edit 7 '
    Assert-Match ($edit[0].Arguments -join ' ') '--add-label agent-blocked --remove-label agent-working'
}

Test-Case 'lock: CM_AGENT_MAX_MINUTES decides how long is too long' {
    $inside = Invoke-TestWatch -LockSpec @{ Issue = 7; ProcessId = 3100; MinutesAgo = 100; Alive = $true } -EnvLines @('CM_AGENT_MAX_MINUTES=120')
    Assert-Equal 'running' $inside.Result.Outcome
    $outside = Invoke-TestWatch -LockSpec @{ Issue = 7; ProcessId = 3100; MinutesAgo = 100; Alive = $true } -EnvLines @('CM_AGENT_MAX_MINUTES=60')
    Assert-Equal 1 (Get-AgentCall 'taskkill' '.').Count
}

Test-Case 'lock: only one watcher pass runs at a time' {
    $root = New-TestRoot
    $held = $null
    try {
        New-AgentMachine @{ Issues = @((New-AgentIssue 1)) } | Out-Null
        Initialize-Cm -Apply $true
        $ctx = New-CmAgentContext -Root $root -Repo $script:AgentSlug -AgentDir $script:AgentDir
        $held = Enter-CmLock -Path $ctx.WatcherLockFile
        $result = @(Invoke-CmAgentWatch -Ctx $ctx 6>$null) | Select-Object -Last 1
        Assert-Equal 'busy' $result.Outcome
        Assert-Equal 0 $global:CmCalls.Count
    }
    finally { Exit-CmLock $held; Remove-TestRoot $root }
}

# ---- starting a run ---------------------------------------------------------------------------------------------------------

Test-Case 'start: the issue is claimed first, then a worktree is cut from origin/main, then the run starts detached' {
    $r = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 7 @('agent-task', 'tier:standard', 'p2'))) }
    Assert-Equal 'started' $r.Result.Outcome
    $edit = Get-AgentCall 'gh' '^issue edit 7 '
    Assert-Equal "issue edit 7 --repo $script:AgentSlug --add-label agent-working" ($edit[0].Arguments -join ' ')
    $comment = Get-AgentCall 'gh' '^issue comment 7 '
    Assert-Equal "issue comment 7 --repo $script:AgentSlug --body-file -" ($comment[0].Arguments -join ' ')
    Assert-Match $comment[0].InputText 'Picked up at 2026-10-31 14:05'
    $worktree = [IO.Path]::Combine($r.Repo, '.claude', 'worktrees', 'agent-issue-7')
    $fetch = Get-AgentCall 'git' ' fetch '
    Assert-Equal "-C $($r.Repo) fetch origin" ($fetch[0].Arguments -join ' ')
    $add = Get-AgentCall 'git' ' worktree add '
    Assert-Equal 1 $add.Count
    Assert-Equal "-C $($r.Repo) worktree add $worktree -b agent/issue-7 origin/main" ($add[0].Arguments -join ' ')
    $order = @($global:CmCalls | ForEach-Object { if ($_.Command -eq 'start') { 'start' } else { $_.Command + ' ' + ($_.Arguments -join ' ') } })
    $iFetch = [array]::FindIndex($order, [Predicate[string]] { param($s) $s -like 'git -C * fetch origin' })
    $iEdit = [array]::FindIndex($order, [Predicate[string]] { param($s) $s -like 'gh issue edit*' })
    $iAdd = [array]::FindIndex($order, [Predicate[string]] { param($s) $s -like '*worktree add*' })
    $iStart = [array]::IndexOf($order, 'start')
    Assert-True ($iFetch -ge 0 -and $iFetch -lt $iEdit -and $iEdit -lt $iAdd -and $iAdd -lt $iStart) "order was: $($order -join ' | ')"
    Assert-True ((Get-AgentCall 'git' ' fetch ')[0].Mutating) 'a fetch changes the checkout, so it is not run in a dry run'
}

Test-Case 'start: the run is a headless pwsh runner with the model, the prompt file, the log and the limit' {
    $r = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 7 @('agent-task', 'tier:critical'))) } -EnvLines @('CM_AGENT_MAX_MINUTES=90')
    $start = Get-AgentStarts
    Assert-Equal 1 $start.Count
    Assert-Match $start[0].File '(^|[\\/])conhost(\.exe)?$'
    $line = $start[0].Arguments[0]
    Assert-Match $line '^--headless "[^"]*pwsh(\.exe)?" -NoProfile -ExecutionPolicy Bypass -File "[^"]*Run-AgentIssue\.ps1" '
    Assert-Match $line ' -Issue 7 '
    Assert-Match $line ' -Model opus '
    Assert-Match $line ' -Claude "C:\\fake\\claude\.exe" '
    Assert-Match $line ' -PromptFile "[^"]*issue-7-20261031-140500\.prompt\.md" '
    Assert-Match $line ' -LogFile "[^"]*issue-7-20261031-140500\.log" '
    Assert-Match $line ' -ExitFile "[^"]*issue-7-20261031-140500\.exit\.json" '
    Assert-Match $line ' -WorkDir "[^"]*agent-issue-7" '
    Assert-Match $line ' -MaxMinutes 90$'
    Assert-Equal $r.Paths.Repo $r.Repo
}

Test-Case 'start: the lock records the run, the state counts it, and the run is logged' {
    $r = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 7)) }
    Assert-Equal 7 $r.Lock.issue
    Assert-Equal 4242 $r.Lock.pid
    Assert-Equal $script:AgentNow.ToUnixTimeSeconds() $r.Lock.startedUnix
    Assert-Equal 'agent/issue-7' $r.Lock.branch
    Assert-Equal $r.Repo $r.Lock.repoDir
    Assert-Match $r.Lock.log 'issue-7-20261031-140500\.log$'
    Assert-Equal 20261031 $r.State.day
    Assert-Equal 1 $r.State.runs
    Assert-Match $r.Log 'started the agent for issue #7 \(model sonnet\)'
}

Test-Case 'start: a second pass while that run lives starts nothing new' {
    $first = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 7), (New-AgentIssue 8)) } -Inspect {
        param($root, $repo)
        # The first pass left the lock; a second pass (same root) must find the run alive.
        Initialize-Cm -Apply $true
        $ctx = New-CmAgentContext -Root $root -Repo $script:AgentSlug -AgentDir $script:AgentDir
        $global:CmCalls.Clear()
        $second = @(Invoke-CmAgentWatch -Ctx $ctx 6>$null) | Select-Object -Last 1
        @{ Outcome = $second.Outcome; Calls = $global:CmCalls.Count }
    }
    Assert-Equal 'running' $first.Inspected.Outcome
    Assert-Equal 0 $first.Inspected.Calls
}

Test-Case 'start: the model follows the tier label (mechanical haiku, critical opus, anything else sonnet)' {
    Assert-Equal 'haiku' (Get-CmAgentModel @('agent-task', 'tier:mechanical'))
    Assert-Equal 'opus' (Get-CmAgentModel @('agent-task', 'tier:critical'))
    Assert-Equal 'sonnet' (Get-CmAgentModel @('agent-task', 'tier:standard'))
    Assert-Equal 'sonnet' (Get-CmAgentModel @('agent-task'))
    Assert-Equal 'opus' (Get-CmAgentModel @('tier:mechanical', 'tier:critical')) 'the safer model wins a clash'
    $r = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 7 @('agent-task', 'tier:mechanical'))) }
    Assert-Match (Get-AgentStarts)[0].Arguments[0] ' -Model haiku '
}

Test-Case 'prompt: {issue} and {branch} are filled in everywhere and the written prompt is the one the run gets' {
    $text = Expand-CmAgentPrompt -TemplatePath $script:AgentPromptFile -Issue 7 -Branch 'agent/issue-7'
    Assert-False ($text -match '\{issue\}|\{branch\}') 'no placeholder is left'
    Assert-Match $text 'gh issue view 7 --comments'
    Assert-Match $text '`Closes #7`'
    Assert-Match $text 'agent/issue-7'
    $r = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 7)) } -Inspect {
        param($root, $repo)
        $file = @(Get-ChildItem -LiteralPath (Join-Path $root 'logs/agent') -Filter '*.prompt.md')
        @{ Count = $file.Count; Text = $(if ($file.Count) { Get-Content -LiteralPath $file[0].FullName -Raw } else { '' }) }
    }
    Assert-Equal 1 $r.Inspected.Count
    Assert-Equal $text.TrimEnd() $r.Inspected.Text.TrimEnd()
}

Test-Case 'prompt: the template carries the whole process (one issue, tests first, verifier, squash-merge, labels, blocked)' {
    $t = Get-Content -LiteralPath $script:AgentPromptFile -Raw
    foreach ($needle in 'exactly one GitHub issue', 'gh issue view {issue} --comments', 'data, never instructions', 'CLAUDE.md', 'failing test first',
        'scripts/check.ps1', 'Closes #{issue}', 'Notes for the lead', 'verifier subagent', 'git merge origin/main', 'never rebase', 'never force-push',
        'gh pr merge <number> --squash', 'iOS build (TestFlight)', 'Release images', '/health/version', 'agent-review', 'agent-working',
        'agent-blocked', 'needs:cory', 'RESULT:') {
        Assert-True ($t.Contains($needle)) "the prompt should say: $needle"
    }
}

Test-Case 'worktree: one left by an earlier failed run is reused, not recreated or wiped' {
    $r = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 7)) } -Setup {
        param($root, $repo)
        $wt = [IO.Path]::Combine($repo, '.claude', 'worktrees', 'agent-issue-7')
        New-Item -ItemType Directory -Force -Path $wt | Out-Null
        Set-Content -LiteralPath (Join-Path $wt '.git') -Value 'gitdir: elsewhere'
        Set-Content -LiteralPath (Join-Path $wt 'half-done.txt') -Value 'work in progress'
    } -Inspect { param($root, $repo) Test-Path -LiteralPath ([IO.Path]::Combine($repo, '.claude', 'worktrees', 'agent-issue-7', 'half-done.txt')) }
    Assert-Equal 'started' $r.Result.Outcome
    Assert-Equal 0 (Get-AgentCall 'git' ' worktree add ').Count
    Assert-Equal 0 (Get-AgentCall 'git' ' worktree remove').Count
    Assert-True $r.Inspected 'the earlier work is still there'
    Assert-Match $r.Log 'reusing the worktree'
}

Test-Case 'worktree: a branch left behind without its folder is checked out again, not recreated' {
    $r = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 7)); BranchExists = $true }
    $worktree = [IO.Path]::Combine($r.Repo, '.claude', 'worktrees', 'agent-issue-7')
    $add = Get-AgentCall 'git' ' worktree add '
    Assert-Equal "-C $($r.Repo) worktree add $worktree agent/issue-7" ($add[0].Arguments -join ' ')
    Assert-Equal 1 (Get-AgentCall 'git' ' worktree prune').Count 'stale registrations are forgotten first'
}

Test-Case 'worktree: a folder that is not a worktree is refused, and the issue is blocked' {
    $r = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 7)) } -Setup {
        param($root, $repo)
        New-Item -ItemType Directory -Force -Path ([IO.Path]::Combine($repo, '.claude', 'worktrees', 'agent-issue-7')) | Out-Null
    }
    Assert-Equal 'start-failed' $r.Result.Outcome
    Assert-Equal 0 (Get-AgentStarts).Count
    Assert-Match (Get-AgentCall 'gh' '^issue comment 7 ')[1].InputText 'is not a git worktree'
}

Test-Case 'worktree: a failed git worktree add blocks the issue and starts no run' {
    $r = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 7)); WorktreeAddExit = 128 }
    Assert-Equal 'start-failed' $r.Result.Outcome
    Assert-Equal 1 $r.Result.ExitCode
    Assert-Equal 0 (Get-AgentStarts).Count
    Assert-Equal $null $r.Lock
    $edits = Get-AgentCall 'gh' '^issue edit 7 '
    Assert-Equal 2 $edits.Count 'claim, then block'
    Assert-Match ($edits[1].Arguments -join ' ') '--add-label agent-blocked --remove-label agent-working'
    Assert-Match (Get-AgentCall 'gh' '^issue comment 7 ')[1].InputText 'worktree'
}

Test-Case 'worktree: a failed fetch claims nothing, so a network blip costs no comment and the next pass retries' {
    $r = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 7)); FetchExit = 128 }
    Assert-Equal 'fetch-failed' $r.Result.Outcome
    Assert-Equal 3 $r.Result.ExitCode
    Assert-Equal 0 (Get-AgentCall 'gh' '^issue (edit|comment)').Count 'the issue is not touched'
    Assert-Equal 0 (Get-AgentCall 'git' ' worktree add ').Count
    Assert-Equal 0 (Get-AgentStarts).Count
    Assert-Equal 0 $r.State.Count 'and the day''s count does not move'
    Assert-Match $r.Log 'WARN .*git fetch origin failed'
}

Test-Case 'start: a run that cannot be launched blocks the issue and leaves no lock' {
    $r = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 7)); StartThrows = $true }
    Assert-Equal 'start-failed' $r.Result.Outcome
    Assert-Equal $null $r.Lock
    Assert-Match (Get-AgentCall 'gh' '^issue comment 7 ')[1].InputText 'could not start'
}

Test-Case 'start: a claim that fails starts nothing' {
    $r = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 7)); EditExit = 1 }
    Assert-Equal 'claim-failed' $r.Result.Outcome
    Assert-Equal 0 (Get-AgentCall 'git' 'worktree').Count 'no worktree is made for an issue that was not claimed'
    Assert-Equal 0 (Get-AgentStarts).Count
}

Test-Case 'config: nothing is claimed when the checkout is missing, is the deployer''s own clone, or claude is not installed' {
    $missing = Invoke-TestWatch -EnvLines @('CM_AGENT_REPO=/definitely/not/here') -Machine @{ Issues = @((New-AgentIssue 7)) }
    Assert-Equal 'config-error' $missing.Result.Outcome
    Assert-Equal 2 $missing.Result.ExitCode
    Assert-Equal 0 (Get-MutatingCalls).Count
    $deployerClone = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 7)) } -Setup {
        param($root, $repo)
        New-Item -ItemType Directory -Force -Path (Join-Path $root 'repo/.git') | Out-Null
        Add-Content -LiteralPath (Join-Path $root '.env') -Value "CM_AGENT_REPO=$(Join-Path $root 'repo')"
    }
    Assert-Equal 'config-error' $deployerClone.Result.Outcome
    Assert-Match $deployerClone.Log 'deployer''s own clone'
    Assert-Equal 0 (Get-MutatingCalls).Count
    $inside = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 7)) } -Setup {
        param($root, $repo)
        New-Item -ItemType Directory -Force -Path (Join-Path $root 'repo/deploy/.git') | Out-Null
        Add-Content -LiteralPath (Join-Path $root '.env') -Value "CM_AGENT_REPO=$(Join-Path $root 'repo/deploy')"
    }
    Assert-Equal 'config-error' $inside.Result.Outcome 'a folder inside the deployer''s clone is just as bad'
    $noClaude = Invoke-TestWatch -Machine @{ Issues = @((New-AgentIssue 7)); Claude = '' }
    Assert-Equal 'config-error' $noClaude.Result.Outcome
    Assert-Match $noClaude.Log 'claude'
    Assert-Equal 0 (Get-MutatingCalls).Count
}

Test-Case 'config: the checkout defaults to the owner''s source folder, never to the deployer''s clone' {
    $settings = Get-CmAgentSettings -EnvMap @{}
    Assert-Match $settings.Repo 'source[\\/]repos[\\/]CollectionMaxxing$'
    Assert-True ($settings.Repo.StartsWith($HOME)) 'resolved from the home folder, not typed into the repo'
    Assert-Equal 8 $settings.MaxRunsPerDay
    Assert-Equal 180 $settings.MaxMinutes
    $custom = Get-CmAgentSettings -EnvMap @{ CM_AGENT_MAX_RUNS_PER_DAY = '3'; CM_AGENT_MAX_MINUTES = '45'; CM_AGENT_REPO = '/x/y' }
    Assert-Equal 3 $custom.MaxRunsPerDay
    Assert-Equal 45 $custom.MaxMinutes
    Assert-Equal '/x/y' $custom.Repo
    $bad = Get-CmAgentSettings -EnvMap @{ CM_AGENT_MAX_RUNS_PER_DAY = 'lots'; CM_AGENT_MAX_MINUTES = '-5' }
    Assert-Equal 8 $bad.MaxRunsPerDay 'garbage falls back to the default'
    Assert-Equal 180 $bad.MaxMinutes
}

Test-Case 'dry run: a pass without -Apply reads, prints what it would run, and changes nothing' {
    $r = Invoke-TestWatch -DryRun -Machine @{ Issues = @((New-AgentIssue 7)) }
    Assert-Equal 'dry-run' $r.Result.Outcome
    Assert-Equal 0 (Get-MutatingCalls).Count 'no mutating command reached the runner'
    Assert-Equal 0 (Get-AgentStarts).Count
    Assert-Equal $null $r.Lock
    Assert-Equal 0 $r.State.Count
    Assert-Match $r.Screen 'dry run, would run: gh issue edit 7'
    Assert-Match $r.Screen 'worktree add'
    Assert-Match $r.Screen 'dry run, would start'
}

# ---- closing out a finished or stuck run ----------------------------------------------------------------------------------

Test-Case 'finish: a merged PR closes the run out cleanly: the worktree and branch go, no block, the lock clears' {
    $r = Invoke-TestWatch -LockSpec @{ Issue = 7; ProcessId = 3100; MinutesAgo = 50; ExitCode = 0; LogLines = @('done') } -Machine @{
        IssueView = @{ 7 = @{ state = 'CLOSED'; labels = @('agent-task', 'agent-review') } }
        Prs = @{ 'agent/issue-7' = @(@{ number = 21; state = 'MERGED'; mergedAt = '2026-10-31T17:50:00Z'; url = 'https://github.com/x/y/pull/21' }) }
    }
    Assert-Equal 0 (Get-AgentCall 'gh' '^issue comment').Count 'a success posts nothing'
    $remove = Get-AgentCall 'git' ' worktree remove '
    Assert-Equal 1 $remove.Count
    Assert-Equal "-C $($r.Repo) worktree remove --force $($r.Paths.Worktree)" ($remove[0].Arguments -join ' ')
    Assert-Equal "-C $($r.Repo) branch -D agent/issue-7" ((Get-AgentCall 'git' ' branch -D ')[0].Arguments -join ' ')
    Assert-False (Test-Path -LiteralPath $r.Paths.Worktree) 'the folder is gone'
    Assert-Equal $null $r.Lock
    $edit = Get-AgentCall 'gh' '^issue edit 7 '
    Assert-Match ($edit[0].Arguments -join ' ') '--remove-label agent-review'
    Assert-False (($edit[0].Arguments -join ' ') -match 'agent-blocked')
}

Test-Case 'finish: an agent that blocked itself is left alone: no second comment, worktree kept, lock cleared' {
    $r = Invoke-TestWatch -LockSpec @{ Issue = 7; ProcessId = 3100; MinutesAgo = 50; ExitCode = 0; LogLines = @('RESULT: blocked needs a product decision') } -Machine @{
        IssueView = @{ 7 = @{ state = 'OPEN'; labels = @('agent-task', 'agent-blocked', 'needs:cory') } }
    } -Inspect { param($root, $repo) Test-Path -LiteralPath ([IO.Path]::Combine($repo, '.claude', 'worktrees', 'agent-issue-7', '.git')) }
    Assert-Equal 0 (Get-AgentCall 'gh' '^issue (comment|edit)').Count
    Assert-Equal 0 (Get-AgentCall 'git' ' worktree remove').Count
    Assert-True $r.Inspected 'the worktree folder is still there'
    Assert-Equal $null $r.Lock
}

Test-Case 'finish: a non-zero exit blocks the issue with the exit code, and the worktree is kept for inspection' {
    $r = Invoke-TestWatch -LockSpec @{ Issue = 7; ProcessId = 3100; MinutesAgo = 50; ExitCode = 1; LogLines = @('Error: something went wrong') } -Machine @{
        IssueView = @{ 7 = @{ state = 'OPEN'; labels = @('agent-task', 'agent-review') } }
    } -Inspect { param($root, $repo) Test-Path -LiteralPath ([IO.Path]::Combine($repo, '.claude', 'worktrees', 'agent-issue-7', '.git')) }
    $edit = Get-AgentCall 'gh' '^issue edit 7 '
    Assert-Match ($edit[0].Arguments -join ' ') '--add-label agent-blocked'
    Assert-Match ($edit[0].Arguments -join ' ') '--remove-label agent-review'
    $body = (Get-AgentCall 'gh' '^issue comment 7 ')[0].InputText
    Assert-Match $body 'exit code 1'
    Assert-Match $body 'Something went wrong|something went wrong'
    Assert-Match $body 'worktree'
    Assert-True $r.Inspected 'kept'
    Assert-Equal 0 (Get-AgentCall 'git' ' worktree remove').Count
    Assert-Equal $null $r.Lock
}

Test-Case 'finish: a clean exit that left the issue open and still agent-working is blocked too' {
    $r = Invoke-TestWatch -LockSpec @{ Issue = 7; ProcessId = 3100; MinutesAgo = 50; ExitCode = 0; LogLines = @('I ran out of turns') }
    $body = (Get-AgentCall 'gh' '^issue comment 7 ')[0].InputText
    Assert-Match $body 'without finishing'
    Assert-Match ((Get-AgentCall 'gh' '^issue edit 7 ')[0].Arguments -join ' ') '--add-label agent-blocked --remove-label agent-working'
}

Test-Case 'finish: a merged PR on an issue that stayed open is blocked (so it is not run twice) but its worktree is cleaned' {
    $r = Invoke-TestWatch -LockSpec @{ Issue = 7; ProcessId = 3100; MinutesAgo = 50; ExitCode = 0; LogLines = @('merged') } -Machine @{
        IssueView = @{ 7 = @{ state = 'OPEN'; labels = @('agent-task') } }
        Prs = @{ 'agent/issue-7' = @(@{ number = 21; state = 'MERGED'; mergedAt = '2026-10-31T17:50:00Z'; url = 'https://github.com/x/y/pull/21' }) }
    }
    Assert-Match ((Get-AgentCall 'gh' '^issue edit 7 ')[0].Arguments -join ' ') '--add-label agent-blocked'
    Assert-Match (Get-AgentCall 'gh' '^issue comment 7 ')[0].InputText 'still open'
    Assert-Equal 1 (Get-AgentCall 'git' ' worktree remove').Count
}

Test-Case 'finish: the comment shows the last 30 log lines with every secret hidden' {
    $secret = 'tmdb-secret-token-123456'
    $token = 'ghp_' + ('A1b2C3d4' * 5)
    $lines = @(1..40 | ForEach-Object { "log line $('{0:00}' -f $_)" })
    $lines[37] = "cwd $HOME/work"
    $lines[38] = "calling tmdb with $secret now"
    $lines[39] = "gh auth said $token and Authorization: Bearer abcdefghijklmnop123456 and the password test-only-password"
    $r = Invoke-TestWatch -EnvLines @("CM_TMDB_API_KEY=$secret") -LockSpec @{ Issue = 7; ProcessId = 3100; MinutesAgo = 50; ExitCode = 1; LogLines = $lines }
    $body = (Get-AgentCall 'gh' '^issue comment 7 ')[0].InputText
    Assert-False $body.Contains($secret) 'the .env secret is hidden'
    Assert-False $body.Contains($token) 'a GitHub token is hidden by its shape'
    Assert-False $body.Contains('abcdefghijklmnop123456') 'a bearer token is hidden'
    Assert-False $body.Contains('test-only-password') 'the database password is hidden'
    Assert-Match $body '\*\*\*'
    Assert-Match $body 'log line 11'
    Assert-False ($body -match 'log line 10\b') 'only the last 30 lines are quoted'
    Assert-False $body.Contains($HOME) 'the home folder (and with it the account name) is not published'
    Assert-Equal 0 (@($global:CmCalls | Where-Object { $_.Command -eq 'gh' -and $_.Display.Contains($secret) }).Count) 'and no secret was ever on a command line'
}

Test-Case 'finish: a run killed for its time limit says so; one with no exit file says the exit is unknown' {
    $killed = Invoke-TestWatch -LockSpec @{ Issue = 7; ProcessId = 3100; MinutesAgo = 200; Alive = $true }
    Assert-Match (Get-AgentCall 'gh' '^issue comment 7 ')[0].InputText 'stopped'
    $global:CmCalls.Clear()
    $unknown = Invoke-TestWatch -LockSpec @{ Issue = 7; ProcessId = 3100; MinutesAgo = 20 }
    Assert-Match (Get-AgentCall 'gh' '^issue comment 7 ')[0].InputText 'no exit code'
}

Test-Case 'finish: when GitHub cannot be asked the lock stays and the next pass tries again, up to a limit' {
    $first = Invoke-TestWatch -LockSpec @{ Issue = 7; ProcessId = 3100; MinutesAgo = 20; ExitCode = 0 } -Machine @{ ViewExit = 1 }
    Assert-Equal 'finish-pending' $first.Result.Outcome
    Assert-Equal 1 $first.Lock.attempts
    Assert-Equal 0 (Get-AgentCall 'gh' '^issue list').Count 'nothing new starts until the old run is closed out'
    $last = Invoke-TestWatch -LockSpec @{ Issue = 7; ProcessId = 3100; MinutesAgo = 20; ExitCode = 0; Attempts = 11 } -Machine @{ ViewExit = 1 }
    Assert-Equal $null $last.Lock 'after 12 tries the lock is dropped so the service cannot stay stuck'
    Assert-Match $last.Log 'ERROR .*gave up'
}

Test-Case 'finish: after closing a run out, the same pass goes on to the next issue' {
    $r = Invoke-TestWatch -LockSpec @{ Issue = 7; ProcessId = 3100; MinutesAgo = 20; ExitCode = 1 } -Machine @{ Issues = @((New-AgentIssue 9)) }
    Assert-Equal 'started' $r.Result.Outcome
    Assert-Equal 9 $r.Result.Issue
    Assert-Equal 9 $r.Lock.issue
}

Test-Case 'finish: a merge of the same branch from before this run started does not count (a reopened issue)' {
    $r = Invoke-TestWatch -LockSpec @{ Issue = 7; ProcessId = 3100; MinutesAgo = 20; ExitCode = 1; LogLines = @('failed again') } -Machine @{
        IssueView = @{ 7 = @{ state = 'OPEN'; labels = @('agent-task', 'agent-working') } }
        Prs = @{ 'agent/issue-7' = @(@{ number = 9; state = 'MERGED'; mergedAt = '2026-10-20T10:00:00Z'; url = 'https://github.com/x/y/pull/9' }) }
    }
    Assert-Equal 0 (Get-AgentCall 'git' ' worktree remove').Count 'the worktree of the failed run is kept'
    Assert-Equal 0 (Get-AgentCall 'git' ' branch -D').Count
    $body = (Get-AgentCall 'gh' '^issue comment 7 ')[0].InputText
    Assert-Match $body 'exit code 1'
    Assert-False ($body -match 'merged, but the issue is still open')
}

Test-Case 'finish: only a worktree this service made is ever removed' {
    $r = Invoke-TestWatch -LockSpec @{ Issue = 7; ProcessId = 3100; MinutesAgo = 50; ExitCode = 0 } -Machine @{
        IssueView = @{ 7 = @{ state = 'CLOSED'; labels = @('agent-task') } }
        Prs = @{ 'agent/issue-7' = @(@{ number = 21; state = 'MERGED'; mergedAt = '2026-10-31T17:50:00Z'; url = 'u' }) }
    } -Setup {
        param($root, $repo)
        $lockPath = Join-Path $root 'state/agent.lock'
        $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
        $lock.worktree = $repo   # a lock that points at the owner's whole checkout
        $lock | ConvertTo-Json | Set-Content -LiteralPath $lockPath
    } -Inspect { param($root, $repo) Test-Path -LiteralPath (Join-Path $repo '.git') }
    Assert-Equal 0 (Get-AgentCall 'git' ' worktree remove').Count
    Assert-Equal 0 (Get-AgentCall 'git' ' branch -D').Count
    Assert-True $r.Inspected 'the owner''s checkout is untouched'
    Assert-Match $r.Log 'refusing to remove'
}

# ---- a run that fails at once must not burn the queue -----------------------------------------------------------------------

function Get-AgentQuickLock {
    <# A run that started 10 minutes ago and ended 2 minutes after it started, with the given exit code. #>
    param([int]$ExitCode = 1, [int]$DurationSeconds = 120, [string[]]$LogLines = @('=== header ===', 'Error: not signed in'))
    @{ Issue = 7; ProcessId = 3100; MinutesAgo = 10; ExitCode = $ExitCode; DurationSeconds = $DurationSeconds; LogLines = $LogLines }
}

Test-Case 'back-off: a non-zero exit within 5 minutes puts the issue back in the queue, says so once, pauses the service and is not counted' {
    $now = $script:AgentNow.ToUnixTimeSeconds()
    $lines = @(1..15 | ForEach-Object { "start line $('{0:00}' -f $_)" })
    $lines[1] = 'Error: not signed in, key tmdb-secret-token-123456'
    $r = Invoke-TestWatch -EnvLines @('CM_TMDB_API_KEY=tmdb-secret-token-123456') -State @{ day = 20261031; runs = 3 } -LockSpec (Get-AgentQuickLock -LogLines $lines) -Machine @{ Issues = @((New-AgentIssue 8)) } -Inspect {
        param($root, $repo) Test-Path -LiteralPath ([IO.Path]::Combine($repo, '.claude', 'worktrees', 'agent-issue-7', '.git'))
    }
    Assert-Equal 'paused' $r.Result.Outcome 'the pass stops here: the next issue does not start'
    $edit = Get-AgentCall 'gh' '^issue edit 7 '
    Assert-Equal 1 $edit.Count
    Assert-Equal "issue edit 7 --repo $script:AgentSlug --remove-label agent-working" ($edit[0].Arguments -join ' ') 'back in the queue: agent-task stays, agent-blocked is not added'
    $comment = Get-AgentCall 'gh' '^issue comment 7 '
    Assert-Equal 1 $comment.Count
    $body = $comment[0].InputText
    Assert-Match $body '^The agent couldn''t start \(exit 1; first log lines below, secrets hidden\)\. It will try again after 2026-10-31 15:05 -04:00\.'
    Assert-Match $body 'start line 10'
    Assert-False ($body -match 'start line 11') 'only the first 10 lines'
    Assert-False $body.Contains('tmdb-secret-token-123456') 'secrets are hidden'
    Assert-Equal 0 (Get-AgentCall 'gh' '^issue list').Count
    Assert-Equal ($now + 3600) $r.State.pauseUntilUnix 'the first pause is 60 minutes'
    Assert-Equal 1 $r.State.quickFailures
    Assert-Equal 2 $r.State.runs 'a quick failure does not count against the daily cap'
    Assert-Equal $null $r.Lock
    Assert-True $r.Inspected 'the worktree is kept'
    Assert-Match $r.Log 'WARN .*issue #7 .*paused'
}

Test-Case 'back-off: a second quick failure doubles the pause, up to 8 hours' {
    $now = $script:AgentNow.ToUnixTimeSeconds()
    foreach ($case in @(@{ Before = 0; Minutes = 60 }, @{ Before = 1; Minutes = 120 }, @{ Before = 2; Minutes = 240 }, @{ Before = 3; Minutes = 480 }, @{ Before = 4; Minutes = 480 }, @{ Before = 12; Minutes = 480 })) {
        $r = Invoke-TestWatch -State @{ quickFailures = $case.Before; pauseUntilUnix = $now - 5 } -LockSpec (Get-AgentQuickLock)
        Assert-Equal ($now + 60 * $case.Minutes) $r.State.pauseUntilUnix "after $($case.Before) earlier quick failures"
        Assert-Equal ($case.Before + 1) $r.State.quickFailures
    }
}

Test-Case 'back-off: while paused every pass exits with no call and no log line; when the time is up the queue is served again' {
    $now = $script:AgentNow.ToUnixTimeSeconds()
    $paused = Invoke-TestWatch -State @{ pauseUntilUnix = $now + 1800; quickFailures = 1 } -Machine @{ Issues = @((New-AgentIssue 8)) }
    Assert-Equal 'paused' $paused.Result.Outcome
    Assert-Equal 0 $paused.Result.ExitCode
    Assert-Equal 0 $global:CmCalls.Count
    Assert-Equal '' $paused.Log
    $over = Invoke-TestWatch -State @{ pauseUntilUnix = $now - 1; quickFailures = 1 } -Machine @{ Issues = @((New-AgentIssue 8)) }
    Assert-Equal 'started' $over.Result.Outcome
    Assert-Equal 8 $over.Result.Issue
}

Test-Case 'back-off: a run that got past 5 minutes, or that did not fail quickly, resets the streak' {
    $now = $script:AgentNow.ToUnixTimeSeconds()
    $state = @{ quickFailures = 3; pauseUntilUnix = $now - 60 }
    $slow = Invoke-TestWatch -State $state -LockSpec (Get-AgentQuickLock -DurationSeconds 600)
    Assert-Match ((Get-AgentCall 'gh' '^issue edit 7 ')[0].Arguments -join ' ') '--add-label agent-blocked' 'a failure after 5 minutes is a normal block'
    Assert-Equal 0 ([int]$slow.State.quickFailures)
    Assert-False $slow.State.ContainsKey('pauseUntilUnix') -Message 'no pause is left over'
    $clean = Invoke-TestWatch -State $state -LockSpec (Get-AgentQuickLock -ExitCode 0)
    Assert-Match ((Get-AgentCall 'gh' '^issue edit 7 ')[0].Arguments -join ' ') '--add-label agent-blocked' 'exit 0 is not a startup failure'
    Assert-Equal 0 ([int]$clean.State.quickFailures)
    $done = Invoke-TestWatch -State $state -LockSpec (Get-AgentQuickLock -ExitCode 0) -Machine @{ IssueView = @{ 7 = @{ state = 'CLOSED'; labels = @('agent-task') } } }
    Assert-Equal 0 ([int]$done.State.quickFailures) 'a finished issue resets it too'
    $quiet = Invoke-TestWatch -LockSpec (Get-AgentQuickLock -DurationSeconds 600)
    Assert-False $quiet.State.ContainsKey('quickFailures') -Message 'nothing is written when there was nothing to reset'
}

Test-Case 'back-off: only an unfinished, unmerged, open issue counts as a quick failure' {
    $self = Invoke-TestWatch -LockSpec (Get-AgentQuickLock) -Machine @{ IssueView = @{ 7 = @{ state = 'OPEN'; labels = @('agent-task', 'agent-blocked', 'needs:cory') } } }
    Assert-False $self.State.ContainsKey('pauseUntilUnix') -Message 'the agent blocked itself: that is an answer, not a startup failure'
    $merged = Invoke-TestWatch -LockSpec (Get-AgentQuickLock) -Machine @{
        IssueView = @{ 7 = @{ state = 'OPEN'; labels = @('agent-task') } }
        Prs = @{ 'agent/issue-7' = @(@{ number = 21; state = 'MERGED'; mergedAt = '2026-10-31T17:56:30Z'; url = 'u' }) }
    }
    Assert-False $merged.State.ContainsKey('pauseUntilUnix') -Message 'it merged something, so it did start'
    Assert-Match ((Get-AgentCall 'gh' '^issue edit 7 ')[0].Arguments -join ' ') '--add-label agent-blocked'
    $unknown = Invoke-TestWatch -LockSpec @{ Issue = 7; ProcessId = 3100; MinutesAgo = 3; ExitCode = 1 }
    Assert-False $unknown.State.ContainsKey('pauseUntilUnix') -Message 'no end time recorded, so the run length is unknown'
    $killed = Invoke-TestWatch -LockSpec @{ Issue = 7; ProcessId = 3100; MinutesAgo = 181; Alive = $true }
    Assert-False $killed.State.ContainsKey('pauseUntilUnix') -Message 'a run stopped for its time limit is not a quick failure'
}

Test-Case 'back-off: only a run counted on the day it started is taken off that day''s count, and never below zero' {
    $zero = Invoke-TestWatch -State @{ day = 20261031; runs = 0 } -LockSpec (Get-AgentQuickLock)
    Assert-Equal 0 $zero.State.runs
    $other = Invoke-TestWatch -State @{ day = 20261030; runs = 5 } -LockSpec (Get-AgentQuickLock)
    Assert-Equal 5 $other.State.runs 'a count from another day is left alone'
    $none = Invoke-TestWatch -LockSpec (Get-AgentQuickLock)
    Assert-False $none.State.ContainsKey('runs') -Message 'no count is invented'
}

# ---- text that goes into a public issue comment ---------------------------------------------------------------------------

Test-Case 'secrets: .env values that look secret are collected, short or ordinary ones are not' {
    $found = Get-CmAgentSecrets -EnvMap @{ POSTGRES_PASSWORD = 'pw-long-enough'; CM_TMDB_API_KEY = 'abcdef123'; CM_HEARTBEAT_URL = 'https://hc.example/x-uuid'; CM_API_PORT = '5480'; CM_TZ = 'UTC'; CM_AUTH0_WEB_CLIENT_SECRET = 'x' }
    Assert-True ($found -contains 'pw-long-enough')
    Assert-True ($found -contains 'abcdef123')
    Assert-True ($found -contains 'https://hc.example/x-uuid')
    Assert-False ($found -contains '5480')
    Assert-False ($found -contains 'UTC')
    Assert-False ($found -contains 'x') 'a one-character value would mask half the log'
}

Test-Case 'secrets: Hide-CmAgentText removes terminal colour codes and token shapes' {
    Initialize-Cm -Apply $false -Secrets @('s3cret-value')
    $text = Hide-CmAgentText "$([char]27)[31mred$([char]27)[0m s3cret-value github_pat_$('x' * 30) sk-ant-api03-abcdefghij1234 ghs_$('Z' * 30)"
    Assert-Equal 'red *** *** *** ***' $text
}

Test-Case 'secrets: a token in a comment body never reaches gh''s command line, only its standard input' {
    New-AgentMachine @{} | Out-Null
    Initialize-Cm -Apply $true -Secrets @('s3cret-value')
    $ctx = New-CmAgentContext -Root (Join-Path ([IO.Path]::GetTempPath()) 'cm-ops1-test-none') -Repo $script:AgentSlug -AgentDir $script:AgentDir
    Assert-True (Add-CmIssueComment -Ctx $ctx -Issue 7 -Body 'the key is s3cret-value') 'the comment was posted'
    $call = Get-AgentCall 'gh' '^issue comment 7 '
    Assert-Equal 'the key is ***' $call[0].InputText
    Assert-False ($call[0].Display.Contains('s3cret-value'))
}

# ---- the runner (the process the watcher starts) -----------------------------------------------------------------------------

function New-FakeAgentProcess {
    param([int]$ExitCode = 0, [bool]$Finishes = $true)
    $p = [pscustomobject]@{ Id = 99; ExitCode = $ExitCode; Finishes = $Finishes; WaitedMs = -1 }
    $p | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { param($ms) $this.WaitedMs = $ms; $this.Finishes }
    $p
}

Test-Case 'runner: claude gets the prompt file on stdin and its output is appended to the log; the exit code is recorded' {
    New-AgentMachine @{} | Out-Null
    $root = New-TestRoot
    try {
        $dir = Join-Path $root 'logs/agent'
        $log = Join-Path $dir 'issue-7-x.log'; $exitFile = Join-Path $dir 'issue-7-x.exit.json'; $prompt = Join-Path $dir 'issue-7-x.prompt.md'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $global:CmFakeProcess = New-FakeAgentProcess -ExitCode 3
        $global:CmStartProcess = {
            param($File, $Arguments, $WorkingDirectory)
            $global:CmCalls.Add([pscustomobject]@{ Command = 'start'; Arguments = @($Arguments); Env = @{}; Mutating = $true; Display = "$File $Arguments"; InputText = ''; File = $File; WorkingDirectory = $WorkingDirectory })
            $global:CmFakeProcess
        }
        $code = Invoke-CmAgentRunner -Claude 'C:\fake\claude.exe' -Model 'sonnet' -PromptFile $prompt -LogFile $log -ExitFile $exitFile -WorkDir $root -MaxMinutes 120 -Issue 7
        Assert-Equal 3 $code
        $start = Get-AgentStarts
        Assert-Equal 1 $start.Count
        Assert-Equal $root $start[0].WorkingDirectory
        Assert-Match $start[0].Arguments[0] '^/d /s /c ""C:\\fake\\claude\.exe" -p --permission-mode auto --model sonnet < "[^"]*issue-7-x\.prompt\.md" >> "[^"]*issue-7-x\.log" 2>&1"$'
        Assert-Equal 7200000 $global:CmFakeProcess.WaitedMs 'the limit is the process wait'
        $recorded = Get-Content -LiteralPath $exitFile -Raw | ConvertFrom-Json -AsHashtable
        Assert-Equal 3 $recorded.exitCode
        Assert-Equal $false $recorded.timedOut
        $text = Get-Content -LiteralPath $log -Raw
        Assert-Match $text 'issue #7 starting \(model sonnet, limit 120 minutes\)'
        Assert-Match $text 'ended with exit code 3'
        Assert-Equal 0 (Get-AgentCall 'taskkill' '.').Count
    }
    finally { Remove-TestRoot $root; $global:CmFakeProcess = $null }
}

Test-Case 'runner: a run that outlives its limit is killed with its tree and recorded as timed out' {
    New-AgentMachine @{} | Out-Null
    $root = New-TestRoot
    try {
        $dir = Join-Path $root 'logs/agent'
        $log = Join-Path $dir 'issue-7-x.log'; $exitFile = Join-Path $dir 'issue-7-x.exit.json'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $global:CmFakeProcess = New-FakeAgentProcess -Finishes $false
        $global:CmStartProcess = { param($File, $Arguments, $WorkingDirectory) $global:CmFakeProcess }
        Invoke-CmAgentRunner -Claude 'claude' -Model 'haiku' -PromptFile 'p' -LogFile $log -ExitFile $exitFile -WorkDir $root -MaxMinutes 1 -Issue 7 | Out-Null
        $kill = Get-AgentCall 'taskkill' '.'
        Assert-Equal '/PID 99 /T /F' ($kill[0].Arguments -join ' ')
        $recorded = Get-Content -LiteralPath $exitFile -Raw | ConvertFrom-Json -AsHashtable
        Assert-Equal $true $recorded.timedOut
        Assert-Match (Get-Content -LiteralPath $log -Raw) 'stopped after the 1 minute limit'
    }
    finally { Remove-TestRoot $root; $global:CmFakeProcess = $null }
}

Test-Case 'runner: the exit file is replaced atomically and holds nothing secret' {
    New-AgentMachine @{} | Out-Null
    $root = New-TestRoot
    try {
        $dir = Join-Path $root 'logs/agent'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $global:CmFakeProcess = New-FakeAgentProcess -ExitCode 0
        $global:CmStartProcess = { param($File, $Arguments, $WorkingDirectory) $global:CmFakeProcess }
        $exitFile = Join-Path $dir 'x.exit.json'
        Invoke-CmAgentRunner -Claude 'claude' -Model 'sonnet' -PromptFile 'p' -LogFile (Join-Path $dir 'x.log') -ExitFile $exitFile -WorkDir $root -MaxMinutes 5 -Issue 1 | Out-Null
        Assert-False (Test-Path -LiteralPath "$exitFile.tmp")
        Assert-Equal @('endedUnix', 'exitCode', 'timedOut') (@((Get-Content -LiteralPath $exitFile -Raw | ConvertFrom-Json -AsHashtable).Keys | Sort-Object))
    }
    finally { Remove-TestRoot $root; $global:CmFakeProcess = $null }
}

# ---- the installer -------------------------------------------------------------------------------------------------------------

$script:AgentInstall = Join-Path $script:AgentDir 'Install-AgentWatcher.ps1'

Test-Case 'install: without -Apply (or with -WhatIf) it prints the exact schtasks command and registers nothing' {
    foreach ($flags in @(@{}, @{ WhatIf = $true }, @{ Apply = $true; WhatIf = $true })) {
        New-AgentMachine @{} | Out-Null
        $global:CmCalls.Clear()
        $root = New-TestRoot
        try {
            $out = (& $script:AgentInstall -Root $root @flags 6>&1 | Out-String)
            Assert-Equal 0 $LASTEXITCODE
            Assert-Match $out '/Create /TN CollectionMaxxingAgent /SC MINUTE /MO 5 /TR'
            Assert-Match $out 'Watch-AgentIssues\.ps1'
            Assert-Equal 0 (Get-MutatingCalls).Count
            Assert-NoForbiddenCalls
            Assert-False (Test-Path (Join-Path $root 'logs')) 'no folders are created in a dry run'
        }
        finally { Remove-TestRoot $root }
    }
}

Test-Case 'install: -Apply makes one schtasks /Create for the current user, through conhost --headless, every 5 minutes' {
    New-AgentMachine @{} | Out-Null
    $root = New-TestRoot
    try {
        & $script:AgentInstall -Root $root -Apply 6>$null
        Assert-Equal 0 $LASTEXITCODE
        $m = Get-MutatingCalls
        Assert-Equal 1 $m.Count
        $args_ = @($m[0].Arguments)
        Assert-Equal '/Create /TN CollectionMaxxingAgent /SC MINUTE /MO 5' ($args_[0..6] -join ' ')
        Assert-True ($args_ -contains '/RL' -and $args_[[array]::IndexOf($args_, '/RL') + 1] -eq 'LIMITED') 'no elevation'
        Assert-False ($args_ -contains '/RU') 'no other account'
        $tr = $args_[[array]::IndexOf($args_, '/TR') + 1]
        Assert-Match $tr '^conhost\.exe --headless "[^"]*pwsh(\.exe)?" -NoProfile -ExecutionPolicy Bypass -File "[^"]*Watch-AgentIssues\.ps1" -Root "[^"]+" -Apply$'
        Assert-True (Test-Path (Join-Path $root 'logs/agent')) 'the log folders exist before the first run'
        Assert-True (Test-Path (Join-Path $root 'state'))
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'install: the task command fits schtasks'' 261 character limit with the real default paths' {
    $tr = Get-CmAgentTaskAction -Pwsh 'C:\Program Files\PowerShell\7\pwsh.exe' -Script 'C:\CollectionMaxxing\repo\deploy\agent\Watch-AgentIssues.ps1' -Root 'C:\CollectionMaxxing'
    Assert-True ($tr.Length -le 261) "the /TR text is $($tr.Length) characters"
}

Test-Case 'install: it will not replace an existing task, and refuses a task name that is not its own' {
    New-AgentMachine @{ TaskExists = $true } | Out-Null
    $root = New-TestRoot
    try {
        & $script:AgentInstall -Root $root -Apply 6>$null
        Assert-Equal 1 $LASTEXITCODE
        Assert-Equal 0 (Get-MutatingCalls).Count
        $global:CmCalls.Clear()
        & $script:AgentInstall -Root $root -TaskName 'CollectionMaxxingDeployer' -Apply 6>$null
        Assert-Equal 2 $LASTEXITCODE
        Assert-Equal 0 $global:CmCalls.Count 'the deployer''s task is never even queried'
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'uninstall: -WhatIf prints the delete; -Apply deletes only this task; a missing task is not an error' {
    New-AgentMachine @{ TaskExists = $true } | Out-Null
    $out = (& $script:AgentInstall -Uninstall -WhatIf 6>&1 | Out-String)
    Assert-Equal 0 $LASTEXITCODE
    Assert-Match $out '/Delete /TN CollectionMaxxingAgent /F'
    Assert-Equal 0 (Get-MutatingCalls).Count
    & $script:AgentInstall -Uninstall -Apply 6>$null
    $m = Get-MutatingCalls
    Assert-Equal 1 $m.Count
    Assert-Equal '/Delete /TN CollectionMaxxingAgent /F' ($m[0].Arguments -join ' ')
    New-AgentMachine @{ TaskExists = $false } | Out-Null
    $global:CmCalls.Clear()
    & $script:AgentInstall -Uninstall -Apply 6>$null
    Assert-Equal 0 $LASTEXITCODE
    Assert-Equal 0 (Get-MutatingCalls).Count
    & $script:AgentInstall -Uninstall -TaskName 'SomeoneElsesTask' -Apply 6>$null
    Assert-Equal 2 $LASTEXITCODE
}

# ---- the written word -------------------------------------------------------------------------------------------------------------

Test-Case 'docs: every CM_AGENT_ setting the watcher reads is in .env.example and in the README' {
    $code = Get-Content -LiteralPath (Join-Path $script:AgentDir 'Cm.Agent.ps1') -Raw
    $names = @([regex]::Matches($code, 'CM_AGENT_[A-Z_]+') | ForEach-Object { $_.Value } | Sort-Object -Unique)
    Assert-True ($names.Count -ge 4) "found only: $($names -join ' ')"
    $example = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../.env.example') -Raw
    $readme = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../README.md') -Raw
    foreach ($n in $names) {
        Assert-True ($example.Contains($n)) "$n is missing from .env.example"
        Assert-True ($readme.Contains($n)) "$n is missing from the README"
    }
}

Test-Case 'docs: the README names the labels and the commands of the agent service' {
    $readme = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../README.md') -Raw
    foreach ($needle in 'agent-task', 'agent-working', 'agent-review', 'agent-blocked', 'needs:cory', 'Install-AgentWatcher.ps1', 'logs\agent', 'CollectionMaxxingAgent') {
        Assert-True ($readme.Contains($needle)) "the README should mention $needle"
    }
}

Test-Case 'retired: the old Muse watcher script is gone' {
    Assert-False (Test-Path -LiteralPath (Join-Path $PSScriptRoot '../../scripts/watch-muse-issues.ps1')) 'scripts/watch-muse-issues.ps1 should be deleted'
}
