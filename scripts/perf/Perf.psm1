# The load check's building blocks (QA-2a, #170). Run-Perf.ps1 and Stop-Perf.ps1 are thin wrappers around these functions,
# and tests.ps1 tests them without starting Docker (a fake runner records the commands instead).
#
# Safety rules, all enforced here and proved in tests.ps1:
#   - the target is a throwaway stack on its own Docker network: no published port, no URL parameter, nothing to point elsewhere;
#   - every container and network carries the label cm.agent=qa-2a and a name starting cm-perf-, and cleanup is by that label;
#   - the ports 8080, 8409, 32400, 8096 (other apps on this PC) and 5480 (the live API) never appear in a docker command.

Set-StrictMode -Version Latest

# Sign-in is Auth0, and the run has no internet: a fake Auth0 runs in the throwaway network (scripts/fake-auth0), the API trusts its per-run CA
# and the runner signs one bearer token per account. Nothing in the API's settings is weakened; it is configured as in production.
. (Join-Path $PSScriptRoot '../fake-auth0/FakeAuth0.ps1')

$script:Label = 'cm.agent=qa-2a'
$script:ForbiddenPorts = @(8080, 8409, 32400, 8096, 5480)
$script:ApiPort = 5481   # inside the container only; nothing is published
$script:DockerRunner = $null   # tests install a recorder here

function Get-PerfRoot { $PSScriptRoot }

function Get-PerfConfig {
  Get-Content -LiteralPath (Join-Path $PSScriptRoot 'profiles.json') -Raw | ConvertFrom-Json
}

function Set-PerfDockerRunner([scriptblock]$Runner) { $script:DockerRunner = $Runner }

# Runs one docker command. Returns @{ Code; Output }. CM_PERF_DOCKER is a test hook for "Docker is not installed".
function Invoke-PerfDocker {
  param([Parameter(Mandatory)][string[]]$DockerArgs)
  if ($script:DockerRunner) { return & $script:DockerRunner $DockerArgs }
  $exe = if ($env:CM_PERF_DOCKER) { $env:CM_PERF_DOCKER } else { 'docker' }
  try {
    $lines = & $exe @DockerArgs 2>&1 | ForEach-Object { "$_" }
    [pscustomobject]@{ Code = $LASTEXITCODE; Output = @($lines) }
  }
  catch {
    [pscustomobject]@{ Code = 127; Output = @($_.Exception.Message) }
  }
}

function Assert-PerfDocker {
  $answer = Invoke-PerfDocker -DockerArgs @('version', '--format', '{{.Server.Version}}')
  if ($answer.Code -ne 0) { throw 'Docker is not running (or not installed). Start Docker Desktop and try again; nothing was started.' }
}

# ---- profiles ----------------------------------------------------------------------------------------------------------

function Resolve-PerfProfile {
  param([string]$Name = 'smoke')
  $config = Get-PerfConfig
  $key = "$Name".Trim()
  $known = @($config.profiles.PSObject.Properties.Name)
  if ($known -cnotcontains $key) { throw "Unknown profile '$Name'. Known profiles: $($known -join ', ')." }
  $p = $config.profiles.$key
  [pscustomobject]@{
    Name = $key; Scanners = [int]$p.scanners; Accounts = [int]$p.accounts; DurationSeconds = [int]$p.durationSeconds
    RampSeconds = [int]$p.rampSeconds; MaxMinutes = [int]$p.maxMinutes
  }
}

# The long profile never starts by accident: it needs -Profile full spelled out AND -ConfirmFull.
function Assert-PerfStartAllowed {
  param([Parameter(Mandatory)]$Profile, [switch]$ConfirmFull)
  if ($Profile.Name -eq 'full' -and -not $ConfirmFull) {
    throw "The 'full' profile runs $($Profile.Scanners) scanners for $([int]($Profile.DurationSeconds / 60)) minutes and is meant to be started on purpose. Re-run with:  -Profile full -ConfirmFull"
  }
}

