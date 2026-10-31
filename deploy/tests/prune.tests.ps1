# Old-image clean-up tests: after a good deploy (and on quiet polls) the deployer removes old images of this project's
# three repositories (api, migrate, backup) and nothing else. Dot-sourced by Run-Tests.ps1 after ops2.tests.ps1 (it uses the
# helpers from deployer.tests.ps1, ops1b.tests.ps1 and ops2.tests.ps1). Nothing here touches the real Docker: every
# `docker image ls` / `docker image rm` goes to the recorder in TestKit.ps1.

$script:PruneRepos = @{
    api     = 'ghcr.io/coryfinnegan/collectionmaxxing-api'
    migrate = 'ghcr.io/coryfinnegan/collectionmaxxing-migrate'
    backup  = 'ghcr.io/coryfinnegan/collectionmaxxing-backup'
}
$script:PruneEpoch = [DateTimeOffset]::Parse('2026-10-01T00:00:00Z')

function New-PruneSha { param([int]$N) ('{0:x40}' -f $N) }

function New-PruneId {
    <# A random-looking image id that is stable for one repository and tag (real ids are hashes, never runs of zeros). #>
    param([string]$Repo, [string]$Tag)
    'sha256:' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("$Repo|$Tag"))).ToLowerInvariant()
}

function New-PruneImage {
    <# The object Select-CmImagesToPrune takes: the Nth image of a repository, built N hours after the epoch. #>
    param([string]$Repo = $script:PruneRepos.api, [string]$Tag, [int]$N = 0, $Created = 'n', [string]$Id = '')
    if (-not $Tag) { $Tag = New-PruneSha $N }
    if (-not $Id) { $Id = New-PruneId $Repo $Tag }
    $when = if ($Created -is [string] -and $Created -eq 'n') { $script:PruneEpoch.AddHours($N) } else { $Created }
    [pscustomobject]@{ Repository = $Repo; Tag = $Tag; Id = $Id; Created = $when }
}

function New-PruneRow {
    <# One line the way `docker image ls --no-trunc --format '{{.Repository}}|{{.Tag}}|{{.ID}}|{{.CreatedAt}}'` prints it. #>
    param([string]$Repo = $script:PruneRepos.api, [string]$Tag, [int]$N = 0, [string]$CreatedText = '')
    if (-not $Tag) { $Tag = New-PruneSha $N }
    if (-not $CreatedText) { $CreatedText = $script:PruneEpoch.AddHours($N).ToOffset([TimeSpan]::FromHours(-5)).ToString('yyyy-MM-dd HH:mm:ss') + ' -0500 CDT' }
    "$Repo|$Tag|$(New-PruneId $Repo $Tag)|$CreatedText"
}

function New-PruneRows {
    <# Images 1..Count of each of the three repositories, N hours apart (a higher N is newer). #>
    param([int]$Count)
    $rows = @()
    foreach ($repo in $script:PruneRepos.Values) { foreach ($n in 1..$Count) { $rows += New-PruneRow -Repo $repo -N $n } }
    $rows
}

function Get-ImageRemovals { @(Get-Calls 'docker' 'docker image rm *') }
function Get-RemovedRefs { @(Get-ImageRemovals | ForEach-Object { $_.Arguments[2] }) }

# ---- the selection (pure) ------------------------------------------------------------------------------------------------

Test-Case 'prune select: the running and the previous image are always kept, however old, plus the 3 newest of the rest' {
    $images = @(1..10 | ForEach-Object { New-PruneImage -N $_ })
    $keep = @((New-PruneSha 1), (New-PruneSha 2))   # the two OLDEST: current and previous good
    $sel = Select-CmImagesToPrune -Images $images -Repositories @($script:PruneRepos.api) -KeepTags $keep
    $removed = @($sel.Remove | ForEach-Object Tag)
    Assert-Equal 5 $removed.Count 'ten images, minus current, previous and the 3 newest'
    foreach ($n in 1, 2, 8, 9, 10) { Assert-False ($removed -contains (New-PruneSha $n)) "image $n must be kept" }
    foreach ($n in 3..7) { Assert-True ($removed -contains (New-PruneSha $n)) "image $n should go" }
}

