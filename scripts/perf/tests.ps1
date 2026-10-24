<#
.SYNOPSIS
  Tests for the load-check scripts (scripts/perf). No packages: a tiny runner, plain asserts.

  Run:  pwsh -NoProfile -File scripts/perf/tests.ps1        (exits non-zero if any test fails)

  Nothing here starts the load check. The runner is exercised with -DryRun or with a Docker that is "off"; the stop script is
  tested against a recorder, and once for real with two throwaway containers that carry a TEST label (cm.agent=qa-2a-selftest, so a
  real load run is never touched). The real part needs a Docker daemon and an image with `sleep` (grafana/k6); -SkipDocker leaves it out.
#>
[CmdletBinding()]
param([switch]$SkipDocker)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here '../..')).Path
Import-Module (Join-Path $here 'Perf.psm1') -Force

$script:failures = 0
$script:count = 0
$script:skipped = 0
$pwsh = (Get-Process -Id $PID).Path

function It([string]$Name, [scriptblock]$Body) {
  $script:count++
  try { & $Body; Write-Host "ok   $Name" }
  catch { $script:failures++; Write-Host "FAIL $Name"; Write-Host "     $($_.Exception.Message)" }
}

function Should-BeTrue($Condition, [string]$What) { if (-not $Condition) { throw "expected: $What" } }
function Should-Be($Actual, $Expected, [string]$What = 'value') { if ("$Actual" -cne "$Expected") { throw "$What : expected <$Expected> but got <$Actual>" } }

# Runs a script in a fresh pwsh with extra environment variables; returns the exit code and the whole output.
function Invoke-Script([string]$Script, [string[]]$Arguments, [hashtable]$Env = @{}) {
  $saved = @{}
  foreach ($k in $Env.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $Env[$k]) }
  try {
    $text = & $pwsh -NoProfile -File (Join-Path $here $Script) @Arguments 2>&1 | ForEach-Object { "$_" }
    [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($text -join "`n") }
  }
  finally { foreach ($k in $Env.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) } }
}

# A recorder that stands in for Docker: remembers every command and answers from a table.
function Use-FakeDocker([scriptblock]$Answer = { param($a) @() }) {
  $script:calls = [System.Collections.Generic.List[string]]::new()
  $capture = $script:calls
  Set-PerfDockerRunner {
    param($a)
    $capture.Add($a -join ' ')
    [pscustomobject]@{ Code = 0; Output = @(& $Answer $a) }
  }.GetNewClosure()
}

function Get-TestPlan([string]$Name = 'smoke') {
  $p = Resolve-PerfProfile -Name $Name
  Get-PerfPlan -Profile $p -RunId '20261018120000-abc' -OutDir 'C:\temp\cm\out' -Auth0Dir 'C:\temp\cm\fake-auth0' -Tag 'test'
}

# ---- the runner's guards ---------------------------------------------------------------------------------------------
It 'the runner refuses an unknown profile' {
  $r = Invoke-Script 'Run-Perf.ps1' @('-Profile', 'turbo', '-DryRun')
  Should-BeTrue ($r.Code -ne 0) 'a non-zero exit code'
  Should-BeTrue ($r.Text -match "Unknown profile 'turbo'") "the message names the profile: $($r.Text)"
  Should-BeTrue ($r.Text -notmatch 'dry run') 'it stopped before the plan'
}

It 'the profile name is matched exactly (no FULL, no spaces trick to skip the flag)' {
  Should-Be (Resolve-PerfProfile -Name 'smoke').Name 'smoke' 'smoke resolves'
  $threw = $false; try { Resolve-PerfProfile -Name 'FULL' | Out-Null } catch { $threw = $true }
  Should-BeTrue $threw 'FULL is not a profile'
}

It 'the default profile is smoke' {
  $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $here 'Run-Perf.ps1'), [ref]$null, [ref]$null)
  $param = $ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Profile' }
  Should-Be $param.DefaultValue.Value 'smoke' 'default of -Profile'
  $r = Invoke-Script 'Run-Perf.ps1' @('-DryRun')
  Should-BeTrue ($r.Text -match 'profile:\s+smoke') 'a bare dry run plans smoke'
}