# ---- the rate-limit arithmetic -------------------------------------------------------------------------------------------

# The defaults in SecurityOptions.cs (read, never changed): group name -> permits and window seconds.
function Get-PerfConfiguredLimits {
  param([string]$Path = (Join-Path $PSScriptRoot '../../src/CollectionMaxxing.Api/Security/SecurityOptions.cs'))
  $limits = @{}
  foreach ($m in [regex]::Matches((Get-Content -LiteralPath $Path -Raw), 'public GroupLimit (\w+) \{ get; set; \} = new\((\d+), (\d+)\)')) {
    $limits[$m.Groups[1].Value] = [pscustomobject]@{ Permits = [int]$m.Groups[2].Value; WindowSeconds = [int]$m.Groups[3].Value }
  }
  $limits
}

# What one account (user) sends per minute, on average, and in the worst mix at the fastest think time. The limits are per user,
# so the scanners of one account share them.
function Get-PerfRateBudget {
  param([Parameter(Mandatory)]$Profile)
  $m = (Get-PerfConfig).model
  $perAccount = [math]::Ceiling($Profile.Scanners / $Profile.Accounts)
  $meanScan = 60 / (($m.scanIntervalSeconds[0] + $m.scanIntervalSeconds[1]) / 2)
  $fastScan = 60 / $m.scanIntervalSeconds[0]
  $meanItems = ($m.itemsPerIdentify[0] + $m.itemsPerIdentify[1]) / 2
  $share = [double]$m.barcodeShare
  $others = (60 / $m.pollEverySeconds) + (60 / $m.syncEverySeconds) + (60 / $m.commitEverySeconds) * (1 + $m.acceptsPerCommit) +
  (60 / $m.browseEverySeconds) + (60 / $m.searchEverySeconds) + 1   # + token refreshes and session opens
  [pscustomobject]@{
    ScannersPerAccount = $perAccount
    DefaultPerMinute = $perAccount * ($meanScan + $others)
    SearchPerMinute = $perAccount * (60 / $m.searchEverySeconds)
    BarcodesPerMinute = $perAccount * $meanScan * $share
    IdentifyItemsPerMinute = $perAccount * $meanScan * (1 - $share) * $meanItems
    # The same, with every scan at the fastest interval: still has to be inside the limit.
    BarcodesPerMinuteFastest = $perAccount * $fastScan * $share
    IdentifyItemsPerMinuteFastest = $perAccount * $fastScan * (1 - $share) * $m.itemsPerIdentify[1]
    DefaultPerMinuteFastest = $perAccount * ($fastScan + $others)
  }
}

# ---- the plan: every docker command, built but not run -----------------------------------------------------------------------

function New-PerfRunId { (Get-Date -Format 'yyyyMMddHHmmss') + '-' + (-join ((1..3) | ForEach-Object { '0123456789abcdef'[(Get-Random -Maximum 16)] })) }

function Get-PerfImageName { param([string]$Which, [string]$Tag) "cm-perf-${Which}:$Tag" }

function Get-PerfBuildCommands {
  param([Parameter(Mandatory)][string]$Tag, [Parameter(Mandatory)][string]$RepoRoot)
  @(
    [pscustomobject]@{ Step = 'build api image'; Args = @('build', '-f', 'deploy/docker/Dockerfile.api', '--label', $script:Label, '-t', (Get-PerfImageName 'api' $Tag), '.'); WorkDir = $RepoRoot }
    [pscustomobject]@{ Step = 'build migrate image'; Args = @('build', '-f', 'deploy/docker/Dockerfile.migrate', '--label', $script:Label, '-t', (Get-PerfImageName 'migrate' $Tag), '.'); WorkDir = $RepoRoot }
  )
}