Test-Case 'prune select: tags are ordered by the image created time, not by name or listing order' {
    # Tag order is the opposite of build order, and the listing is shuffled.
    $images = @(
        (New-PruneImage -Tag (New-PruneSha 9) -Created $script:PruneEpoch.AddHours(1)),
        (New-PruneImage -Tag (New-PruneSha 1) -Created $script:PruneEpoch.AddHours(9)),
        (New-PruneImage -Tag (New-PruneSha 5) -Created $script:PruneEpoch.AddHours(5)),
        (New-PruneImage -Tag (New-PruneSha 2) -Created $script:PruneEpoch.AddHours(8)),
        (New-PruneImage -Tag (New-PruneSha 3) -Created $script:PruneEpoch.AddHours(2)),
        (New-PruneImage -Tag (New-PruneSha 4) -Created $script:PruneEpoch.AddHours(7)),
        (New-PruneImage -Tag (New-PruneSha 7) -Created $script:PruneEpoch.AddHours(3)))
    $sel = Select-CmImagesToPrune -Images $images -Repositories @($script:PruneRepos.api) -KeepTags @((New-PruneSha 5))
    # Kept: sha 5 (current) and the 3 newest by creation: sha 1 (h9), sha 2 (h8), sha 4 (h7). Removed oldest first: h1, h2, h3.
    Assert-Equal "$(New-PruneSha 9) $(New-PruneSha 3) $(New-PruneSha 7)" (@($sel.Remove | ForEach-Object Tag) -join ' ')
}

Test-Case 'prune select: the current and the previous image survive even when nothing else is old enough to remove' {
    $images = @(1..3 | ForEach-Object { New-PruneImage -N $_ })
    $sel = Select-CmImagesToPrune -Images $images -Repositories @($script:PruneRepos.api) -KeepTags @((New-PruneSha 1), (New-PruneSha 2))
    Assert-Equal 0 @($sel.Remove).Count
}

Test-Case 'prune select: another repository, a lookalike repository and a non-sha tag are never selected' {
    $api = $script:PruneRepos.api
    $images = @(
        (New-PruneImage -Repo $api -Tag 'main' -N 1),
        (New-PruneImage -Repo $api -Tag 'latest' -N 2),
        (New-PruneImage -Repo $api -Tag '<none>' -N 3),
        (New-PruneImage -Repo $api -Tag ('a' * 39) -N 4),
        (New-PruneImage -Repo $api -Tag ('A' * 40) -N 5),
        (New-PruneImage -Repo $api -Tag (('a' * 40) + 'x') -N 6),
        (New-PruneImage -Repo 'ghcr.io/coryfinnegan/collectionmaxxing-other' -Tag (New-PruneSha 7) -N 7),
        (New-PruneImage -Repo 'ghcr.io/someoneelse/collectionmaxxing-api' -Tag (New-PruneSha 8) -N 8),
        (New-PruneImage -Repo 'ghcr.io/coryfinnegan/collectionmaxxing-api2' -Tag (New-PruneSha 9) -N 9),
        (New-PruneImage -Repo 'postgres' -Tag (New-PruneSha 10) -N 10),
        (New-PruneImage -Repo 'mcr.microsoft.com/dotnet/aspire-dashboard' -Tag (New-PruneSha 11) -N 11),
        (New-PruneImage -Repo '<none>' -Tag '<none>' -N 12),
        (New-PruneImage -Repo $api.ToUpperInvariant() -Tag (New-PruneSha 13) -N 13))
    # A current image that is not even listed: everything is "old", yet none of the above may be chosen.
    $sel = Select-CmImagesToPrune -Images $images -Repositories @($api, $script:PruneRepos.migrate, $script:PruneRepos.backup) -KeepTags @((New-PruneSha 99)) -KeepRecent 0
    Assert-Equal 0 @($sel.Remove).Count "selected: $(@($sel.Remove | ForEach-Object { $_.Repository + ':' + $_.Tag }) -join ', ')"
}