It 'full does not start without -ConfirmFull, and does with it' {
  $r = Invoke-Script 'Run-Perf.ps1' @('-Profile', 'full', '-DryRun')
  Should-BeTrue ($r.Code -ne 0) 'a non-zero exit code'
  Should-BeTrue ($r.Text -match 'ConfirmFull') "it says what to add: $($r.Text)"
  Should-BeTrue ($r.Text -notmatch 'dry run: nothing was started') 'no plan was accepted'
  $ok = Invoke-Script 'Run-Perf.ps1' @('-Profile', 'full', '-ConfirmFull', '-DryRun')
  Should-Be $ok.Code 0 'exit code with the flag'
}

It 'the runner refuses when Docker is off, before starting anything' {
  $r = Invoke-Script 'Run-Perf.ps1' @() @{ CM_PERF_DOCKER = 'cm-perf-no-such-docker' }
  Should-BeTrue ($r.Code -ne 0) 'a non-zero exit code'
  Should-BeTrue ($r.Text -match 'refused: Docker is not running') "the reason is Docker: $($r.Text)"
  Should-BeTrue ($r.Text -notmatch 'ok   ') 'no step ran'
}

# ---- safety of the generated commands ---------------------------------------------------------------------------------------
foreach ($pn in @('smoke', 'gaming', 'full')) {
  It "the $pn plan publishes no port, uses no forbidden port and labels and names everything" {
    $plan = Get-TestPlan $pn
    $build = Get-PerfBuildCommands -Tag 'test' -RepoRoot $repo
    $problems = @(Test-PerfPlanSafe -Plan $plan -BuildCommands $build)
    Should-Be ($problems -join '; ') '' 'problems'
    $all = (@($plan.Commands) + @($build) | ForEach-Object { $_.Args }) -join ' '
    foreach ($port in 8080, 8409, 32400, 8096, 5480) { Should-BeTrue ($all -notmatch "(?<!\d)$port(?!\d)") "port $port does not appear" }
    Should-BeTrue ($all -notmatch '(^| )(-p|-P|--publish|--publish-all)( |=)') 'no publish flag'
    Should-BeTrue ($all -match '--internal') 'the network is internal'
    Should-BeTrue ($plan.Commands[-1].Args -contains '--cpus' -and $plan.Commands[-1].Args[$plan.Commands[-1].Args.IndexOf('--cpus') + 1] -eq '2') 'k6 runs with --cpus 2'
    foreach ($c in $plan.Commands) { Should-BeTrue ($c.Args -contains 'cm.agent=qa-2a') "$($c.Step) carries the label" }
  }
}

It 'the safety check really catches a published port, a forbidden port and an unlabelled container' {
  $plan = Get-TestPlan
  $bad = [pscustomobject]@{ Commands = @(
      [pscustomobject]@{ Step = 'a'; Args = @('run', '-p', '127.0.0.1:8080:80', 'x') },
      [pscustomobject]@{ Step = 'b'; Args = @('run', '--name', 'plex', '-e', 'PORT=32400', 'x') }) }
  $problems = @(Test-PerfPlanSafe -Plan $bad) -join '; '
  Should-BeTrue ($problems -match 'a publishes a port') "published port: $problems"
  Should-BeTrue ($problems -match '8080') "forbidden port 8080: $problems"
  Should-BeTrue ($problems -match '32400') "forbidden port 32400: $problems"
  Should-BeTrue ($problems -match 'b is not labelled') "unlabelled: $problems"
  Should-BeTrue ($problems -match 'b is named plex') "bad name: $problems"
}

It 'the target is only the throwaway API inside the private network' {
  $plan = Get-TestPlan
  Should-BeTrue ($plan.ApiUrl -match '^http://cm-perf-[0-9a-f-]+-api:5481$') "api url: $($plan.ApiUrl)"
  $script = Get-Content -LiteralPath (Join-Path $here 'k6/scanners.js') -Raw
  Should-BeTrue ($script -notmatch "https?://(?!\$\{)") 'the k6 scripts contain no URL of their own (BASE_URL comes from the environment)'
  $runner = Get-Content -LiteralPath (Join-Path $here 'Run-Perf.ps1') -Raw
  $ast = [System.Management.Automation.Language.Parser]::ParseInput($runner, [ref]$null, [ref]$null)
  $names = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
  Should-BeTrue (-not ($names | Where-Object { $_ -match 'url|uri|host|target|server|port' })) "the runner has no URL-like parameter: $($names -join ', ')"
}

