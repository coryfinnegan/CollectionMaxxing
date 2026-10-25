# Tests for the runner's own shell-suite verdict (Get-ShellSuiteResult in TestKit.ps1): a bash suite delivers its
# verdict as an exit code, so PASS lines must never mask an abort. Dot-sourced by Run-Tests.ps1. (#290)

Test-Case 'runner: an aborted suite (non-zero exit) fails despite its PASS lines' {
    $r = Get-ShellSuiteResult -Suite 's.test.sh' -Lines @('PASS one', 'PASS two', 's.test.sh: line 9: X: unbound variable') -ExitCode 1
    Assert-Equal 2 $r.Passed
    Assert-Equal 1 $r.Failed.Count
    Assert-Match $r.Failed[0] 'exited with code 1'
}

Test-Case 'runner: a suite that never prints SUMMARY fails even when it exits 0' {
    $r = Get-ShellSuiteResult -Suite 's.test.sh' -Lines @('PASS one', 'PASS two') -ExitCode 0
    Assert-Equal 2 $r.Passed
    Assert-Equal 1 $r.Failed.Count
    Assert-Match $r.Failed[0] 'never printed its SUMMARY line'
}

Test-Case 'runner: a clean suite passes with no failures' {
    $r = Get-ShellSuiteResult -Suite 's.test.sh' -Lines @('PASS one', 'SUMMARY passed=1 failed=0') -ExitCode 0
    Assert-Equal 1 $r.Passed
    Assert-Equal 0 $r.Failed.Count
}

Test-Case 'runner: FAIL lines plus a non-zero exit report both failures' {
    $r = Get-ShellSuiteResult -Suite 's.test.sh' -Lines @('PASS one', 'FAIL two: boom', 'SUMMARY passed=1 failed=1') -ExitCode 1
    Assert-Equal 1 $r.Passed
    Assert-Equal 2 $r.Failed.Count
    Assert-Match $r.Failed[1] 'exited with code 1'
}

Test-Case 'runner: empty output fails as ran-nothing and still reports a non-zero exit' {
    $r = Get-ShellSuiteResult -Suite 's.test.sh' -Lines @() -ExitCode 3
    Assert-Equal 0 $r.Passed
    Assert-Equal 2 $r.Failed.Count
    Assert-Match $r.Failed[0] 'ran nothing'
    Assert-Match $r.Failed[1] 'exited with code 3'
}
