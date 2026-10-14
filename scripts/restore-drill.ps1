<#
.SYNOPSIS
  The restore drill for Windows: restores the newest backup into a throwaway Postgres container and compares it with
  the manifest written at backup time. Prints PASS or FAIL with the numbers. Runbook: deploy/RESTORE.md.

.DESCRIPTION
  This is a thin wrapper: the logic lives in restore-drill.sh (so it is one thing to test), which needs bash (Git for
  Windows has it) and a running Docker. The drill starts one container with no network, no ports and no volumes,
  named cm-drill-*, and removes it afterwards. It never reads or writes the live database or its volume.
  Exit codes: 0 PASS, 1 FAIL, 2 the drill could not run.

.PARAMETER Path
  A backup folder (the one that holds db\ and files\), or one .dump file. Default: CM_BACKUP_DIR from <Root>\.env,
  and the newest dump in it.

.EXAMPLE
  pwsh -NoProfile -File scripts\restore-drill.ps1
  pwsh -NoProfile -File scripts\restore-drill.ps1 -Path F:\Backups\CollectionMaxxing
#>
[CmdletBinding()]
param(
    [string]$Path = '',
    [string]$Root = 'C:\CollectionMaxxing'
)
$ErrorActionPreference = 'Stop'

if (-not $Path) {
    $envFile = Join-Path $Root '.env'
    if (Test-Path -LiteralPath $envFile) {
        $line = Get-Content -LiteralPath $envFile | Where-Object { $_ -match '^\s*CM_BACKUP_DIR\s*=' } | Select-Object -Last 1
        if ($line) { $Path = ($line -replace '^\s*CM_BACKUP_DIR\s*=\s*', '').Trim().Trim('"').Trim("'") }
    }
}
if (-not $Path) {
    Write-Host "restore drill: give -Path <backup folder or .dump>, or set CM_BACKUP_DIR in $Root\.env"
    exit 2
}

function Find-Bash {
    $candidates = @()
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'Git/bin/bash.exe'), (Join-Path $env:ProgramFiles 'Git/usr/bin/bash.exe') }
    $candidates += '/bin/bash', '/usr/bin/bash'
    $found = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    if ($found) { return $found }
    # Never pick Windows' own bash.exe (System32): that starts WSL.
    $cmd = Get-Command bash -ErrorAction SilentlyContinue | Where-Object { $_.Source -notmatch '\\Windows\\System32\\' } | Select-Object -First 1
    if ($cmd) { return $cmd.Source }
    $null
}

$bash = Find-Bash
if (-not $bash) {
    Write-Host 'restore drill: bash was not found (install Git for Windows, or run scripts/restore-drill.sh on Linux)'
    exit 2
}
$script = (Join-Path $PSScriptRoot 'restore-drill.sh') -replace '\\', '/'
& $bash $script ($Path -replace '\\', '/')
exit $LASTEXITCODE
