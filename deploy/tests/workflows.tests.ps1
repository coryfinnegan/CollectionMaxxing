# Static checks on the two workflows that publish things or hold signing material: release.yml (images, attestations,
# the :main tag the deployer follows) and ios-build.yml (the signing secrets, TestFlight). A tag like @v7 can be moved
# by whoever controls that repository, and these jobs run with secrets or a token that can write, so every action in
# them is pinned to a full commit SHA (with the version in a comment, for Dependabot and for people). The release
# workflow's own checks are in release.tests.ps1. The last section pins how ci.yml decides what a pull request runs.

$script:PinnedWorkflows = @('release.yml', 'ios-build.yml')

function Get-WorkflowText {
    param([string]$Name)
    $path = Join-Path $PSScriptRoot "../../.github/workflows/$Name"
    if (-not (Test-Path -LiteralPath $path)) { throw "workflow not found: $path" }
    @(Get-Content -LiteralPath $path)
}

function Get-WorkflowJobBlocks {
    <# Top-level jobs of a workflow as name -> lines, comments removed. Fails loudly when none are found. #>
    param([string]$Name)
    $jobs = [ordered]@{}
    $inJobs = $false
    $current = $null
    foreach ($raw in (Get-WorkflowText $Name)) {
        $line = $raw -replace '(^|\s+)#.*$', '$1'
        if ($line -match '^jobs:\s*$') { $inJobs = $true; continue }
        if ($inJobs -and $line -match '^\S') { $inJobs = $false; $current = $null; continue }
        if (-not $inJobs) { continue }
        if ($line -match '^  ([A-Za-z0-9_-]+):\s*$') { $current = $Matches[1]; $jobs[$current] = @() }
        elseif ($null -ne $current) { $jobs[$current] += $line }
    }
    if ($jobs.Count -eq 0) { throw "no jobs found in $Name; the reader no longer matches the file" }
    $jobs
}

foreach ($workflow in $script:PinnedWorkflows) {
    $wf = $workflow
    Test-Case "workflows: every action in $wf is pinned to a full commit SHA, with its version in a comment" {
        $uses = @(Get-WorkflowText $wf | Where-Object { $_ -match '^\s*(-\s+)?uses:' })
        Assert-True ($uses.Count -ge 1) "$wf uses at least one action"
        foreach ($line in $uses) {
            Assert-Match $line 'uses:\s*[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}\s+#\s*v\d+(\.\d+)*' "unpinned or uncommented action: $($line.Trim())"
        }
    }
}

Test-Case 'workflows: the same action is pinned to the same commit in both workflows' {
    $pins = @{}
    foreach ($wf in $script:PinnedWorkflows) {
        foreach ($line in (Get-WorkflowText $wf | Where-Object { $_ -match 'uses:\s*([^@\s]+)@([0-9a-f]{40})' })) {
            $null = $line -match 'uses:\s*([^@\s]+)@([0-9a-f]{40})'
            $action = $Matches[1]; $sha = $Matches[2]
            if ($pins.ContainsKey($action)) { Assert-Equal $pins[$action] $sha "$action is pinned to two different commits" } else { $pins[$action] = $sha }
        }
    }
    Assert-True ($pins.Count -ge 3) 'checkout, setup-node and at least one more are pinned'
}

Test-Case 'ios-build: the job that holds the signing secrets cannot write to the repository' {
    $jobs = Get-WorkflowJobBlocks 'ios-build.yml'
    Assert-True ($jobs.Contains('build')) 'the build job exists'
    $text = $jobs['build'] -join "`n"
    Assert-Match $text 'secrets\.APPLE_CERTIFICATE_P12' 'this is the job that holds the signing secrets'
    Assert-Match $text 'environment:\s*testflight' 'it runs in the testflight environment'
    Assert-Match $text '(?m)^    permissions:\s*\n\s+contents:\s*read\s*$' 'its token is read-only'
    Assert-False ($text -match 'write') 'nothing in the signing job asks to write (permissions, or a gh call that creates things)'
}

Test-Case 'ios-build: the one write (the GitHub release) is a separate job with no secrets and no environment' {
    $jobs = Get-WorkflowJobBlocks 'ios-build.yml'
    Assert-True ($jobs.Contains('release')) 'the release job exists'
    $text = $jobs['release'] -join "`n"
    Assert-Match $text 'contents:\s*write' 'it may create the release'
    Assert-Match $text '(?m)^    needs:\s*build\s*$' 'it waits for the build (the TestFlight upload) to succeed'
    Assert-Match $text "refs/tags/v" 'it runs for v* tags only'
    Assert-False ($text -match 'secrets\.') 'it reads no secret (only the workflow token)'
    Assert-False ($text -match '(?m)^    environment:') 'it does not enter the testflight environment'
    Assert-Match $text 'gh release create' 'it creates the release'
    Assert-Match $text 'needs\.build\.outputs\.build_number' 'it gets the build number the build uploaded'
    $build = $jobs['build'] -join "`n"
    Assert-Match $build 'build_number:\s*\$\{\{\s*steps\.published\.outputs\.build_number\s*\}\}' 'the build job publishes the number'
    Assert-False ($build -match 'gh release create') 'the signing job no longer creates the release'
}

Test-Case 'ios-build: the workflow-level token is read-only' {
    $text = (Get-WorkflowText 'ios-build.yml' | ForEach-Object { $_ -replace '(^|\s+)#.*$', '$1' }) -join "`n"
    Assert-Match $text '(?m)^permissions:\s*\n\s+contents:\s*read\s*$' 'top-level permissions are contents: read'
}

