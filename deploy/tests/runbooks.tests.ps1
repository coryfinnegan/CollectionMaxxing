# OPS-2b tests: the runbooks in deploy/runbooks and the drill log template in deploy/drills are safe to paste, point at
# files that exist, have the same shape, and are all reachable from the index. Also the live-path guard of the restore
# drill's PowerShell wrapper. Dot-sourced by Run-Tests.ps1. Documents only: nothing here starts Docker or touches the PC.

$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:RunbookDir = Join-Path $script:RepoRoot 'deploy/runbooks'
$script:DrillsReadme = Join-Path $script:RepoRoot 'deploy/drills/README.md'

function Get-RunbookFiles { @(Get-ChildItem -LiteralPath $script:RunbookDir -Filter '*.md' -ErrorAction SilentlyContinue | Where-Object Name -NE 'README.md') }
function Get-DocsToScan {
    $all = @(Get-ChildItem -LiteralPath $script:RunbookDir -Filter '*.md' -ErrorAction SilentlyContinue)
    if (Test-Path -LiteralPath $script:DrillsReadme) { $all += Get-Item -LiteralPath $script:DrillsReadme }
    $all
}

Test-Case 'runbooks: the folder has an index and the eight runbooks the brief names' {
    Assert-True (Test-Path -LiteralPath (Join-Path $script:RunbookDir 'README.md')) 'deploy/runbooks/README.md is missing'
    $names = (Get-RunbookFiles).Name
    foreach ($want in 'reboot-or-docker-off.md', 'update-and-rollback.md', 'missed-backup-or-full-disk.md', 'lost-phone-or-remove-member.md',
        'rotate-signing-keys.md', 'tailscale-and-https.md', 'iphone-app-testflight.md', 'v1-release-and-week-of-use.md') {
        Assert-True ($names -contains $want) "missing runbook $want"
    }
}

Test-Case 'runbooks: every file or script path a runbook mentions exists in the repo' {
    $missing = [System.Collections.Generic.List[string]]::new()
    foreach ($f in Get-DocsToScan) {
        $text = (Get-Content -LiteralPath $f.FullName -Raw) -replace 'C:\\CollectionMaxxing\\repo\\', ''
        foreach ($m in [regex]::Matches($text, '(?<![\w/\\:.<-])((?:deploy|scripts|docs|contracts|app|tests|\.github)[\\/][^\s`''"|;,()]+)')) {
            $p = $m.Groups[1].Value
            if ($p -match '[<>*{}$]') { continue }
            $p = ($p -split '#')[0].TrimEnd('.', ':', '`')
            $rel = $p -replace '\\', '/'
            if (-not (Test-Path -LiteralPath (Join-Path $script:RepoRoot $rel))) { $missing.Add("$($f.Name): $p") }
        }
    }
    Assert-Equal 0 $missing.Count "paths that do not exist: $($missing -join '; ')"
}

Test-Case 'runbooks: no forbidden command, real-looking host, email address or long secret' {
    $problems = [System.Collections.Generic.List[string]]::new()
    $forbidden = @(
        @{ Why = 'docker system/volume/image/network prune'; Rx = '(?i)\bdocker\s+(system|volume|image|container|network)\s+prune' },
        @{ Why = 'docker rm / rmi'; Rx = '(?i)\bdocker\s+(container\s+)?(rm|rmi)\b' },
        @{ Why = 'compose down (use stop)'; Rx = '(?i)\bcompose\b[^\r\n]*\bdown\b' },
        @{ Why = 'a -v / --volumes flag that deletes volumes'; Rx = '(?i)\bdown\s+(-\w*v\w*|--volumes)\b' },
        @{ Why = 'a recursive delete'; Rx = '(?i)(\brm\s+-\w*r|Remove-Item|\brmdir\b|\bdel\s+/)' },
        @{ Why = 'a real *.ts.net name (only pc.tailnet.ts.net is allowed)'; Rx = '(?i)(?<![\w.-])(?!pc\.tailnet\.ts\.net\b)[a-z0-9][a-z0-9-]*(\.[a-z0-9-]+)*\.ts\.net\b' },
        @{ Why = 'an email address'; Rx = '[A-Za-z0-9._+-]+@[A-Za-z0-9-]+\.[A-Za-z]{2,}' },
        @{ Why = 'a long hex string (32+)'; Rx = '\b[0-9a-fA-F]{32,}\b' },
        @{ Why = 'a long base64-looking string (40+)'; Rx = '[A-Za-z0-9+]{40,}={0,2}' }
    )
    foreach ($f in Get-DocsToScan) {
        $text = Get-Content -LiteralPath $f.FullName -Raw
        foreach ($rule in $forbidden) {
            $m = [regex]::Match($text, $rule.Rx)
            if ($m.Success) { $problems.Add("$($f.Name): $($rule.Why): [$($m.Value)]") }
        }
    }
    Assert-Equal 0 $problems.Count ($problems -join '; ')
}