# ---- the dry run ------------------------------------------------------------------------------------------------------------
It 'the dry run prints the plan and starts nothing' {
  Use-FakeDocker
  $plan = Get-TestPlan 'gaming'
  $text = (Format-PerfPlan -Plan $plan) -join "`n"
  Should-Be $script:calls.Count 0 'docker calls made while planning'
  foreach ($needle in 'profile:\s+gaming', '5 scanners', '2 accounts', '180 s', '2 cpu', '1g', 'p95 under 500 ms', 'errors under 1%', 'p95 over 2000 ms for 30 s', 'errors over 10%', 'Stop-Perf.ps1', 'no published ports') {
    Should-BeTrue ($text -match $needle) "the plan mentions '$needle'"
  }
  Set-PerfDockerRunner $null
  $r = Invoke-Script 'Run-Perf.ps1' @('-Profile', 'gaming', '-DryRun') @{ CM_PERF_DOCKER = 'cm-perf-no-such-docker' }
  Should-Be $r.Code 0 'exit code of a dry run with Docker off (it never asks Docker)'
  Should-BeTrue ($r.Text -match 'dry run: nothing was started') 'it says nothing was started'
  Should-BeTrue ($r.Text -notmatch '(?m)^ok   ') 'no step ran'
}

It 'secrets are placeholders in the plan' {
  $text = (Format-PerfPlan -Plan (Get-TestPlan)) -join "`n"
  Should-BeTrue ($text -match '\{\{DB_PASSWORD\}\}') 'the database password is a placeholder, not a value'
  Should-BeTrue ($text -notmatch 'SETUP_CODE|setup-code') 'there is no setup code any more'
}

# ---- the fake Auth0 --------------------------------------------------------------------------------------------------------------
It 'the plan starts a hardened fake Auth0 on the internal network before the API, with the alias its certificate names' {
  $plan = Get-TestPlan
  $steps = @($plan.Commands | ForEach-Object { $_.Step })
  Should-Be ($steps -join ',') 'network,database,migrate,auth0,api,k6' 'the order of the steps'
  $fake = ($plan.Commands | Where-Object Step -eq 'auth0').Args
  $line = $fake -join ' '
  Should-BeTrue ($line -match '--name cm-perf-[0-9a-f-]+-auth0 ') 'named cm-perf-*'
  Should-BeTrue ($line -match '--network cm-perf-[0-9a-f-]+ ') 'on the private network'
  Should-BeTrue ($line -match '--network-alias fake-auth0') 'the alias the certificate is for'
  Should-BeTrue ($fake -contains 'cm.agent=qa-2a') 'labelled'
  Should-BeTrue ($line -match '--read-only' -and $line -match '--cap-drop ALL' -and $line -match '--user 65534') 'hardened'
  Should-BeTrue ($line -match 'python:3\.13-alpine python -u /fake/server\.py') 'a public image running our server.py'
  Should-Be $plan.Containers[4] ($fake[$fake.IndexOf('--name') + 1]) 'the fake is the fifth container of the plan'
}

It 'the API is configured like production against the fake, trusts only its CA, and gets no key and no weakening switch' {
  $plan = Get-TestPlan
  $api = ($plan.Commands | Where-Object Step -eq 'api').Args
  $line = $api -join ' '
  foreach ($setting in 'Auth__Auth0__Domain=fake-auth0:8443', 'Auth__Auth0__Audience=urn:collectionmaxxing:api', 'SSL_CERT_FILE=/fake-auth0-ca/ca.pem', 'Auth__PublicOrigin=https://') {
    Should-BeTrue ($line -match [regex]::Escape($setting)) "the API gets $setting"
  }
  Should-BeTrue ($line -match 'fake-auth0.ca,target=/fake-auth0-ca,readonly') 'only the ca folder is mounted, read-only'
  Should-BeTrue ($line -notmatch 'target=/fake,|server\.key|signing\.json|/secrets') 'no key, no signing material, no secrets folder'
  Should-BeTrue ($line -notmatch 'Insecure|DisableHttps|RequireHttps|ValidateIssuer|ValidateAudience|SkipCert|Unsafe') 'no setting that weakens TLS or issuer checks'
  Should-BeTrue ($line -notmatch 'RelyingParty|Auth__Keys|SetupCode') 'none of the removed sign-in settings'
  $k6 = ($plan.Commands | Where-Object Step -eq 'k6').Args -join ' '
  Should-BeTrue ($k6 -notmatch 'SETUP_CODE|RP_ID|ORIGIN') 'k6 gets no setup code and no passkey origin'
}