Test-Case 'prune select: each repository is judged on its own' {
    $images = @()
    foreach ($repo in $script:PruneRepos.Values) { $images += @(1..6 | ForEach-Object { New-PruneImage -Repo $repo -N $_ }) }
    $sel = Select-CmImagesToPrune -Images $images -Repositories @($script:PruneRepos.Values) -KeepTags @((New-PruneSha 6))
    # Per repository: sha 6 is current (kept), 5/4/3 are the 3 newest beyond it, so 1 and 2 go: 2 x 3 repositories.
    Assert-Equal 6 @($sel.Remove).Count
    foreach ($repo in $script:PruneRepos.Values) {
        $mine = @($sel.Remove | Where-Object Repository -eq $repo | ForEach-Object Tag)
        Assert-Equal "$(New-PruneSha 1) $(New-PruneSha 2)" ($mine -join ' ') $repo
    }
}

Test-Case 'prune select: at most Max tags per call, oldest first, and a later call carries on where this one stopped' {
    $images = @(1..260 | ForEach-Object { New-PruneImage -N $_ })
    $keep = @((New-PruneSha 260))
    $repos = @($script:PruneRepos.api)
    $first = Select-CmImagesToPrune -Images $images -Repositories $repos -KeepTags $keep -Max 100
    Assert-Equal 100 @($first.Remove).Count 'the cap holds'
    Assert-Equal (New-PruneSha 1) $first.Remove[0].Tag 'oldest first'
    Assert-Equal (New-PruneSha 100) $first.Remove[99].Tag
    Assert-Equal 256 $first.Eligible 'it reports how many tags were eligible before the cap'
    $left = @($images | Where-Object { @($first.Remove | ForEach-Object Tag) -notcontains $_.Tag })
    $second = Select-CmImagesToPrune -Images $left -Repositories $repos -KeepTags $keep -Max 100
    Assert-Equal (New-PruneSha 101) $second.Remove[0].Tag 'the next poll continues'
    $left = @($left | Where-Object { @($second.Remove | ForEach-Object Tag) -notcontains $_.Tag })
    $third = Select-CmImagesToPrune -Images $left -Repositories $repos -KeepTags $keep -Max 100
    Assert-Equal 56 @($third.Remove).Count 'the rest'
    $left = @($left | Where-Object { @($third.Remove | ForEach-Object Tag) -notcontains $_.Tag })
    Assert-Equal 4 $left.Count 'what is left is the current image and the 3 newest'
    Assert-Equal 0 @((Select-CmImagesToPrune -Images $left -Repositories $repos -KeepTags $keep -Max 100).Remove).Count
}

Test-Case 'prune select: an image a container still uses is skipped, by reference and by image id' {
    $images = @(1..8 | ForEach-Object { New-PruneImage -N $_ })
    $byRef = "$($script:PruneRepos.api):$(New-PruneSha 1)"
    $byId = $images[1].Id            # a container created from a tag that has since moved shows the bare id
    $byShortId = $images[2].Id.Substring(7, 12)
    $sel = Select-CmImagesToPrune -Images $images -Repositories @($script:PruneRepos.api) -KeepTags @((New-PruneSha 8)) -InUse @($byRef, $byId, $byShortId)
    $removed = @($sel.Remove | ForEach-Object Tag)
    Assert-Equal 0 @($removed | Where-Object { $_ -in @((New-PruneSha 1), (New-PruneSha 2), (New-PruneSha 3)) }).Count 'in-use images stay'
    Assert-Equal 1 $removed.Count 'sha 4 is the only one left to remove (5, 6, 7 are the 3 newest, 8 is current)'
    Assert-Equal (New-PruneSha 4) $removed[0]
}

Test-Case 'prune select: when nothing is known to be in use, nothing is selected' {
    $images = @(1..10 | ForEach-Object { New-PruneImage -N $_ })
    $sel = Select-CmImagesToPrune -Images $images -Repositories @($script:PruneRepos.api) -KeepTags @()
    Assert-Equal 0 @($sel.Remove).Count
    $sel = Select-CmImagesToPrune -Images $images -Repositories @($script:PruneRepos.api) -KeepTags @('', $null, 'not-a-sha')
    Assert-Equal 0 @($sel.Remove).Count 'a kept tag that is not a full commit hash is not a usable answer either'
}

Test-Case 'prune select: an image whose created time is unknown is never removed' {
    $images = @(1..8 | ForEach-Object { New-PruneImage -N $_ })
    $images[0].Created = $null
    $sel = Select-CmImagesToPrune -Images $images -Repositories @($script:PruneRepos.api) -KeepTags @((New-PruneSha 8))
    Assert-False (@($sel.Remove | ForEach-Object Tag) -contains (New-PruneSha 1)) 'it cannot be ordered, so it stays'
    Assert-Equal 3 @($sel.Remove).Count '8 images: current, the 3 newest and the unordered one stay'
}