Test-Case 'runbooks: each has a title, When to use this, numbered steps and How to know it worked' {
    $files = Get-RunbookFiles
    Assert-True ($files.Count -ge 8) 'the runbooks are missing'
    $problems = [System.Collections.Generic.List[string]]::new()
    foreach ($f in $files) {
        $lines = Get-Content -LiteralPath $f.FullName
        if (-not ($lines | Select-Object -First 1) -or ($lines | Select-Object -First 1) -notmatch '^# \S') { $problems.Add("$($f.Name): no title on line 1") }
        if (-not ($lines -match '^## When to use this\s*$')) { $problems.Add("$($f.Name): no '## When to use this'") }
        if (-not ($lines -match '^## How to know it worked\s*$')) { $problems.Add("$($f.Name): no '## How to know it worked'") }
        if (@($lines -match '^\d+\. \S').Count -lt 3) { $problems.Add("$($f.Name): fewer than 3 numbered steps") }
    }
    Assert-Equal 0 $problems.Count ($problems -join '; ')
}

Test-Case 'runbooks: the index links every runbook and every link in it resolves' {
    $index = Join-Path $script:RunbookDir 'README.md'
    $text = Get-Content -LiteralPath $index -Raw
    foreach ($f in Get-RunbookFiles) { Assert-True ($text -match [regex]::Escape("]($($f.Name))")) "the index does not link $($f.Name)" }
    foreach ($m in [regex]::Matches($text, '\]\(([^)#\s]+)(#[^)]*)?\)')) {
        $target = $m.Groups[1].Value
        if ($target -match '^https?:') { continue }
        Assert-True (Test-Path -LiteralPath (Join-Path $script:RunbookDir $target)) "the index links $target, which does not exist"
    }
}

Test-Case 'runbooks: the v1.0 checklist gathers every pending hands-on owner step' {
    $text = Get-Content -LiteralPath (Join-Path $script:RunbookDir 'v1-release-and-week-of-use.md') -Raw
    foreach ($want in 'ios-build.yml', 'VPN On Demand', 'failing test', 'Run evals', 'golden', 'restore-drill.ps1', '-ConfirmLive',
        'Run-Perf.ps1', '-Profile gaming', '-Profile full', 'ios-checklist.md', 'VoiceOver', 'Large', 'Reduce Motion',
        'https://pc.tailnet.ts.net/', 'passkey', 'TrustedProxies', 'TestFlight', 'Run-ZapBaseline.ps1') {
        Assert-True ($text.Contains($want)) "the v1.0 checklist does not mention [$want]"
    }
    Assert-True (@([regex]::Matches($text, '(?m)^\s*- \[ \] ')).Count -ge 10) 'expected tick boxes (- [ ])'
}

Test-Case 'runbooks: nothing in them promises or sets up an off-site copy (owner decision 2026-10-17)' {
    foreach ($f in Get-RunbookFiles) {
        $text = Get-Content -LiteralPath $f.FullName -Raw
        Assert-False ($text -match '(?i)\brclone\b') "$($f.Name) mentions rclone"
    }
}

Test-Case 'drills: the log template has the columns and no personal data' {
    Assert-True (Test-Path -LiteralPath $script:DrillsReadme) 'deploy/drills/README.md is missing'
    $text = Get-Content -LiteralPath $script:DrillsReadme -Raw
    foreach ($want in 'Date', 'Backup age', 'Counts', 'PASS', 'FAIL', 'Minutes') { Assert-True ($text.Contains($want)) "the drill log template lacks [$want]" }
}

# ---- the drill's PowerShell wrapper refuses live paths ----------------------------------------------------------------

function Invoke-DrillWrapper {
    param([string[]]$Arguments)
    $pwsh = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
    if (-not $pwsh) { $pwsh = Join-Path $PSHOME 'pwsh.exe' }
    $out = & $pwsh -NoProfile -File (Join-Path $script:RepoRoot 'scripts/restore-drill.ps1') @Arguments 2>&1
    [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($out | Out-String) }
}

Test-Case 'restore-drill.ps1: a path inside -Root is refused with exit 3 unless -ConfirmLive is given' {
    $root = Join-Path ([IO.Path]::GetTempPath()) "cm-ops2b-root-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    $r = Invoke-DrillWrapper @('-Root', $root, '-Path', (Join-Path $root 'backups'))
    Assert-Equal 3 $r.Code $r.Text
    Assert-Match $r.Text 'REFUSED'
    $r2 = Invoke-DrillWrapper @('-Root', $root, '-Path', (Join-Path $root 'backups'), '-ConfirmLive')
    Assert-Equal 2 $r2.Code "with -ConfirmLive it gets past the guard and fails on the missing folder: $($r2.Text)"
}

Test-Case 'restore-drill.ps1: the CM_BACKUP_DIR in .env counts as live, and so does the default path' {
    $root = Join-Path ([IO.Path]::GetTempPath()) "cm-ops2b-root-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    try {
        $bk = Join-Path ([IO.Path]::GetTempPath()) "cm-ops2b-bk-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        Set-Content -LiteralPath (Join-Path $root '.env') -Value "CM_BACKUP_DIR=$bk"
        Assert-Equal 3 (Invoke-DrillWrapper @('-Root', $root)).Code 'no -Path: the .env backup folder is live'
        Assert-Equal 3 (Invoke-DrillWrapper @('-Root', $root, '-Path', (Join-Path $bk 'db'))).Code 'an explicit path inside it is live too'
        Assert-Equal 2 (Invoke-DrillWrapper @('-Root', $root, '-Path', (Join-Path ([IO.Path]::GetTempPath()) 'cm-ops2b-elsewhere'))).Code 'a temp folder elsewhere is not refused'
    }
    finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}
