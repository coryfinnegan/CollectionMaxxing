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
