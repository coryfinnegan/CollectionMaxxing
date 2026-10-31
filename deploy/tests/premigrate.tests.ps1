# Pre-migrate database dump tests: before a deploy runs the migrate step, the deployer takes a named dump of the live
# database into <CM_BACKUP_DIR>/pre-migrate; if that fails, nothing is migrated. Dot-sourced by Run-Tests.ps1 after
# ops2.tests.ps1 (it uses the helpers from deployer.tests.ps1 and ops1b.tests.ps1). Nothing here touches the real
# Docker: the dump goes to the recorder in TestKit.ps1; the one test that runs the dump script uses stub pg tools.

$script:MigrationChange = @('src/CollectionMaxxing.Infrastructure/Migrations/20261030120000_AddShelf.cs', 'src/CollectionMaxxing.Infrastructure/Migrations/AppDbContextModelSnapshot.cs', 'src/CollectionMaxxing.Api/Program.cs')

function Get-DumpCalls { @(Get-Calls 'docker' '*--entrypoint sh backup*') }

function New-BackupDirEnvText {
    param([string]$BackupDir)
    @('POSTGRES_PASSWORD=test-only-password', "CM_BACKUP_DIR=$BackupDir", 'CM_API_PORT=5480', 'CM_DASHBOARD_PORT=18888',
        'CM_HEARTBEAT_URL=https://hc.example/test-uuid', 'CM_SECRETS_DIR=/tmp/cm-test-secrets',
        'CM_PUBLIC_ORIGIN=https://pc.tailnet.ts.net', 'CM_AUTH0_DOMAIN=test.us.auth0.com', 'CM_AUTH0_WEB_CLIENT_ID=test-web', 'CM_AUTH0_WEB_CLIENT_SECRET=test-secret', 'CM_AUTH0_MANAGEMENT_CLIENT_ID=test-mgmt', 'CM_AUTH0_MANAGEMENT_CLIENT_SECRET=test-secret')
}

function New-TestBackupDir {
    <# A throwaway backup folder with old pre-migrate dumps (stamped in 2020, so the one a deploy makes now is the newest) and unrelated files. #>
    param([int]$OldDumps = 8)
    $dir = Join-Path ([IO.Path]::GetTempPath()) ("cm-ops1-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path (Join-Path $dir 'pre-migrate'), (Join-Path $dir 'db'), (Join-Path $dir 'files') | Out-Null
    foreach ($i in 1..$OldDumps) {
        Set-Content -LiteralPath (Join-Path $dir ('pre-migrate/pre-migrate-{0}-2020010{1}-120000.dump' -f ('{0:x12}' -f $i), $i)) -Value 'old dump'
    }
    Set-Content -LiteralPath (Join-Path $dir 'pre-migrate/notes.txt') -Value 'mine'
    Set-Content -LiteralPath (Join-Path $dir 'pre-migrate/pre-migrate-abc.dump') -Value 'not ours'
    Set-Content -LiteralPath (Join-Path $dir 'pre-migrate/pre-migrate-000000000001-20200101-120000.dump.partial') -Value 'half'
    Set-Content -LiteralPath (Join-Path $dir 'db/collectionmaxxing-20200101-030000.dump') -Value 'nightly'
    Set-Content -LiteralPath (Join-Path $dir 'files/files-20200101-030000.tar.gz') -Value 'nightly'
    $dir
}

function Get-TestBash {
    $candidates = @()
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'Git/bin/bash.exe'), (Join-Path $env:ProgramFiles 'Git/usr/bin/bash.exe') }
    $candidates += '/bin/bash', '/usr/bin/bash'
    $found = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    if ($found) { return $found }
    $null
}

# ---- whether migrations are pending -------------------------------------------------------------------------------------