# The placeholder {{DB_PASSWORD}} is filled in when the command really runs and never printed.
function Get-PerfPlan {
  param(
    [Parameter(Mandatory)]$Profile, [Parameter(Mandatory)][string]$RunId, [Parameter(Mandatory)][string]$OutDir,
    [Parameter(Mandatory)][string]$Auth0Dir, [string]$Tag = 'dev'
  )
  $stack = (Get-PerfConfig).stack
  $net = "cm-perf-$RunId"
  $db = "$net-db"; $api = "$net-api"; $migrate = "$net-migrate"; $k6 = "$net-k6"; $auth0 = "$net-auth0"
  $labels = @('--label', $script:Label, '--label', "cm.run=$RunId")
  $hardening = @('--security-opt', 'no-new-privileges:true')
  $connection = "Host=$db;Database=collectionmaxxing;Username=collectionmaxxing;Password={{DB_PASSWORD}}"
  $apiUrl = "http://${api}:$script:ApiPort"
  $apiEnv = @{
    ASPNETCORE_HTTP_PORTS = "$script:ApiPort"
    'ConnectionStrings__collectionmaxxing' = $connection
    Server__PublicUrl = 'https://pc.tailnet.ts.net'
    Auth__KeyRing__Path = '/data/files/keyring'
    Catalog__CoversRoot = '/data/files/covers'
    Files__Root = '/data/files'
    Exports__Path = '/data/files/exports'
    Evals__Enabled = 'false'
    # Providers are refused at once (see $providerHosts); with the default 3 retries and 1 s delay a barcode would wait out its 2 s window and a search 5 s,
    # which measures the retry policy of a down provider, not the API. One quick retry keeps the numbers about our code.
    Providers__Resilience__MaxRetryAttempts = '1'
    Providers__Resilience__RetryDelay = '00:00:00.050'
    # The providers' own client-side rate limits (UPCitemdb: one call per 10 s) are for the real services; against refused calls they only
    # make requests queue until the 2 s resolve window ends, so they are lifted here.
    Providers__UpcItemDb__RequestsPerSecond = '1000'
    Providers__UpcItemDb__DailyLimit = '1000000'
    Providers__OpenLibrary__RequestsPerSecond = '1000'
    # Without a contact address Open Library is held to one call a second whatever its rate says; with the fake address the lifted rate applies.
    Providers__OpenLibrary__ContactEmail = 'perf@example.test'
    # The seed makes every account, library and invite from one address, which the per-address limits (10 a minute, 30 an hour) could refuse.
    # Only these two are widened; the limits the scanners live under (default, search, barcodes, identify) are the shipped ones.
    Auth__RateLimit__PermitLimit = '10000'
    Security__Limits__AuthHourly__Permits = '100000'
  }
  # The providers' host names point at the container itself: a lookup is refused at once instead of timing out for 5 s per attempt, so no
  # request ever leaves the machine and the numbers measure our API and database, not a provider's latency.
  $providerHosts = @('api.igdb.com', 'api.themoviedb.org', 'api.upcitemdb.com', 'covers.openlibrary.org', 'id.twitch.tv', 'image.tmdb.org', 'images.igdb.com', 'openlibrary.org', 'www.googleapis.com') | ForEach-Object { '--add-host'; "$_`:127.0.0.1" }
  foreach ($pair in (Get-FakeAuth0ApiEnvironment).GetEnumerator()) { $apiEnv[$pair.Key] = $pair.Value }
  $apiEnvArgs = $apiEnv.GetEnumerator() | Sort-Object Name | ForEach-Object { '-e'; "$($_.Name)=$($_.Value)" }

  $commands = @(
    [pscustomobject]@{ Step = 'network'; Args = @('network', 'create', '--internal') + $labels + @($net) }
    [pscustomobject]@{
      Step = 'database'
      Args = @('run', '-d', '--name', $db, '--network', $net) + $labels + $hardening + @(
        '--cpus', $stack.dbCpus, '--memory', $stack.dbMemory, '--tmpfs', '/var/lib/postgresql/data:rw,size=256m',
        '-e', 'POSTGRES_DB=collectionmaxxing', '-e', 'POSTGRES_USER=collectionmaxxing', '-e', 'POSTGRES_PASSWORD={{DB_PASSWORD}}', 'postgres:17')
    }
    [pscustomobject]@{
      Step = 'migrate'
      Args = @('run', '--rm', '--name', $migrate, '--network', $net) + $labels + $hardening + @(
        '-e', "ConnectionStrings__collectionmaxxing=$connection", (Get-PerfImageName 'migrate' $Tag))
    }
    [pscustomobject]@{ Step = 'auth0'; Args = @('run', '-d') + $labels + (Get-FakeAuth0ContainerArgs -Name $auth0 -Network $net -Directory $Auth0Dir) }
    [pscustomobject]@{
      Step = 'api'
      Args = @('run', '-d', '--name', $api, '--network', $net) + $labels + $hardening + @(
        '--cap-drop', 'ALL', '--cpus', $stack.apiCpus, '--memory', $stack.apiMemory,
        '--tmpfs', '/data/files:rw,size=128m,uid=1654,gid=1654,mode=0755') + $providerHosts +
      (Get-FakeAuth0ApiMount -Directory $Auth0Dir) + $apiEnvArgs + @((Get-PerfImageName 'api' $Tag))
    }
    [pscustomobject]@{
      Step = 'k6'
      Args = @('run', '--rm', '--name', $k6, '--network', $net) + $labels + $hardening + @(
        '--cpus', $stack.k6Cpus, '--memory', $stack.k6Memory,
        '--mount', "type=bind,source=$(Get-PerfRoot),target=/scripts,readonly",
        '--mount', "type=bind,source=$OutDir,target=/out",
        '-e', "PROFILE=$($Profile.Name)", '-e', "BASE_URL=$apiUrl",
        'grafana/k6:1.0.0', 'run', '/scripts/k6/scanners.js')
    }
  )
  [pscustomobject]@{ RunId = $RunId; Network = $net; Containers = @($db, $api, $migrate, $k6, $auth0); ApiUrl = $apiUrl; Profile = $Profile; Commands = $commands; Tag = $Tag }
}