Test-Case 'prune: the created time is read from the line docker prints, any time zone' {
    $utc = ConvertFrom-CmDockerCreatedAt '2026-10-30 22:31:55 -0500 CDT'
    Assert-Equal '2026-10-31T03:31:55.0000000Z' $utc.UtcDateTime.ToString('o')
    Assert-Equal '2026-10-30T12:00:00.0000000Z' (ConvertFrom-CmDockerCreatedAt '2026-10-30 12:00:00 +0000 UTC').UtcDateTime.ToString('o')
    Assert-Equal '2026-10-30T06:30:00.0000000Z' (ConvertFrom-CmDockerCreatedAt '2026-10-30 12:00:00 +0530 IST').UtcDateTime.ToString('o')
    Assert-Equal '2026-10-30T12:00:00.5000000Z' (ConvertFrom-CmDockerCreatedAt '2026-10-30 12:00:00.5 +0000 UTC').UtcDateTime.ToString('o') 'fractions of a second'
    foreach ($bad in '', 'yesterday', '3 days ago', '2026-13-45 99:99:99 +0000 UTC', '2026-10-30') {
        Assert-True ($null -eq (ConvertFrom-CmDockerCreatedAt $bad)) "[$bad] is not a time"
    }
}

Test-Case 'prune: CM_PRUNE_IMAGES is on unless it says 0 (or false, off, no), in .env or in the environment' {
    Assert-True (Test-CmPruneEnabled -EnvMap @{} -ProcessValue '')
    Assert-True (Test-CmPruneEnabled -EnvMap @{ CM_PRUNE_IMAGES = '1' } -ProcessValue '')
    Assert-True (Test-CmPruneEnabled -EnvMap @{ CM_PRUNE_IMAGES = '' } -ProcessValue '')
    Assert-True (Test-CmPruneEnabled -EnvMap @{ CM_PRUNE_IMAGES = 'true' } -ProcessValue '')
    foreach ($off in '0', 'false', 'FALSE', 'off', 'No', ' 0 ') {
        Assert-False (Test-CmPruneEnabled -EnvMap @{ CM_PRUNE_IMAGES = $off } -ProcessValue '') ".env [$off]"
        Assert-False (Test-CmPruneEnabled -EnvMap @{} -ProcessValue $off) "environment [$off]"
    }
    Assert-False (Test-CmPruneEnabled -EnvMap @{ CM_PRUNE_IMAGES = '1' } -ProcessValue '0') 'either one switches it off'
}

# ---- in a deploy ------------------------------------------------------------------------------------------------------

function Get-PruneMachine {
    <# 12 images per repository; Sha1 and Sha2 (the real-looking test commits) are the previous and the new one. #>
    param([int]$Count = 12, [hashtable]$Extra = @{})
    $rows = @()
    foreach ($repo in $script:PruneRepos.Values) {
        foreach ($n in 1..$Count) { $rows += New-PruneRow -Repo $repo -N $n }
        # The previous good version is older than every numbered image on purpose: only the keep rule saves it.
        $rows += New-PruneRow -Repo $repo -Tag $script:Sha1 -CreatedText '2026-09-01 00:00:00 -0500 CDT'
        # The one that is being deployed now.
        $rows += New-PruneRow -Repo $repo -Tag $script:Sha2 -N 100000
    }
    $m = @{ ImageRows = $rows }
    foreach ($k in $Extra.Keys) { $m[$k] = $Extra[$k] }
    $m
}