Test-Case 'pre-migrate: a changed file under a Migrations folder means migrations may be pending' {
    $root = New-TestRoot
    try {
        New-FakeMachine @{ ChangedFiles = $script:MigrationChange } | Out-Null
        $ctx = New-TestContext -Root $root
        $p = Test-CmMigrationsPending -Ctx $ctx -From $script:Sha1 -To $script:Sha2
        Assert-True $p.Pending
        Assert-Match $p.Reason 'migration'
        $diff = Get-Calls 'git' '*diff*'
        Assert-Equal 1 $diff.Count
        Assert-False $diff[0].Mutating 'git diff only reads'
        Assert-Match $diff[0].Display "diff --name-only .*$($script:Sha1) $($script:Sha2)"
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'pre-migrate: no changed file under a Migrations folder means nothing is pending' {
    $root = New-TestRoot
    try {
        New-FakeMachine @{ ChangedFiles = @('src/CollectionMaxxing.Api/Program.cs', 'app/src/Migrations.md', 'docs/Migrationsplan/x.md', 'deploy/deployer/Cm.Deploy.ps1') } | Out-Null
        $ctx = New-TestContext -Root $root
        Assert-False (Test-CmMigrationsPending -Ctx $ctx -From $script:Sha1 -To $script:Sha2).Pending
        New-FakeMachine @{ ChangedFiles = @() } | Out-Null
        Assert-False (Test-CmMigrationsPending -Ctx $ctx -From $script:Sha1 -To $script:Sha2).Pending 'no changes at all'
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'pre-migrate: when git cannot tell, or there is no deployed commit to compare with, migrations count as pending' {
    $root = New-TestRoot
    try {
        New-FakeMachine @{ DiffFails = $true } | Out-Null
        $ctx = New-TestContext -Root $root
        $p = Test-CmMigrationsPending -Ctx $ctx -From $script:Sha1 -To $script:Sha2
        Assert-True $p.Pending
        Assert-Match $p.Reason 'could not tell'
        New-FakeMachine @{} | Out-Null
        $global:CmCalls.Clear()
        $p = Test-CmMigrationsPending -Ctx $ctx -From '' -To $script:Sha2
        Assert-True $p.Pending 'a first deploy has nothing to compare with'
        Assert-Equal 0 (Get-Calls 'git' '*diff*').Count 'and git is not even asked'
        $p = Test-CmMigrationsPending -Ctx $ctx -From 'main' -To $script:Sha2
        Assert-True $p.Pending 'a deployed commit that is not a full hash cannot be compared'
    }
    finally { Remove-TestRoot $root }
}

# ---- the dump in a deploy -----------------------------------------------------------------------------------------------

Test-Case 'pre-migrate: a release that changes migration files dumps the database first, then migrates, then starts' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ ChangedFiles = $script:MigrationChange }
    Assert-Equal 'deployed' $r.Result.Outcome
    $verify = Get-CallIndex '*attestation verify*'
    $dump = Get-CallIndex '*--entrypoint sh backup*'
    $migrate = Get-CallIndex '*run --rm migrate*'
    $up = Get-CallIndex '*up -d*'
    Assert-True ($verify -ge 0 -and $verify -lt $dump -and $dump -lt $migrate -and $migrate -lt $up) "verify=$verify dump=$dump migrate=$migrate up=$up"
    Assert-Equal 1 (Get-DumpCalls).Count
    $d = (Get-DumpCalls)[0]
    Assert-True $d.Mutating 'writing a dump changes the machine'
    Assert-Match $d.Display ' -p collectionmaxxing run --rm '
    Assert-Match $d.Display ' --no-deps '
    Assert-Match $d.Display 'pg_dump -Fc'
    Assert-Match $d.Display "/backups/pre-migrate/pre-migrate-$($script:Sha2.Substring(0, 12))-\d{8}-\d{6}\.dump"
    Assert-Equal $script:Sha1 $d.Env['CM_TAG'] 'taken with the backup image that is running now'
    Assert-False ($d.Display -match 'test-only-password') 'no secret on the command line'
    Assert-Match $r.Log 'pre-migrate dump'
}

Test-Case 'pre-migrate: a dump that fails stops the deploy before anything is migrated, and the running version stays' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ ChangedFiles = $script:MigrationChange; DumpExit = 1 }
    Assert-Equal 'dump-failed' $r.Result.Outcome
    Assert-Equal 1 $r.Result.ExitCode
    Assert-Equal 0 (Get-Calls 'docker' '*run --rm migrate*').Count 'nothing is migrated'
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count 'nothing is restarted'
    Assert-Equal $script:Sha1 $r.State.current.sha 'the current version is kept'
    Assert-False $r.State.ContainsKey('failedSha') 'a full disk is not the release''s fault, so the commit is not blamed'
    Assert-Match $r.Log 'ALERT deploy dump-failed'
    Assert-Match $r.Result.Reason 'not migrated'
    Assert-Match $r.Result.Reason $script:Sha2.Substring(0, 12)
    Assert-Equal 1 (Get-AlertCalls).Count
}

