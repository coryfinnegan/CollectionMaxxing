# Static checks on the two workflows that publish things or hold signing material: release.yml (images, attestations,
# the :main tag the deployer follows) and ios-build.yml (the signing secrets, TestFlight). A tag like @v7 can be moved
# by whoever controls that repository, and these jobs run with secrets or a token that can write, so every action in
# them is pinned to a full commit SHA (with the version in a comment, for Dependabot and for people). The release
# workflow's own checks are in release.tests.ps1.

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