Test-Case 'prune: a good deploy removes old images by explicit repo:tag and keeps the new one, the previous one and the 3 newest of each' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine (Get-PruneMachine)
    Assert-Equal 'deployed' $r.Result.Outcome
    $refs = Get-RemovedRefs
    # Per repository: 14 tags. Kept: Sha2 (new), Sha1 (previous), the 3 newest numbered (12, 11, 10). Removed: 1..9.
    Assert-Equal 27 $refs.Count "removed: $($refs -join ' ')"
    foreach ($repo in $script:PruneRepos.Values) {
        foreach ($n in 1..9) { Assert-True ($refs -contains "${repo}:$(New-PruneSha $n)") "$repo $n should be removed" }
        foreach ($n in 10..12) { Assert-False ($refs -contains "${repo}:$(New-PruneSha $n)") "$repo $n is one of the 3 newest" }
        Assert-False ($refs -contains "${repo}:$($script:Sha1)") 'the previous good version is the rollback target'
        Assert-False ($refs -contains "${repo}:$($script:Sha2)") 'the new version is running'
    }
    foreach ($c in Get-ImageRemovals) {
        Assert-True $c.Mutating 'image removals are mutating calls'
        Assert-Equal 3 @($c.Arguments).Count 'image rm <ref>: no flags'
        Assert-Equal 'image' $c.Arguments[0]; Assert-Equal 'rm' $c.Arguments[1]
        Assert-Match $c.Arguments[2] '^ghcr\.io/coryfinnegan/collectionmaxxing-(api|migrate|backup):[0-9a-f]{40}$'
    }
    $other = @($global:CmCalls | Where-Object { $_.Command -eq 'docker' -and $_.Display -match '^docker (image|system|volume|container|network|builder|rmi|rm)\b' -and $_.Display -notmatch '^docker image (inspect|ls|rm) ' })
    Assert-Equal 0 $other.Count "only image ls / image rm / image inspect: $(($other | ForEach-Object Display) -join ' | ')"
    Assert-Equal 0 @($global:CmCalls | Where-Object { $_.Display -match '\bprune\b|\brmi\b|image rm .*(-f|--force)' }).Count 'never prune, never force'
    Assert-Equal 0 (Get-AlertCalls).Count
    Assert-Match $r.Log 'removed 27 old image'
}

Test-Case 'prune: the previous good version is remembered in the state, as the rollback target' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine (Get-PruneMachine)
    Assert-Equal $script:Sha2 $r.State.current.sha
    Assert-Equal $script:Sha1 $r.State.previous.sha
}

Test-Case 'prune: it runs only after the new version passed the smoke test and the service check' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine (Get-PruneMachine)
    $smoke = Get-CallIndex 'curl*health/ready'
    $ps = Get-CallIndex '*ps --format json*'
    $list = Get-CallIndex 'docker image ls*'
    $rm = Get-CallIndex 'docker image rm*'
    Assert-True ($smoke -ge 0 -and $ps -gt $smoke -and $list -gt $ps -and $rm -gt $list) "smoke=$smoke ps=$ps list=$list rm=$rm"
}

Test-Case 'prune: a deploy that rolled back, failed to migrate, or was refused removes nothing' {
    foreach ($machine in @{ UnhealthyTags = @($script:Sha2) }, @{ MigrateExit = 1 }, @{ BadAttest = @('api') }, @{ DumpExit = 1; ChangedFiles = @('src/X/Migrations/1_A.cs') }) {
        $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine (Get-PruneMachine -Extra $machine)
        Assert-True ($r.Result.ExitCode -ne 0) "outcome $($r.Result.Outcome)"
        Assert-Equal 0 (Get-ImageRemovals).Count "outcome $($r.Result.Outcome)"
        Assert-Equal 0 (Get-Calls 'docker' 'docker image ls*').Count "outcome $($r.Result.Outcome) lists nothing"
    }
}

Test-Case 'prune: a quiet run carries on with the backlog, and keeps the running and previous versions' {
    $state = Get-QuietState @{ previous = @{ sha = $script:Sha1 } }
    $r = Invoke-TestDeploy -State $state -Machine (Get-PruneMachine)
    Assert-Equal 'up-to-date' $r.Result.Outcome
    Assert-Equal 27 (Get-ImageRemovals).Count
    $refs = Get-RemovedRefs
    foreach ($repo in $script:PruneRepos.Values) {
        Assert-False ($refs -contains "${repo}:$($script:Sha1)")
        Assert-False ($refs -contains "${repo}:$($script:Sha2)")
    }
}

Test-Case 'prune: a known-bad poll carries on too, keeping the version that runs' {
    $state = Get-OldGoodState; $state['failedSha'] = $script:Sha2
    $r = Invoke-TestDeploy -State $state -Machine (Get-PruneMachine)
    Assert-Equal 'known-bad' $r.Result.Outcome
    Assert-True ((Get-ImageRemovals).Count -gt 0)
    Assert-False (@(Get-RemovedRefs) -contains "$($script:PruneRepos.api):$($script:Sha1)") 'Sha1 is what runs'
}