It 'each account gets a signed token and the owner is the first; the tokens file and the keys are removed after the run' {
  $out = Join-Path ([System.IO.Path]::GetTempPath()) "cm-perf-test-$([guid]::NewGuid().ToString('N'))"
  $dir = Join-Path $out 'fake-auth0'
  try {
    $profile = Resolve-PerfProfile 'full'
    [void](New-PerfAuth0 -Auth0Dir $dir -OutDir $out -Profile $profile)
    $file = Get-Content -LiteralPath (Join-Path $out 'tokens.json') -Raw | ConvertFrom-Json
    Should-Be @($file.tokens).Count $profile.Accounts 'one token per account'
    Should-Be $file.ownerSub 'auth0|cm-perf-user-1' 'the owner is the first account'
    $subs = foreach ($t in $file.tokens) {
      $payload = $t.Split('.')[1].Replace('-', '+').Replace('_', '/'); while ($payload.Length % 4) { $payload += '=' }
      $claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
      Should-Be $claims.iss 'https://fake-auth0:8443/' 'issuer'
      Should-Be $claims.aud 'urn:collectionmaxxing:api' 'audience'
      Should-BeTrue ($claims.exp - $claims.iat -ge 3600) 'a token outlives any profile'
      $claims.sub
    }
    Should-Be (@($subs | Select-Object -Unique).Count) $profile.Accounts 'every account has its own sub'
    Should-Be ((Get-Content -LiteralPath (Join-Path $dir 'users.json') -Raw | ConvertFrom-Json).PSObject.Properties.Name.Count) $profile.Accounts 'the fake knows every account'
    Should-BeTrue (Test-Path -LiteralPath (Join-Path $dir 'ca/ca.pem')) 'the CA certificate is there for the API'
    Remove-PerfAuth0Files -Auth0Dir $dir -OutDir $out
    Should-BeTrue (-not (Test-Path -LiteralPath $dir)) 'the fake Auth0 folder is gone'
    Should-BeTrue (-not (Test-Path -LiteralPath (Join-Path $out 'tokens.json'))) 'the tokens file is gone'
  }
  finally { Remove-Item -LiteralPath $out -Recurse -Force -ErrorAction SilentlyContinue }
}

It 'the k6 seed uses the new flow: web sign-in for the owner, library, invite, bearer first call and accept; none of the old sign-in' {
  $js = Get-Content -LiteralPath (Join-Path $here 'k6/scanners.js') -Raw
  foreach ($needle in '/account/login', '/signin-oidc', "'/api/v1/libraries'", '/invites`', "'/api/v1/invites:accept'", "'/api/v1/me'", 'invite.code', '/out/tokens.json') {
    Should-BeTrue ($js.Contains($needle)) "scanners.js mentions $needle"
  }
  foreach ($old in "'/setup'", 'connect/token', 'passkey', 'pairing', 'webauthn', 'SETUP_CODE', 'refresh_token') {
    Should-BeTrue (-not $js.Contains($old)) "scanners.js no longer mentions $old"
  }
  Should-BeTrue (-not (Test-Path -LiteralPath (Join-Path $here 'k6/webauthn.js'))) 'the software authenticator is gone'
}

# ---- the profiles and the k6 script -------------------------------------------------------------------------------------------
It 'the profiles are the ones in the brief' {
  $s = Resolve-PerfProfile 'smoke'; $g = Resolve-PerfProfile 'gaming'; $f = Resolve-PerfProfile 'full'
  Should-Be "$($s.Scanners)/$($s.DurationSeconds)" '2/30' 'smoke'
  Should-Be "$($g.Scanners)/$($g.DurationSeconds)" '5/180' 'gaming'
  Should-Be "$($f.Scanners)/$($f.DurationSeconds)/$($f.RampSeconds)" '20/600/60' 'full'
}