Test-Case 'pre-migrate: after a failed dump the next poll tries the same commit again' {
    $first = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ ChangedFiles = $script:MigrationChange; DumpExit = 1 }
    Assert-Equal 'dump-failed' $first.Result.Outcome
    $second = Invoke-TestDeploy -State $first.State -Machine @{ ChangedFiles = $script:MigrationChange }
    Assert-Equal 'deployed' $second.Result.Outcome
    Assert-Equal $script:Sha2 $second.State.current.sha
}

Test-Case 'pre-migrate: a release that changes no migration file takes no dump, and still migrates' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ ChangedFiles = @('src/CollectionMaxxing.Api/Program.cs') }
    Assert-Equal 'deployed' $r.Result.Outcome
    Assert-Equal 0 (Get-DumpCalls).Count
    Assert-Equal 1 (Get-Calls 'docker' '*run --rm migrate*').Count 'the migrate step is unchanged'
    Assert-Match $r.Log 'no migration file changed'
}

Test-Case 'pre-migrate: when git cannot say whether migrations are pending, the database is dumped anyway, and the log says so' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ DiffFails = $true }
    Assert-Equal 'deployed' $r.Result.Outcome
    Assert-Equal 1 (Get-DumpCalls).Count
    Assert-Match $r.Log 'could not tell'
}

Test-Case 'pre-migrate: the very first deploy dumps too (a lost state file must not hide a real database), with the new version and the dependencies started' {
    $r = Invoke-TestDeploy
    Assert-Equal 'deployed' $r.Result.Outcome
    $d = Get-DumpCalls
    Assert-Equal 1 $d.Count
    Assert-Equal $script:Sha2 $d[0].Env['CM_TAG'] 'there is no running version yet'
    Assert-False ($d[0].Display -match '--no-deps') 'postgres may need starting'
}

Test-Case 'pre-migrate: the dump uses the compose file of the running version, the migration the new one' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ ChangedFiles = $script:MigrationChange; ComposeAt = @{ $script:Sha2 = 'new: file'; $script:Sha1 = 'old: file' } }
    Assert-Equal 'deployed' $r.Result.Outcome
    Assert-Equal 'old: file' (Get-DumpCalls)[0].ComposeText
    Assert-Equal 'new: file' (Get-Calls 'docker' '*run --rm migrate*')[0].ComposeText
}

Test-Case 'pre-migrate: the dry run makes no dump and the plan names it' {
    $r = Invoke-TestDeploy -DryRun -State (Get-OldGoodState)
    Assert-Equal 'dry-run' $r.Result.Outcome
    Assert-Equal 0 (Get-DumpCalls).Count
    Assert-Equal 0 (Get-MutatingCalls).Count
    $root = New-TestRoot
    try {
        $plan = (Get-CmPlan -Ctx (New-TestContext -Root $root)) -join "`n"
        Assert-Match $plan 'pre-migrate'
        Assert-Match $plan 'diff --name-only'
        Assert-Match $plan 'pg_dump -Fc'
    }
    finally { Remove-TestRoot $root }
}

# ---- keeping only the last 5 --------------------------------------------------------------------------------------------

Test-Case 'pre-migrate retention: the last 5 dumps stay, older ones go, and nothing else in the folder is looked at' {
    $names = @(
        'pre-migrate-000000000003-20200103-120000.dump', 'pre-migrate-000000000001-20200101-120000.dump', 'pre-migrate-000000000008-20200108-120000.dump',
        'pre-migrate-000000000002-20200102-120000.dump', 'pre-migrate-000000000006-20200106-120000.dump', 'pre-migrate-000000000004-20200104-120000.dump',
        'pre-migrate-000000000005-20200105-120000.dump', 'pre-migrate-000000000007-20200107-120000.dump',
        'notes.txt', 'pre-migrate-abc.dump', 'pre-migrate-000000000001-20200101-120000.dump.partial', 'collectionmaxxing-20200101-030000.dump',
        'PRE-MIGRATE-000000000001-20200101-120000.DUMP', 'pre-migrate-00000000000g-20200101-120000.dump')
    $gone = @(Select-CmPreMigrateDumpsToRemove -Names $names -Keep 5)
    Assert-Equal 'pre-migrate-000000000001-20200101-120000.dump pre-migrate-000000000002-20200102-120000.dump pre-migrate-000000000003-20200103-120000.dump' (($gone | Sort-Object) -join ' ')
    Assert-Equal 0 @(Select-CmPreMigrateDumpsToRemove -Names $names[0..3] -Keep 5).Count 'fewer than 5: nothing goes'
    Assert-Equal 0 @(Select-CmPreMigrateDumpsToRemove -Names @() -Keep 5).Count
    Assert-Equal 0 @(Select-CmPreMigrateDumpsToRemove -Names $names -Keep 0).Count 'a keep count of 0 is refused, not honoured: the newest always stays'
}