Test-Case 'prune: a clean machine costs one listing per poll and removes nothing' {
    $r = Invoke-TestDeploy -State (Get-QuietState) -Machine (Get-PruneMachine -Count 2)
    Assert-Equal 'up-to-date' $r.Result.Outcome
    Assert-Equal 0 (Get-ImageRemovals).Count
    Assert-Equal 0 @(Get-MutatingCalls | Where-Object { $_.Display -notmatch '^docker pull|^git ' }).Count 'only the pull and the clone move'
    Assert-False ($r.Log -match 'old image') 'nothing to say, so nothing is logged'
}

Test-Case 'prune: CM_PRUNE_IMAGES=0 in .env switches it off completely' {
    $envText = @('POSTGRES_PASSWORD=test-only-password', 'CM_BACKUP_DIR=/tmp/cm-test-backups', 'CM_API_PORT=5480', 'CM_DASHBOARD_PORT=18888',
        'CM_HEARTBEAT_URL=https://hc.example/test-uuid', 'CM_SECRETS_DIR=/tmp/cm-test-secrets', 'CM_PRUNE_IMAGES=0',
        'CM_PUBLIC_ORIGIN=https://pc.tailnet.ts.net', 'CM_AUTH0_DOMAIN=test.us.auth0.com', 'CM_AUTH0_WEB_CLIENT_ID=test-web', 'CM_AUTH0_WEB_CLIENT_SECRET=test-secret', 'CM_AUTH0_MANAGEMENT_CLIENT_ID=test-mgmt', 'CM_AUTH0_MANAGEMENT_CLIENT_SECRET=test-secret')
    foreach ($state in (Get-OldGoodState), (Get-QuietState)) {
        $r = Invoke-TestDeploy -State $state -Machine (Get-PruneMachine) -EnvText $envText
        Assert-True ($r.Result.ExitCode -eq 0) "outcome $($r.Result.Outcome)"
        Assert-Equal 0 (Get-Calls 'docker' 'docker image ls*').Count 'no listing'
        Assert-Equal 0 (Get-ImageRemovals).Count 'no removal'
        Assert-Equal 0 (Get-Calls 'docker' 'docker ps *').Count
    }
}

Test-Case 'prune: CM_PRUNE_IMAGES=0 in the environment switches it off too' {
    $saved = $env:CM_PRUNE_IMAGES
    try {
        $env:CM_PRUNE_IMAGES = '0'
        $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine (Get-PruneMachine)
        Assert-Equal 'deployed' $r.Result.Outcome
        Assert-Equal 0 (Get-Calls 'docker' 'docker image ls*').Count
        Assert-Equal 0 (Get-ImageRemovals).Count
    }
    finally { $env:CM_PRUNE_IMAGES = $saved }
}

Test-Case 'prune: a removal that fails is a warning, never an alert, and never fails the deploy' {
    $stuck = "$($script:PruneRepos.api):$(New-PruneSha 3)"
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine (Get-PruneMachine -Extra @{ RmFails = @($stuck) })
    Assert-Equal 'deployed' $r.Result.Outcome
    Assert-Equal 0 $r.Result.ExitCode
    Assert-Equal 0 (Get-AlertCalls).Count
    Assert-Equal 0 (Get-AlertLines $r.Log)
    Assert-Match $r.Log 'WARN .*could not remove 1 old image'
    Assert-Match $r.Log 'removed 26 old image'
    Assert-Equal $script:Sha2 $r.State.current.sha
}

Test-Case 'prune: when every removal fails it gives up after 5 in a row instead of trying all of them' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine (Get-PruneMachine -Extra @{ RmAllFail = $true })
    Assert-Equal 'deployed' $r.Result.Outcome
    Assert-Equal 5 (Get-ImageRemovals).Count
    Assert-Equal 0 (Get-AlertCalls).Count
    Assert-Match $r.Log 'WARN .*could not remove 5 old image'
}

