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

Test-Case 'runner: the lines printed after a FAIL line (docker''s own error) stay in the report' {
    $lines = @('PASS one', 'docker: Error response from daemon: toomanyrequests: rate limit', "Run 'docker run --help' for more information", 'FAIL setup: could not start postgres:17',
        'extra detail 1', 'extra detail 2', 'SUMMARY passed=1 failed=1')
    $r = Get-ShellSuiteResult -Suite 's.test.sh' -Lines $lines -ExitCode 1
    Assert-Equal 2 $r.Failed.Count 'one FAIL entry (with its detail) and the exit code'
    Assert-Match $r.Failed[0] 'could not start postgres:17'
    Assert-Match $r.Failed[0] 'extra detail 1'
    Assert-Match $r.Failed[0] 'extra detail 2'
    Assert-Match $r.Failed[1] 'toomanyrequests' 'the exit-code report shows the last lines of output, not just three'
}

Test-Case 'runner: FAIL detail stops at the next PASS line' {
    $r = Get-ShellSuiteResult -Suite 's.test.sh' -Lines @('FAIL a: x', 'detail of a', 'PASS b', 'not detail of a', 'SUMMARY passed=1 failed=1') -ExitCode 1
    Assert-Match $r.Failed[0] 'detail of a'
    Assert-False ($r.Failed[0] -match 'not detail') 'text after a PASS line is not part of the FAIL'
}

Test-Case 'runner: empty output fails as ran-nothing and still reports a non-zero exit' {
    $r = Get-ShellSuiteResult -Suite 's.test.sh' -Lines @() -ExitCode 3
    Assert-Equal 0 $r.Passed
    Assert-Equal 2 $r.Failed.Count
    Assert-Match $r.Failed[0] 'ran nothing'
    Assert-Match $r.Failed[1] 'exited with code 3'
}
