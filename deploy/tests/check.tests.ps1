# Tests for the local gate script (scripts/check.ps1): the format phase runs `dotnet format` per project and
# must survive dotnet-format's flaky 'Warnings were encountered while loading the workspace' stderr line (exit 0),
# including under Windows PowerShell 5.1, where merged native stderr throws under $ErrorActionPreference Stop (#398).
# Each case runs the real script in a child process with a fake dotnet first on PATH, under every engine available
# (pwsh always; powershell.exe where it exists, which is the #398 regression leg). Dot-sourced by Run-Tests.ps1.

$script:CheckScript = Join-Path $PSScriptRoot '../../scripts/check.ps1'

function Get-CheckEngines {
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $pwsh) { throw 'pwsh was not found; the gate tests need it' }
    $engines = @($pwsh.Source)
    $ps51 = Get-Command powershell.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($ps51) { $engines += $ps51.Source }
    $engines
}

function New-CheckFakeDotnet {
    <#
      A fake dotnet first on PATH for the child gate run. 'warn' prints the flaky workspace warning to stderr
      and exits 0 (#398); 'fail' adds a genuine file diagnostic and exits 2. Returns the bin dir.
    #>
    param([Parameter(Mandatory)][string]$Mode)
    $bin = Join-Path ([IO.Path]::GetTempPath()) ('cm-check-test-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $bin | Out-Null
    if ($IsWindows) {
        $lines = @('@echo off', 'echo Warnings were encountered while loading the workspace. 1>&2')
        if ($Mode -eq 'fail') { $lines += 'echo src/Fake/Fake.cs(1,1): error WHITESPACE: fake violation for the gate test' }
        $lines += if ($Mode -eq 'fail') { 'exit /b 2' } else { 'exit /b 0' }
        Set-Content -LiteralPath (Join-Path $bin 'dotnet.cmd') -Value $lines
    }
    else {
        $lines = @('#!/bin/sh', "echo 'Warnings were encountered while loading the workspace.' 1>&2")
        if ($Mode -eq 'fail') { $lines += "echo 'src/Fake/Fake.cs(1,1): error WHITESPACE: fake violation for the gate test'" }
        $lines += if ($Mode -eq 'fail') { 'exit 2' } else { 'exit 0' }
        $fake = Join-Path $bin 'dotnet'
        Set-Content -LiteralPath $fake -Value $lines
        & chmod +x $fake
        if ($LASTEXITCODE -ne 0) { throw "chmod +x failed on the fake dotnet at $fake" }
    }
    $bin
}

function Remove-CheckFakeDotnet {
    param([string]$Bin)
    # Only ever deletes folders this file created.
    if ($Bin -like '*cm-check-test-*' -and (Test-Path -LiteralPath $Bin)) { Remove-Item -LiteralPath $Bin -Recurse -Force }
}

function Assert-CheckFakeResolves {
    <# The child inherits our PATH, so the fake must already win here or the case would run the real dotnet. #>
    param([Parameter(Mandatory)][string]$Bin)
    $oldPath = $env:PATH
    $env:PATH = $Bin + [IO.Path]::PathSeparator + $oldPath
    try {
        $found = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1
        Assert-True $found 'dotnet resolves at all with the fake bin on PATH'
        Assert-Match $found.Source 'cm-check-test-' "the fake dotnet wins PATH (got $($found.Source))"
    }
    finally {
        $env:PATH = $oldPath
    }
}

function Invoke-CheckFormatChild {
    param([Parameter(Mandatory)][string]$Engine, [Parameter(Mandatory)][string]$FakeBin)
    $oldPath = $env:PATH
    $env:PATH = $FakeBin + [IO.Path]::PathSeparator + $oldPath
    try {
        $lines = @(& $Engine -NoProfile -File $script:CheckScript -Phase format 2>&1 | ForEach-Object { "$_" })
        [pscustomobject]@{ Exit = $LASTEXITCODE; Lines = @($lines) }
    }
    finally {
        $env:PATH = $oldPath
    }
}

Test-Case 'check format: flaky workspace stderr still passes on every engine (#398)' {
    $bin = New-CheckFakeDotnet -Mode 'warn'
    try {
        Assert-CheckFakeResolves $bin
        foreach ($engine in (Get-CheckEngines)) {
            $r = Invoke-CheckFormatChild -Engine $engine -FakeBin $bin
            Assert-Equal 0 $r.Exit "$engine exited $($r.Exit): $($r.Lines -join ' | ')"
            Assert-Match ($r.Lines -join "`n") '^ok   format' "$engine did not report ok"
        }
    }
    finally {
        Remove-CheckFakeDotnet $bin
    }
}

Test-Case 'check format: genuine violations still fail on every engine' {
    $bin = New-CheckFakeDotnet -Mode 'fail'
    try {
        Assert-CheckFakeResolves $bin
        foreach ($engine in (Get-CheckEngines)) {
            $r = Invoke-CheckFormatChild -Engine $engine -FakeBin $bin
            Assert-Equal 1 $r.Exit "$engine exited $($r.Exit), want 1: $($r.Lines -join ' | ')"
            Assert-Match ($r.Lines -join "`n") 'FAIL format' "$engine did not report FAIL"
        }
    }
    finally {
        Remove-CheckFakeDotnet $bin
    }
}
