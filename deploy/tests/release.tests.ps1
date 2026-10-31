# Static checks on .github/workflows/release.yml: the :main tags must always name a complete, attested
# image set, so the deployer (which reads api:main for the newest commit) never chases a partial release.

$script:ReleaseWorkflow = Join-Path $PSScriptRoot '../../.github/workflows/release.yml'

function Get-ReleaseJobs {
    <#
      Reads the workflow's top-level jobs into name -> text. Fails loudly when the file is missing or
      has no jobs, so a rename or reformat breaks the suite instead of silently passing it.
    #>
    if (-not (Test-Path -LiteralPath $script:ReleaseWorkflow)) { throw "release workflow not found at $script:ReleaseWorkflow" }
    $jobs = [ordered]@{}
    $inJobs = $false
    $current = $null
    foreach ($raw in (Get-Content -LiteralPath $script:ReleaseWorkflow)) {
        $line = $raw -replace '(^|\s+)#.*$', '$1'   # comments say nothing about behaviour; the tests read code only
        if ($line -match '^jobs:\s*$') { $inJobs = $true; continue }
        if ($inJobs -and $line -match '^\S') { $inJobs = $false; $current = $null; continue }
        if (-not $inJobs) { continue }
        if ($line -match '^  ([A-Za-z0-9_-]+):\s*$') { $current = $Matches[1]; $jobs[$current] = @() }
        elseif ($null -ne $current) { $jobs[$current] += $line }
    }
    if ($jobs.Count -eq 0) { throw 'no jobs found in release.yml; the reader no longer matches the file' }
    $jobs
}

function Test-JobNeedsImages {
    <# True when the job block waits for the images job (needs: images, in scalar or list form). #>
    param([string[]]$Block)
    for ($i = 0; $i -lt $Block.Count; $i++) {
        if ($Block[$i] -notmatch '^\s+needs:\s*(.*)$') { continue }
        $rest = $Matches[1].Trim()
        if ($rest -match '\bimages\b') { return $true }
        for ($j = $i + 1; $j -lt $Block.Count -and $Block[$j] -match '^\s+-\s+(\S+)'; $j++) {
            if ($Matches[1] -match '^[''"]?images[''"]?$') { return $true }
        }
        return $false
    }
    $false
}

Test-Case 'release: the build legs push only the SHA tag, never :main' {
    $jobs = Get-ReleaseJobs
    Assert-True ($jobs.Contains('images')) 'the images job exists'
    $text = $jobs['images'] -join "`n"
    Assert-Match $text 'github\.sha' 'each leg still pushes its commit-SHA tag'
    Assert-False ($text -match ':main') 'no leg moves :main on its own (a failed sibling would leave a partial set)'
}

Test-Case 'release: each leg signs its attestation before anything downstream can run' {
    $jobs = Get-ReleaseJobs
    $text = $jobs['images'] -join "`n"
    Assert-Match $text 'attest-build-provenance' 'the attestation is a step of the build legs'
    Assert-Match $text 'push-to-registry:\s*true' 'the attestation is pushed with the image'
}

Test-Case 'release: every job that moves :main waits for all build legs and their attestations' {
    $jobs = Get-ReleaseJobs
    $movers = @($jobs.Keys | Where-Object { ($jobs[$_] -join "`n") -match ':main' })
    Assert-True ($movers.Count -ge 1) 'some job moves :main, or the deployer would never see a new commit'
    foreach ($name in $movers) {
        Assert-True (Test-JobNeedsImages $jobs[$name]) "$name moves :main but does not need the images job"
    }
}

# ---- the CI gate: :main moves only after CI succeeded for this same commit -------------------------------------------------

function Test-JobNeeds {
    <# True when the job block waits for the named job (needs: x, needs: [x, y] or a list). #>
    param([string[]]$Block, [string]$Job)
    for ($i = 0; $i -lt $Block.Count; $i++) {
        if ($Block[$i] -notmatch '^\s+needs:\s*(.*)$') { continue }
        if ($Matches[1] -match "(^|[\s\[,'""])$([regex]::Escape($Job))($|[\s\],'""])") { return $true }
        for ($j = $i + 1; $j -lt $Block.Count -and $Block[$j] -match '^\s+-\s+(\S+)'; $j++) {
            if ($Matches[1] -match "^['""]?$([regex]::Escape($Job))['""]?$") { return $true }
        }
        return $false
    }
    $false
}