# ---- ci.yml: a pull request runs only the checks its files can affect (#633) ------------------------------------------
# The wiring decides what is skipped and whether the required checks (backend, app, e2e) still report, and a mistake in it
# fails silently (a skipped leg looks green), so it is pinned here. The classifier itself is tested in changed-areas.test.sh.

# leg -> the classifier answer it waits for
$script:CiGatedLegs = [ordered]@{ 'backend-test' = 'backend'; 'backend-format' = 'backend'; 'backend-tooling' = 'backend'; 'app' = 'app'; 'e2e' = 'app' }

Test-Case 'ci: the changes job diffs the merge commit against its first parent with renames off, and a failure fails it' {
    $jobs = Get-WorkflowJobBlocks 'ci.yml'
    Assert-True ($jobs.Contains('changes')) 'the changes job exists'
    $text = $jobs['changes'] -join "`n"
    Assert-Match $text 'git diff --name-only --no-renames HEAD\^1 HEAD' 'it lists what the merge commit brings over the base (a rename would hide its old path without --no-renames)'
    Assert-Match $text 'fetch-depth:\s*2' 'the checkout is deep enough to reach the first parent'
    Assert-Match $text '(?m)^\s+shell:\s*bash\s*$' 'the step names its shell: the default is bash -e without pipefail, and tee would hide a classifier that failed'
    Assert-Match $text 'scripts/ci/changed-areas\.sh' 'it runs the classifier'
    Assert-Match $text '\$EVENT"\s*=\s*pull_request' 'only a pull request is classified; every other event runs everything'
    Assert-Match $text 'backend=true\\napp=true' 'every other event answers true for both'
}

foreach ($ciLeg in $script:CiGatedLegs.Keys) {
    $ciArea = $script:CiGatedLegs[$ciLeg]
    Test-Case "ci: $ciLeg waits for changes and is skipped only when changes succeeded and said $ciArea=false" {
        $jobs = Get-WorkflowJobBlocks 'ci.yml'
        Assert-True ($jobs.Contains($ciLeg)) "the $ciLeg job exists"
        $text = $jobs[$ciLeg] -join "`n"
        Assert-Match $text '(?m)^    needs:\s*changes\s*$' 'it needs the changes job'
        $gate = "if: `${{ !cancelled() && (needs.changes.result != 'success' || needs.changes.outputs.$ciArea != 'false') }}"
        Assert-True ($text.Contains($gate)) "its if is: $gate (a failed, empty or missing answer must run the leg)"
        Assert-False ($text -match "outputs\.$ciArea\s*==") "it never tests for == (an empty answer would skip the leg)"
    }
}

Test-Case 'ci: the required contexts are plain jobs with their own names, so a skipped job still reports' {
    $jobs = Get-WorkflowJobBlocks 'ci.yml'
    foreach ($required in 'backend', 'app', 'e2e') {
        Assert-True ($jobs.Contains($required)) "the $required job exists (the ruleset requires that context)"
        $text = $jobs[$required] -join "`n"
        Assert-False ($text -match '(?m)^    name:') "$required has no name: its context is the job id"
        Assert-False ($text -match '(?m)^    strategy:') "$required is not a matrix: its context is the job id"
    }
}

Test-Case 'ci: the backend aggregate always runs and passes only what changes allows' {
    $jobs = Get-WorkflowJobBlocks 'ci.yml'
    $text = $jobs['backend'] -join "`n"
    Assert-Match $text '(?m)^    if:\s*always\(\)\s*$' 'it runs whatever happened to the legs'
    Assert-Match $text '(?m)^    needs:\s*\[\s*changes,\s*backend-test,\s*backend-format,\s*backend-tooling\s*\]\s*$' 'it needs changes and every backend leg'
    Assert-Match $text 'CHANGES_RESULT:\s*\$\{\{\s*needs\.changes\.result\s*\}\}' 'the changes result reaches the step through env'
    Assert-Match $text 'BACKEND_CHANGED:\s*\$\{\{\s*needs\.changes\.outputs\.backend\s*\}\}' 'the answer reaches the step through env'
    Assert-Match $text '"\$CHANGES_RESULT"\s*=\s*success\s*\]\s*&&\s*\[\s*"\$BACKEND_CHANGED"\s*=\s*false' 'it passes without checking only when changes succeeded and said backend=false'
    $reportSteps = [regex]::Matches($text, "if: \`$\{\{ needs\.changes\.result != 'success' \|\| needs\.changes\.outputs\.backend != 'false' \}\}")
    Assert-Equal 2 $reportSteps.Count 'the shard-report download and the add-up check both run whenever the legs ran'
    Assert-Match $text 'want="[^"]*CollectionMaxxing\.Api\.Tests\.Library' 'the add-up check covers the Library shards'
}

Test-Case 'ci: there is no workflow-level paths filter (a required check that never starts never reports)' {
    $lines = @(Get-WorkflowText 'ci.yml' | ForEach-Object { $_ -replace '(^|\s+)#.*$', '$1' })
    $end = [array]::FindIndex($lines, [Predicate[string]] { param($l) $l -match '^jobs:\s*$' })
    Assert-True ($end -gt 0) 'the jobs: line is found'
    $head = ($lines[0..($end - 1)]) -join "`n"
    Assert-Match $head '(?m)^on:' 'the trigger block is there'
    Assert-Match $head '(?m)^  pull_request:' 'pull requests trigger CI'
    Assert-False ($head -match '(?m)^\s+(paths|paths-ignore):') 'no paths or paths-ignore under on:'
}