Test-Case 'pre-migrate retention: a good dump prunes the folder to the newest 5 and touches nothing else' {
    $bk = New-TestBackupDir -OldDumps 8
    try {
        $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ ChangedFiles = $script:MigrationChange; DumpWritesTo = $bk } -EnvText (New-BackupDirEnvText $bk)
        Assert-Equal 'deployed' $r.Result.Outcome
        $left = @(Get-ChildItem -LiteralPath (Join-Path $bk 'pre-migrate') -Filter 'pre-migrate-*.dump' | Where-Object { $_.Name -match '^pre-migrate-[0-9a-f]{12}-\d{8}-\d{6}\.dump$' } | Sort-Object Name | ForEach-Object Name)
        Assert-Equal 5 $left.Count ($left -join ' ')
        Assert-Match $left[-1] "^pre-migrate-$($script:Sha2.Substring(0, 12))-"
        Assert-Equal 'pre-migrate-000000000005-20200105-120000.dump' $left[0] 'the 4 newest old ones and the new dump'
        foreach ($keep in 'notes.txt', 'pre-migrate-abc.dump', 'pre-migrate-000000000001-20200101-120000.dump.partial') {
            Assert-True (Test-Path -LiteralPath (Join-Path $bk "pre-migrate/$keep")) "$keep is not ours to remove"
        }
        Assert-True (Test-Path -LiteralPath (Join-Path $bk 'db/collectionmaxxing-20200101-030000.dump')) 'the nightly dump is untouched'
        Assert-True (Test-Path -LiteralPath (Join-Path $bk 'files/files-20200101-030000.tar.gz')) 'the nightly files archive is untouched'
    }
    finally { Remove-TestRoot $bk }
}

Test-Case 'pre-migrate retention: a dump that failed removes nothing' {
    $bk = New-TestBackupDir -OldDumps 8
    try {
        $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ ChangedFiles = $script:MigrationChange; DumpExit = 1; DumpWritesTo = $bk } -EnvText (New-BackupDirEnvText $bk)
        Assert-Equal 'dump-failed' $r.Result.Outcome
        Assert-Equal 8 @(Get-ChildItem -LiteralPath (Join-Path $bk 'pre-migrate') -Filter 'pre-migrate-*.dump' | Where-Object { $_.Name -match '^pre-migrate-[0-9a-f]{12}-' }).Count
    }
    finally { Remove-TestRoot $bk }
}