Test-Case 'prune: a listing that fails is a warning and the deploy is still a success' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine (Get-PruneMachine -Extra @{ ListFails = $true })
    Assert-Equal 'deployed' $r.Result.Outcome
    Assert-Equal 0 (Get-ImageRemovals).Count
    Assert-Equal 0 (Get-AlertCalls).Count
    Assert-Match $r.Log 'WARN .*could not list'
}

Test-Case 'prune: a container that uses an old image keeps it, and the poll does not warn about it' {
    $used = "$($script:PruneRepos.migrate):$(New-PruneSha 1)"
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine (Get-PruneMachine -Extra @{ ContainerImages = @($used, 'postgres:17') })
    Assert-Equal 'deployed' $r.Result.Outcome
    Assert-False (@(Get-RemovedRefs) -contains $used)
    Assert-Equal 26 (Get-ImageRemovals).Count
    Assert-False ($r.Log -match 'WARN')
}

Test-Case 'prune: the first run after the change cleans a big backlog 100 tags per poll, over several polls' {
    $machine = Get-PruneMachine -Count 130          # 132 tags per repository, 396 in all; the new version and the previous one are kept
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine $machine
    Assert-Equal 100 (Get-ImageRemovals).Count 'the cap holds for one poll'
    Assert-Match $r.Log 'removed 100 old image'
    Assert-Match $r.Log '(\d+) more'
    # The same machine on the next quiet polls (the fake machine forgets removed images, the recorder is reset per poll).
    $rows = $global:CmMachine.ImageRows
    $polls = @(100)
    foreach ($i in 1..4) {
        $global:CmCalls.Clear()
        $r2 = Invoke-TestDeploy -State $r.State -Machine @{ ImageRows = $rows }
        $polls += (Get-ImageRemovals).Count
        $rows = $global:CmMachine.ImageRows
        if ((Get-ImageRemovals).Count -eq 0) { break }
    }
    # Per repository 130 numbered + the previous + the new = 132 tags; the new, the previous and the 3 newest stay, so 127 go: 381 in all.
    Assert-Equal '100 100 100 81 0' ($polls -join ' ') 'the backlog is worked off 100 tags per poll'
}

Test-Case 'prune: no valid deployed commit on record means nothing is pruned' {
    foreach ($state in @{ current = @{ sha = 'not-a-sha' }; failedSha = $script:Sha2 }, @{ failedSha = $script:Sha2 }) {
        $r = Invoke-TestDeploy -State $state -Machine (Get-PruneMachine)
        Assert-Equal 0 (Get-ImageRemovals).Count
    }
}

Test-Case 'prune: it spends at most its time budget; the rest waits for the next poll' {
    $root = New-TestRoot
    try {
        New-FakeMachine (Get-PruneMachine) | Out-Null
        Initialize-Cm -Apply $true
        $ctx = New-TestContext -Root $root
        Set-TestState -Root $root -State (Get-QuietState)
        Invoke-CmImagePrune -Ctx $ctx -TimeBudgetSeconds 0 6>$null
        Assert-Equal 0 (Get-ImageRemovals).Count 'a spent budget starts no removal'
        Invoke-CmImagePrune -Ctx $ctx 6>$null
        Assert-True ((Get-ImageRemovals).Count -gt 0) 'the default budget is plenty for the fake machine'
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'prune: a dry run or -WhatIf removes nothing and the plan names the clean-up' {
    $root = New-TestRoot
    try {
        New-FakeMachine (Get-PruneMachine) | Out-Null
        Initialize-Cm -Apply $false
        $ctx = New-TestContext -Root $root
        Set-TestState -Root $root -State (Get-QuietState)
        Invoke-CmImagePrune -Ctx $ctx 6>$null
        Assert-Equal 0 (Get-ImageRemovals).Count
        $plan = (Get-CmPlan -Ctx $ctx) -join "`n"
        Assert-Match $plan 'docker image ls'
        Assert-Match $plan 'docker image rm'
        Assert-Match $plan 'CM_PRUNE_IMAGES'
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'prune: the deployer source never prunes, forces or removes anything but a single repo:tag' {
    $src = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../deployer/Cm.Deploy.ps1') -Raw
    Assert-False ($src -match '(?i)\bdocker\s+(system|volume|image|container|network)\s+prune|''prune''') 'no prune of any kind'
    Assert-False ($src -match '(?i)''(rmi|--force|-f)''') 'no rmi, no force flag'
}