function Expand-PerfArgs {
  param([string[]]$CommandArgs, [hashtable]$Secrets)
  $CommandArgs | ForEach-Object { $a = $_; foreach ($k in $Secrets.Keys) { $a = $a.Replace("{{$k}}", $Secrets[$k]) }; $a }
}

# Fails when any docker command publishes a port or mentions a port that belongs to something else.
function Test-PerfPlanSafe {
  param([Parameter(Mandatory)]$Plan, $BuildCommands = @())
  $problems = [System.Collections.Generic.List[string]]::new()
  foreach ($c in @($Plan.Commands) + @($BuildCommands)) {
    $line = $c.Args -join ' '
    if ($c.Args -contains '-p' -or $c.Args -contains '--publish' -or $c.Args -contains '-P' -or $c.Args -contains '--publish-all' -or $line -match '--publish[= ]|(^| )-p[ =]?\d') {
      $problems.Add("$($c.Step) publishes a port")
    }
    foreach ($port in $script:ForbiddenPorts) {
      if ($line -match "(?<!\d)$port(?!\d)") { $problems.Add("$($c.Step) mentions the forbidden port $port") }
    }
    if ($c.Step -ne 'build api image' -and $c.Step -ne 'build migrate image') {
      if ($c.Args -notcontains $script:Label) { $problems.Add("$($c.Step) is not labelled $script:Label") }
      $name = $null
      $i = [array]::IndexOf($c.Args, '--name'); if ($i -ge 0) { $name = $c.Args[$i + 1] }
      if ($name -and $name -notlike 'cm-perf-*') { $problems.Add("$($c.Step) is named $name, not cm-perf-*") }
    }
    if ($line -match '--network[= ]host|--privileged|docker\.sock') { $problems.Add("$($c.Step) uses host networking, privileges or the Docker socket") }
  }
  $problems
}