Test-Case 'pre-migrate retention: a backup folder that is not there yet is fine, and is not created by the deployer' {
    $missing = Join-Path ([IO.Path]::GetTempPath()) ("cm-ops1-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ ChangedFiles = $script:MigrationChange } -EnvText (New-BackupDirEnvText $missing)
    Assert-Equal 'deployed' $r.Result.Outcome
    Assert-False (Test-Path -LiteralPath $missing) 'the dump itself creates the folder inside the container; the host side only ever deletes'
}

# ---- the dump script itself ---------------------------------------------------------------------------------------------

function ConvertTo-TestPosixPath {
    <# Git for Windows' bash wants /c/Users/... where PowerShell has C:\Users\...; on Linux the path is already right. #>
    param([string]$Path)
    $p = $Path -replace '\\', '/'
    if ($p -match '^([A-Za-z]):(.*)$') { $p = '/' + $Matches[1].ToLowerInvariant() + $Matches[2] }
    $p
}

function Invoke-DumpScriptWithStubs {
    <#
      Runs the real dump script under bash in POSIX mode, with pg_dump and pg_restore replaced by shell functions (the
      bodies are given without quote characters). Returns the exit code, the output and what is left in the folder.
    #>
    param([string]$PgDump, [string]$PgRestore)
    $bash = Get-TestBash
    $work = Join-Path ([IO.Path]::GetTempPath()) ("cm-ops1-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    try {
        $out = Join-Path $work 'pre-migrate'
        New-Item -ItemType Directory -Force -Path $work | Out-Null
        $script = Get-CmPreMigrateScript -Directory (ConvertTo-TestPosixPath $out) -FileName 'pre-migrate-aaaaaaaaaaaa-20261030-120000.dump'
        $lines = @(& $bash --posix -c "pg_dump() { $PgDump; }; pg_restore() { $PgRestore; }; $script" 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
        [pscustomobject]@{ ExitCode = $code; Output = $lines; Files = @(if (Test-Path -LiteralPath $out) { Get-ChildItem -LiteralPath $out | ForEach-Object Name }) }
    }
    finally { Remove-TestRoot $work }
}

# pg_dump is called as: pg_dump -Fc -f <target>. These stubs find the target the way the real tool would.
$script:StubDumpFindsTarget = 'while [ $# -gt 0 ]; do if [ $1 = -f ]; then f=$2; fi; shift; done'

Test-Case 'pre-migrate script: a good dump ends up under its final name and nothing else is left' {
    if (-not (Get-TestBash)) { return }
    $r = Invoke-DumpScriptWithStubs -PgDump "$($script:StubDumpFindsTarget); echo data > `$f" -PgRestore 'return 0'
    Assert-Equal 0 $r.ExitCode ($r.Output -join ' | ')
    Assert-Equal 'pre-migrate-aaaaaaaaaaaa-20261030-120000.dump' ($r.Files -join ' ')
    Assert-Match ($r.Output -join ' ') 'pre-migrate/pre-migrate-aaaaaaaaaaaa-20261030-120000\.dump'
}

Test-Case 'pre-migrate script: a dump that errors, comes out empty or does not restore-list fails and leaves no file behind' {
    if (-not (Get-TestBash)) { return }
    $find = $script:StubDumpFindsTarget
    $cases = @(
        @{ Name = 'pg_dump exits 1'; Dump = 'return 1'; Restore = 'return 0' },
        @{ Name = 'pg_dump writes nothing'; Dump = "$find; : > `$f"; Restore = 'return 0' },
        @{ Name = 'pg_dump writes then fails'; Dump = "$find; echo half > `$f; return 1"; Restore = 'return 0' },
        @{ Name = 'pg_restore --list rejects it'; Dump = "$find; echo data > `$f"; Restore = 'return 1' })
    foreach ($c in $cases) {
        $r = Invoke-DumpScriptWithStubs -PgDump $c.Dump -PgRestore $c.Restore
        Assert-True ($r.ExitCode -ne 0) "$($c.Name): exit code must not be 0"
        Assert-Equal 0 $r.Files.Count "$($c.Name): left behind $($r.Files -join ' ')"
    }
}

Test-Case 'pre-migrate script: it has no quote characters, so it reaches the container as typed, and takes names only from the deployer' {
    $script = Get-CmPreMigrateScript -Directory '/backups/pre-migrate' -FileName 'pre-migrate-aaaaaaaaaaaa-20261030-120000.dump'
    Assert-False ($script -match '["'']') 'quotes get mangled by native argument passing on Windows'
    Assert-Match $script 'pg_dump -Fc -f /backups/pre-migrate/pre-migrate-aaaaaaaaaaaa-20261030-120000\.dump\.partial'
    foreach ($bad in 'x; rm -rf /', 'a b.dump', '../x.dump', 'pre-migrate-aaaaaaaaaaaa-20261030-120000.dump; id') {
        $threw = $false
        try { Get-CmPreMigrateScript -Directory '/backups/pre-migrate' -FileName $bad | Out-Null } catch { $threw = $true }
        Assert-True $threw "[$bad] must be refused"
    }
}

Test-Case 'pre-migrate: the dump name carries the short commit and a UTC time stamp' {
    $name = Get-CmPreMigrateDumpName -Sha $script:Sha2 -Utc ([DateTime]::new(2026, 10, 30, 21, 5, 9, [DateTimeKind]::Utc))
    Assert-Equal "pre-migrate-$($script:Sha2.Substring(0, 12))-20261030-210509.dump" $name
    $threw = $false
    try { Get-CmPreMigrateDumpName -Sha 'main' -Utc ([DateTime]::UtcNow) | Out-Null } catch { $threw = $true }
    Assert-True $threw 'only a full commit hash makes a name'
}