It 'the k6 script has the thresholds and the abort rules' {
  $js = Get-Content -LiteralPath (Join-Path $here 'k6/scanners.js') -Raw
  $cfg = Get-PerfConfig
  Should-Be $cfg.thresholds.p95Ms 500 'p95 threshold (ms)'
  Should-Be $cfg.thresholds.errorRate 0.01 'error threshold'
  Should-Be $cfg.abort.p95Ms 2000 'abort p95 (ms)'
  Should-Be $cfg.abort.forSeconds 30 'abort window (s)'
  Should-Be $cfg.abort.errorRate 0.1 'abort error rate'
  Should-BeTrue ($js -match 'p\(95\)<\$\{config\.thresholds\.p95Ms\}') 'a p95 threshold from the config'
  Should-BeTrue ($js -match 'rate<\$\{config\.thresholds\.errorRate\}') 'an error-rate threshold from the config'
  Should-BeTrue ($js -match 'p\(95\)<\$\{config\.abort\.p95Ms\}[^}]*abortOnFail: true') 'the p95 abort rule aborts'
  Should-BeTrue ($js -match 'rate<\$\{config\.abort\.errorRate\}[^}]*abortOnFail: true') 'the error abort rule aborts'
  Should-BeTrue (([regex]::Matches($js, 'delayAbortEval: `\$\{config\.abort\.forSeconds\}s`')).Count -eq 2) 'both abort rules wait the abort window'
  Should-BeTrue ($js -match 'expectedStatuses\(\{ min: 200, max: 399 \}, 429\)') '429 is expected traffic, counted apart'
  Should-BeTrue ($js -match "new Counter\('rate_limited'\)") 'there is a 429 counter'
}

It 'the scanner mix stays inside the rate limits' {
  $limits = Get-PerfConfiguredLimits
  Should-Be $limits['Default'].Permits 600 'default limit read from SecurityOptions.cs'
  Should-Be $limits['Search'].Permits 60 'search limit'
  Should-Be $limits['Barcodes'].Permits 120 'barcodes limit'
  Should-Be $limits['Identify'].Permits 500 'identify limit'
  foreach ($pn in @('smoke', 'gaming', 'full')) {
    $b = Get-PerfRateBudget -Profile (Resolve-PerfProfile $pn)
    # On average at most 70% of every limit; at the fastest think time (every scan 2 s apart) still at or under the limit.
    Should-BeTrue ($b.DefaultPerMinute -le 0.7 * $limits['Default'].Permits) "$pn default/min $($b.DefaultPerMinute) vs $($limits['Default'].Permits)"
    Should-BeTrue ($b.SearchPerMinute -le 0.7 * $limits['Search'].Permits) "$pn search/min $($b.SearchPerMinute) vs $($limits['Search'].Permits)"
    Should-BeTrue ($b.BarcodesPerMinute -le 0.7 * $limits['Barcodes'].Permits) "$pn barcodes/min $($b.BarcodesPerMinute) vs $($limits['Barcodes'].Permits)"
    Should-BeTrue ($b.IdentifyItemsPerMinute -le 0.7 * $limits['Identify'].Permits) "$pn identify items/min $($b.IdentifyItemsPerMinute) vs $($limits['Identify'].Permits)"
    Should-BeTrue ($b.DefaultPerMinuteFastest -le $limits['Default'].Permits) "$pn fastest default/min $($b.DefaultPerMinuteFastest)"
    Should-BeTrue ($b.BarcodesPerMinuteFastest -le $limits['Barcodes'].Permits) "$pn fastest barcodes/min $($b.BarcodesPerMinuteFastest)"
    Should-BeTrue ($b.IdentifyItemsPerMinuteFastest -le $limits['Identify'].Permits) "$pn fastest identify items/min $($b.IdentifyItemsPerMinuteFastest)"
  }
}

It 'a profile that crowds too many scanners onto one account would fail the arithmetic' {
  $crowded = [pscustomobject]@{ Name = 'x'; Scanners = 20; Accounts = 1 }
  $b = Get-PerfRateBudget -Profile $crowded
  Should-BeTrue ($b.BarcodesPerMinute -gt 0.7 * (Get-PerfConfiguredLimits)['Barcodes'].Permits) 'the check has teeth'
}