function Format-PerfPlan {
  param([Parameter(Mandatory)]$Plan, $BuildCommands = @())
  $p = $Plan.Profile
  $budget = Get-PerfRateBudget -Profile $p
  $cfg = Get-PerfConfig
  $s = $cfg.stack
  $lines = @(
    "profile:      $($p.Name)",
    "users:        $($p.Scanners) scanners (paired devices) on $($p.Accounts) accounts, about $($budget.ScannersPerAccount) per account",
    "duration:     $($p.DurationSeconds) s including a $($p.RampSeconds) s ramp (hard cap $($p.MaxMinutes) min, change with -MaxMinutes)",
    "limits:       api $($s.apiCpus) cpu / $($s.apiMemory), database $($s.dbCpus) cpu / $($s.dbMemory) on tmpfs, k6 $($s.k6Cpus) cpu / $($s.k6Memory)",
    "thresholds:   p95 under $($cfg.thresholds.p95Ms) ms, errors under $([double]$cfg.thresholds.errorRate * 100)%",
    "abort:        p95 over $($cfg.abort.p95Ms) ms for $($cfg.abort.forSeconds) s, or errors over $([double]$cfg.abort.errorRate * 100)%",
    "network:      $($Plan.Network) (internal: no published ports, no way out to the internet, so no provider is ever called)",
    "target:       $($Plan.ApiUrl) (inside that network only; never the live stack)",
    "stop:        Ctrl+C here, or  pwsh -NoProfile -File scripts/perf/Stop-Perf.ps1  from any other window"
  )
  $lines += 'docker commands (secrets shown as {{...}}):'
  foreach ($c in @($BuildCommands) + @($Plan.Commands)) { $lines += "  docker $($c.Args -join ' ')" }
  $lines
}

# ---- stopping --------------------------------------------------------------------------------------------------------------

# Removes exactly the containers and networks that carry the label (and, with -RunId, that run's). Anything else is left alone.
function Stop-PerfResources {
  param([string]$RunId, [switch]$RemoveImages)
  # CM_PERF_LABEL is a test hook so the self-test in tests.ps1 never touches a real run's containers.
  $stopLabel = if ($env:CM_PERF_LABEL) { $env:CM_PERF_LABEL } else { $script:Label }
  $filter = @('--filter', "label=$stopLabel")
  if ($RunId) { $filter += @('--filter', "label=cm.run=$RunId") }
  $ids = @((Invoke-PerfDocker -DockerArgs (@('ps', '-a', '-q') + $filter)).Output | Where-Object { $_ -match '^[0-9a-f]{6,}$' })
  if ($ids.Count -gt 0) { [void](Invoke-PerfDocker -DockerArgs (@('rm', '-f', '-v') + $ids)) }
  $nets = @((Invoke-PerfDocker -DockerArgs (@('network', 'ls', '-q') + $filter)).Output | Where-Object { $_ -match '^[0-9a-f]{6,}$' })
  if ($nets.Count -gt 0) { [void](Invoke-PerfDocker -DockerArgs (@('network', 'rm') + $nets)) }
  if ($RemoveImages) {
    $images = @((Invoke-PerfDocker -DockerArgs @('image', 'ls', '-q', '--filter', "label=$stopLabel")).Output | Where-Object { $_ -match '^[0-9a-f]{6,}$' } | Select-Object -Unique)
    if ($images.Count -gt 0) { [void](Invoke-PerfDocker -DockerArgs (@('image', 'rm') + $images)) }
  }
  [pscustomobject]@{ Containers = $ids.Count; Networks = $nets.Count }
}

# ---- the fake Auth0 and the accounts' tokens ----------------------------------------------------------------------------------------

# The people of a run: one account per profile account. The first is the owner of the load library.
function Get-PerfAccounts {
  param([Parameter(Mandatory)][int]$Count)
  @(1..$Count | ForEach-Object { [pscustomobject]@{ Sub = "auth0|cm-perf-user-$_"; Name = "Load user $_"; Email = "load$_@example.test" } })
}