function Get-ReleaseCode {
    <# The workflow file with comments removed, as one string. #>
    (Get-Content -LiteralPath $script:ReleaseWorkflow | ForEach-Object { $_ -replace '(^|\s+)#.*$', '$1' }) -join "`n"
}

Test-Case 'release: every job that moves :main also waits for the CI gate and needs its verdict to be a pass' {
    $jobs = Get-ReleaseJobs
    Assert-True ($jobs.Contains('ci-gate')) 'the ci-gate job exists'
    $movers = @($jobs.Keys | Where-Object { ($jobs[$_] -join "`n") -match ':main' })
    foreach ($name in $movers) {
        Assert-True (Test-JobNeeds $jobs[$name] 'ci-gate') "$name moves :main without waiting for ci-gate"
        Assert-Match ($jobs[$name] -join "`n") "needs\.ci-gate\.outputs\.passed\s*==\s*'true'" "$name must run only when the gate's verdict is passed=true (a cancelled CI ends the gate without failing it)"
    }
}

Test-Case 'release: the CI gate asks for the CI run of this very commit and may read workflow runs' {
    $jobs = Get-ReleaseJobs
    $text = $jobs['ci-gate'] -join "`n"
    Assert-Match $text 'SHA:\s*\$\{\{\s*github\.sha\s*\}\}' 'the gate is told this commit (github.sha), not the branch'
    Assert-Match $text 'scripts/wait-for-ci\.sh' 'the gate runs the wait-for-ci script'
    Assert-Match $text 'actions:\s*read' 'the gate may read workflow runs'
    Assert-Match $text 'passed:\s*\$\{\{\s*steps\.[A-Za-z0-9_-]+\.outputs\.passed\s*\}\}' 'the gate publishes passed as a job output'
    Assert-False ($text -match 'packages:|id-token:|attestations:|contents:\s*write') 'the gate holds no write permission'
    Assert-True (Test-Path -LiteralPath (Join-Path $PSScriptRoot '../../scripts/wait-for-ci.sh')) 'the script the gate runs exists'
}

Test-Case 'release: the workflow still starts on the push to main, which is the identity the deployer checks' {
    # The deployer requires a certificate for release.yml at refs/heads/main with the image's commit as the source digest.
    # A workflow_run trigger would stamp the run with main's latest commit instead of the one CI tested.
    $code = Get-ReleaseCode
    Assert-Match $code '(?m)^on:\s*\r?\n\s+push:\s*\r?\n\s+branches:\s*\[main\]' 'the workflow runs on pushes to main'
    Assert-False ($code -match 'workflow_run') 'the workflow is not started by workflow_run'
    Assert-False ($code -match '(?m)^\s+(pull_request|pull_request_target|workflow_dispatch|schedule):') 'no other trigger can produce an attested image'
}

Test-Case 'release: the attestation steps and the names the deployer checks are intact' {
    $code = Get-ReleaseCode
    Assert-Match $code 'actions/attest-build-provenance@[0-9a-f]{40}' 'the attestation action is pinned and present'
    Assert-Match $code 'subject-digest:\s*\$\{\{\s*steps\.build\.outputs\.digest\s*\}\}' 'the attestation covers the pushed digest'
    Assert-Match $code 'push-to-registry:\s*true' 'the attestation is pushed with the image'
    Assert-Match $code '(?m)^name:\s*Release images\s*$' 'the workflow keeps its name'
    Assert-True (Test-Path -LiteralPath (Join-Path $PSScriptRoot '../../.github/workflows/release.yml')) 'the deployer verifies .github/workflows/release.yml'
    $deployer = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../deployer/Cm.Deploy.ps1') -Raw
    Assert-Match $deployer '\.github/workflows/release\.yml' 'the deployer still names release.yml as the signer workflow'
}

Test-Case 'release: the :main promotion covers api, migrate and backup together' {
    $jobs = Get-ReleaseJobs
    $movers = @($jobs.Keys | Where-Object { ($jobs[$_] -join "`n") -match ':main' })
    foreach ($name in $movers) {
        $text = $jobs[$name] -join "`n"
        foreach ($image in 'api', 'migrate', 'backup') {
            Assert-Match $text $image "$name moves :main but never names $image"
        }
    }
}