# ---- stopping ------------------------------------------------------------------------------------------------------------------
It 'Stop-PerfResources asks Docker for labelled things only and removes exactly those' {
  Use-FakeDocker {
    param($a)
    if ($a[0] -eq 'ps') { return @('aaaaaaaaaaaa', 'bbbbbbbbbbbb') }
    if ($a[0] -eq 'network' -and $a[1] -eq 'ls') { return @('cccccccccccc') }
  }
  $gone = Stop-PerfResources
  Set-PerfDockerRunner $null
  Should-Be $gone.Containers 2 'containers removed'
  Should-Be $gone.Networks 1 'networks removed'
  $list = @($script:calls | Where-Object { $_ -match '^(ps|network ls)' })
  Should-Be $list.Count 2 'list calls'
  foreach ($l in $list) { Should-BeTrue ($l -match '--filter label=cm\.agent=qa-2a') "the listing is filtered by the label: $l" }
  $removals = @($script:calls | Where-Object { $_ -match '^(rm|network rm)' })
  Should-Be ($removals -join ' | ') 'rm -f -v aaaaaaaaaaaa bbbbbbbbbbbb | network rm cccccccccccc' 'removal calls'
  Should-BeTrue (-not ($script:calls | Where-Object { $_ -match 'prune|system|volume|down' })) 'no prune, system or volume command'
}

It 'Stop-PerfResources removes nothing when nothing is labelled' {
  Use-FakeDocker { param($a) @() }
  $gone = Stop-PerfResources
  Set-PerfDockerRunner $null
  Should-Be $gone.Containers 0 'containers'
  Should-BeTrue (-not ($script:calls | Where-Object { $_ -match '^(rm|network rm)' })) 'no removal call'
}

It 'the stop script ends with the word stopped' {
  $r = Invoke-Script 'Stop-Perf.ps1' @() @{ CM_PERF_DOCKER = 'cm-perf-no-such-docker' }
  Should-BeTrue ($r.Text -match 'refused: Docker is not running') 'with Docker off it refuses, and removes nothing'
}

$dockerUp = $false
if (-not $SkipDocker) { $dockerUp = (Invoke-PerfDocker -DockerArgs @('version', '--format', '{{.Server.Version}}')).Code -eq 0 }
if ($dockerUp) {
  It 'the stop script removes labelled containers and networks for real, and an unlabelled container survives' {
    $test = 'cm.agent=qa-2a-selftest'
    $image = 'grafana/k6:1.0.0'
    if ((Invoke-PerfDocker -DockerArgs @('image', 'inspect', $image, '--format', 'ok')).Code -ne 0) { [void](Invoke-PerfDocker -DockerArgs @('pull', '-q', $image)) }
    $bystander = 'cm-perf-selftest-bystander'
    $labelled = 'cm-perf-selftest-labelled'
    $net = 'cm-perf-selftest-net'
    try {
      Invoke-PerfDocker -DockerArgs @('network', 'create', '--internal', '--label', $test, $net) | Out-Null
      Invoke-PerfDocker -DockerArgs @('run', '-d', '--name', $labelled, '--label', $test, '--network', $net, '--entrypoint', 'sleep', $image, '120') | Out-Null
      Invoke-PerfDocker -DockerArgs @('run', '-d', '--name', $bystander, '--entrypoint', 'sleep', $image, '120') | Out-Null   # same name family, NO label
      $r = Invoke-Script 'Stop-Perf.ps1' @() @{ CM_PERF_LABEL = $test }
      Should-BeTrue ($r.Text -match 'removed 1 container\(s\) and 1 network\(s\)') "it removed the labelled ones: $($r.Text)"
      Should-BeTrue ($r.Text -match 'stopped') 'it prints stopped'
      $left = (Invoke-PerfDocker -DockerArgs @('ps', '-a', '--format', '{{.Names}}', '--filter', "name=cm-perf-selftest")).Output
      Should-BeTrue ($left -contains $bystander) 'the unlabelled container survived'
      Should-BeTrue (-not ($left -contains $labelled)) 'the labelled container is gone'
      Should-BeTrue ((Invoke-PerfDocker -DockerArgs @('network', 'inspect', $net)).Code -ne 0) 'the labelled network is gone'
    }
    finally {
      [void](Invoke-PerfDocker -DockerArgs @('rm', '-f', $bystander))
      [void](Invoke-PerfDocker -DockerArgs @('rm', '-f', $labelled))
      [void](Invoke-PerfDocker -DockerArgs @('network', 'rm', $net))
    }
  }
}
else {
  $script:skipped++
  Write-Host 'skip the real-Docker stop test (Docker is not running or -SkipDocker)'
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) of $($script:count) tests failed"; exit 1 }
Write-Host "all $($script:count) perf tests passed$(if ($script:skipped) { " ($($script:skipped) skipped: needs Docker)" })"