# Makes the fake Auth0's keys and certificates in $Auth0Dir, and writes one bearer token per account to $OutDir/tokens.json (read by k6, deleted by
# Remove-PerfAuth0Files). Returns the signing material.
function New-PerfAuth0 {
  param([Parameter(Mandatory)][string]$Auth0Dir, [Parameter(Mandatory)][string]$OutDir, [Parameter(Mandatory)]$Profile)
  $accounts = Get-PerfAccounts -Count $Profile.Accounts
  $material = New-FakeAuth0Material -Directory $Auth0Dir -Users $accounts
  New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
  $tokens = @($accounts | ForEach-Object { New-FakeAuth0Token -Material $material -Subject $_.Sub })
  # The owner's sub travels too: the seed signs the owner in on the web (an invite needs a recent sign-in, which a bearer token never has).
  @{ tokens = $tokens; ownerSub = $accounts[0].Sub } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutDir 'tokens.json')
  $material
}

function Remove-PerfAuth0Files {
  param([string]$Auth0Dir, [string]$OutDir)
  if ($Auth0Dir) { Remove-Item -LiteralPath $Auth0Dir -Recurse -Force -ErrorAction SilentlyContinue }
  if ($OutDir) { Remove-Item -LiteralPath (Join-Path $OutDir 'tokens.json') -Force -ErrorAction SilentlyContinue }
}

function New-PerfPassword { -join ((1..24) | ForEach-Object { 'abcdefghijkmnpqrstuvwxyz23456789'[(Get-Random -Maximum 32)] }) }

# ---- the result ----------------------------------------------------------------------------------------------------------------

# "12.5%" and "310MiB / 1GiB" from `docker stats --format '{{json .}}'` lines, folded into per-container peaks and averages.
function Get-PerfStatsSummary {
  param([object[]]$Samples)
  $byName = @{}
  foreach ($s in $Samples) {
    $name = ($s.Name -replace '^cm-perf-[0-9a-f-]+-', '')
    $cpu = [double](($s.CPUPerc -replace '%', '') -replace ',', '.')
    $used = ($s.MemUsage -split '/')[0].Trim()
    $mb = if ($used -match '^([\d.]+)\s*(B|KiB|MiB|GiB)') { [double]$Matches[1] * @{ B = 1e-6; KiB = 1 / 1024; MiB = 1; GiB = 1024 }[$Matches[2]] } else { 0 }
    if (-not $byName.ContainsKey($name)) { $byName[$name] = [System.Collections.Generic.List[object]]::new() }
    $byName[$name].Add([pscustomobject]@{ Cpu = $cpu; Mb = $mb })
  }
  $out = [ordered]@{}
  foreach ($name in ($byName.Keys | Sort-Object)) {
    $rows = $byName[$name]
    $out[$name] = [pscustomobject]@{
      samples = $rows.Count
      cpuAvgPercent = [math]::Round(($rows | Measure-Object Cpu -Average).Average, 1)
      cpuMaxPercent = [math]::Round(($rows | Measure-Object Cpu -Maximum).Maximum, 1)
      memMaxMiB = [math]::Round(($rows | Measure-Object Mb -Maximum).Maximum, 0)
    }
  }
  [pscustomobject]$out
}

Export-ModuleMember -Function Get-PerfConfig, Set-PerfDockerRunner, Invoke-PerfDocker, Assert-PerfDocker, Resolve-PerfProfile, Assert-PerfStartAllowed,
Get-PerfConfiguredLimits, Get-PerfRateBudget, New-PerfRunId, Get-PerfBuildCommands, Get-PerfPlan, Expand-PerfArgs, Test-PerfPlanSafe,
Format-PerfPlan, Stop-PerfResources, Get-PerfAccounts, New-PerfAuth0, Remove-PerfAuth0Files, New-PerfPassword, Get-PerfStatsSummary, Get-PerfImageName
