# The throwaway stack the ZAP scans run against, and the bearer tokens that sign two throwaway people in on it.
# Dot-sourced after Security.Common.ps1. Nothing here is written into the repository; the fake Auth0's keys and certificates live in a temp
# folder that is deleted at the end of the run, and the database is on tmpfs (no volume to clean up).
#
# Sign-in is Auth0 (ADR-025), and the harness has no internet, so a fake Auth0 runs in the same Docker network (scripts/fake-auth0). The
# API is configured exactly as in production (Auth:Auth0:* names the fake) and trusts the per-run CA through SSL_CERT_FILE; no production
# setting is weakened. The harness signs tokens for its owner and its editor, then uses the API's own flow: the first bearer call makes the
# account, POST /api/v1/libraries makes the library, an invite code is accepted by the editor.

. (Join-Path $PSScriptRoot '../fake-auth0/FakeAuth0.ps1')

$script:SecOwner = [pscustomobject]@{ Sub = 'auth0|cm-sec-owner'; Name = 'Scan owner'; Email = 'owner@example.test' }
$script:SecEditor = [pscustomobject]@{ Sub = 'auth0|cm-sec-editor'; Name = 'Scan editor'; Email = 'editor@example.test' }

function Wait-SecFakeAuth0 {
    param([string]$Container, [int]$Attempts = 30)
    for ($i = 0; $i -lt $Attempts; $i++) {
        if ((Invoke-Sec docker @('logs', $Container)).Output -match 'fake auth0 listening') { return }
        if ($global:SecSleep) { & $global:SecSleep 1 } else { Start-Sleep -Seconds 1 }
    }
    Stop-SecFailure "The fake Auth0 did not start.`n$((Invoke-Sec docker @('logs', '--tail', '15', $Container)).Output -join "`n")"
}

function Start-SecStack {
    <#
      Starts Postgres (tmpfs), applies the migrations, starts the fake Auth0 and then the API image, all on the isolated network.
      Publishes the API on 127.0.0.1:<random> only so this script can sign in. Returns the names, the loopback port and the fake Auth0's
      signing material (for minting tokens). The environment is Production-like (fake providers are not needed: no provider has a key,
      so none is ever called).
    #>
    param([string]$RunId, [int]$Port, [string]$ApiImage, [string]$WorkDir, [int]$ReadyAttempts = 45)
    $net = "$($script:NamePrefix)-net-$RunId"; $pg = "$($script:NamePrefix)-pg-$RunId"; $api = "$($script:NamePrefix)-api-$RunId"; $fake = "$($script:NamePrefix)-auth0-$RunId"
    $authDir = Join-Path $WorkDir 'fake-auth0'
    Assert-OutsideRepo $authDir
    $auth0 = New-FakeAuth0Material -Directory $authDir -Users @($script:SecOwner, $script:SecEditor)
    Open-SecFolderForContainer $authDir 'the fake Auth0 and the API'

    $r = Invoke-Sec docker (@('network', 'create') + (Get-LabelArgs) + @($net))
    if ($r.ExitCode -ne 0) { Stop-SecFailure "Could not create the network: $($r.Output -join ' ')" }

    $dbPassword = New-RandomPassword
    $r = Invoke-Sec docker (@('run', '-d', '--name', $pg) + (Get-LabelArgs) + @('--network', $net, '--tmpfs', '/var/lib/postgresql/data', '--security-opt', 'no-new-privileges:true',
            '-e', 'POSTGRES_DB=cm', '-e', 'POSTGRES_USER=cm', '-e', "POSTGRES_PASSWORD=$dbPassword", $script:PostgresImage))
    if ($r.ExitCode -ne 0) { Stop-SecFailure "Could not start Postgres: $($r.Output -join ' ')" }
    for ($i = 0; $i -lt 40; $i++) {
        if ((Invoke-Sec docker @('exec', $pg, 'pg_isready', '-U', 'cm', '-d', 'cm')).ExitCode -eq 0) { break }
        if ($i -eq 39) { Stop-SecFailure 'Postgres did not become ready in 80 seconds.' }
        if ($global:SecSleep) { & $global:SecSleep 2 } else { Start-Sleep -Seconds 2 }
    }

    # The schema: the same migrations the deployer applies, as one idempotent script run through psql inside the container.
    $sql = Join-Path $WorkDir 'migrate.sql'
    $r = Invoke-Sec dotnet @('ef', 'migrations', 'script', '--idempotent', '--project', 'src/CollectionMaxxing.Infrastructure', '--startup-project', 'src/CollectionMaxxing.Api', '--output', $sql, '--no-build')
    if ($r.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $sql)) {
        $r = Invoke-Sec dotnet @('ef', 'migrations', 'script', '--idempotent', '--project', 'src/CollectionMaxxing.Infrastructure', '--startup-project', 'src/CollectionMaxxing.Api', '--output', $sql)
    }
    if (-not (Test-Path -LiteralPath $sql)) { Stop-SecFailure "Could not make the migration script (dotnet tool restore, then build the solution once):`n$(($r.Output | Select-Object -Last 8) -join "`n")" }
    Invoke-Sec docker @('cp', $sql, "${pg}:/tmp/migrate.sql") | Out-Null
    $r = Invoke-Sec docker @('exec', $pg, 'psql', '-q', '-U', 'cm', '-d', 'cm', '-v', 'ON_ERROR_STOP=1', '-f', '/tmp/migrate.sql')
    if ($r.ExitCode -ne 0) { Stop-SecFailure "Applying the migrations failed:`n$(($r.Output | Select-Object -Last 8) -join "`n")" }

    $r = Invoke-Sec docker (@('run', '-d') + (Get-LabelArgs) + (Get-FakeAuth0ContainerArgs -Name $fake -Network $net -Directory $authDir))
    if ($r.ExitCode -ne 0) { Stop-SecFailure "Could not start the fake Auth0: $($r.Output -join ' ')" }
    Wait-SecFakeAuth0 $fake

    $envArgs = @()
    foreach ($pair in @(
            [ordered]@{
                ASPNETCORE_ENVIRONMENT = 'Production'
                ConnectionStrings__collectionmaxxing = "Host=$pg;Database=cm;Username=cm;Password=$dbPassword"
                Auth__KeyRing__Path = '/data/files/keyring'; Catalog__CoversRoot = '/data/files/covers'; Files__Root = '/data/files'
                Exports__Path = '/data/files/exports'; Evals__Enabled = 'false'
            }, (Get-FakeAuth0ApiEnvironment))) {
        foreach ($name in $pair.Keys) { $envArgs += @('-e', "$name=$($pair[$name])") }
    }
    $publish = if ($Port -gt 0) { "127.0.0.1:${Port}:5480" } else { '127.0.0.1::5480' }
    $r = Invoke-Sec docker (@('run', '-d', '--name', $api) + (Get-LabelArgs) + @('--network', $net, '--network-alias', 'api', '-p', $publish, '--security-opt', 'no-new-privileges:true', '--cap-drop', 'ALL') +
        (Get-FakeAuth0ApiMount -Directory $authDir) + $envArgs + @($ApiImage))
    if ($r.ExitCode -ne 0) { Stop-SecFailure "Could not start the API: $($r.Output -join ' ')" }
    $hostPort = Get-PublishedPort $api
    Assert-PortAllowed $hostPort 'published port'
    for ($i = 0; $i -lt $ReadyAttempts; $i++) {
        try { if ((Invoke-WebRequest -Uri "http://127.0.0.1:$hostPort/health/ready" -SkipHttpErrorCheck -TimeoutSec 2).StatusCode -eq 200) { break } } catch { }
        if ($i -eq $ReadyAttempts - 1) { Stop-SecFailure "The API did not become ready.`n$((Invoke-Sec docker @('logs', '--tail', '15', $api)).Output -join "`n")" }
        Start-Sleep -Seconds 2
    }
    [pscustomobject]@{ Network = $net; Postgres = $pg; Api = $api; Auth0Container = $fake; HostPort = $hostPort; Auth0 = $auth0 }
}

function Read-CookieJar {
    <# A browser's cookie jar, kept by hand: the API's cookies are Secure and __Host- prefixed, and this talks to it over loopback http. #>
    param($Response, [hashtable]$Jar)
    $headers = $null
    if ($Response.Headers.TryGetValue('Set-Cookie', [ref]$headers)) {
        foreach ($h in @($headers)) {
            if ($h -match '^([^=;\s]+)=([^;]*)') { if ($Matches[2]) { $Jar[$Matches[1]] = $Matches[2] } else { $Jar.Remove($Matches[1]) } }
        }
    }
}

function Invoke-Json {
    <# One API call as JSON: with a bearer token, or with the cookies of a web session in $Jar. #>
    param([string]$Base, [string]$Method, [string]$Path, $Body, [string]$Bearer, [hashtable]$Jar = @{})
    $headers = @{ 'X-CSRF' = '1'; 'Accept' = 'application/json' }
    if ($Jar.Count -gt 0) { $headers['Cookie'] = ($Jar.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join '; ' }
    if ($Bearer) { $headers['Authorization'] = "Bearer $Bearer" }
    $args = @{ Uri = "$Base$Path"; Method = $Method; Headers = $headers; SkipHttpErrorCheck = $true; TimeoutSec = 30 }
    if ($null -ne $Body) { $args['Body'] = ($Body | ConvertTo-Json -Depth 12 -Compress); $args['ContentType'] = 'application/json' }
    $resp = Invoke-WebRequest @args
    Read-CookieJar $resp $Jar
    [pscustomobject]@{ Status = [int]$resp.StatusCode; Json = $(if ($resp.Content) { try { $resp.Content | ConvertFrom-Json -Depth 20 } catch { $null } } else { $null }) }
}

function Invoke-Navigation {
    <# A browser navigation that is not followed: the status and where the answer points. Cookies go in and out of $Jar. #>
    param([string]$Base, [string]$Path, [hashtable]$Jar)
    $headers = @{}
    if ($Jar.Count -gt 0) { $headers['Cookie'] = ($Jar.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join '; ' }
    # With redirects off PowerShell still reports "maximum redirection count exceeded" as a (non-fatal) error next to the 302 it returns.
    $resp = Invoke-WebRequest -Uri "$Base$Path" -Method Get -Headers $headers -MaximumRedirection 0 -SkipHttpErrorCheck -TimeoutSec 30 -ErrorAction SilentlyContinue
    if (-not $resp) { Stop-SecFailure "No answer from $Path." }
    Read-CookieJar $resp $Jar
    $location = $null
    $values = $null
    if ($resp.Headers.TryGetValue('Location', [ref]$values)) { $location = @($values)[0] }
    [pscustomobject]@{ Status = [int]$resp.StatusCode; Location = $location }
}

function Invoke-WebSignIn {
    <#
      Plays the browser through the web sign-in against the fake Auth0 and returns the cookie jar of the session:
        GET /account/login            302 to Auth0's authorize address, with the state and the nonce
        GET /signin-oidc?code&state   the callback; the API trades the code at the fake's /oauth/token (over TLS, with its ID token signed
                                      by the run's key) and, for a sub it has not seen, makes the account from the ID token
      The code is the one the fake understands (New-FakeAuth0WebCode). A bearer token never counts as a recent sign-in, so whatever needs
      one (creating an invite) is done with this session.
    #>
    param([string]$Base, [string]$Subject)
    $jar = @{}
    $start = Invoke-Navigation $Base '/account/login?returnUrl=%2F' $jar
    if ($start.Status -ne 302 -or -not $start.Location) { Stop-SecFailure "Starting the web sign-in answered HTTP $($start.Status), not a redirect to Auth0." }
    $query = [System.Web.HttpUtility]::ParseQueryString(([uri]$start.Location).Query)
    if (-not $query['state'] -or -not $query['nonce']) { Stop-SecFailure 'The redirect to Auth0 had no state or nonce.' }
    $code = New-FakeAuth0WebCode -Subject $Subject -Nonce $query['nonce']
    $callback = Invoke-Navigation $Base "/signin-oidc?code=$([uri]::EscapeDataString($code))&state=$([uri]::EscapeDataString($query['state']))" $jar
    if ($callback.Status -ne 302 -or $callback.Location -ne '/') {
        Stop-SecFailure "The web sign-in callback answered HTTP $($callback.Status) to '$($callback.Location)' (wanted a redirect to /)."
    }
    $jar
}

function Assert-SecStatus {
    param($Response, [int[]]$Expected, [string]$What)
    if ($Response.Status -notin $Expected) {
        Stop-SecFailure "$What failed (HTTP $($Response.Status): $($Response.Json.code) $($Response.Json.detail) $($Response.Json.errors | ConvertTo-Json -Compress -Depth 3))."
    }
}

function New-ThrowawayEditorToken {
    <#
      Seeds the stack through the API's own flow and returns the editor's bearer token with the library's id:
        1. the owner signs in on the web (an invite needs a recent sign-in, which a bearer token never has); that makes the account;
        2. the owner creates a library and an editor invite;
        3. a token for the editor; the editor's first call makes that account (the API asks the fake Auth0's /userinfo for the name),
           then the invite code is accepted.
      Tokens stay in memory; nothing is written to disk or logged.
    #>
    param([string]$Base, $Auth0)
    $ownerJar = Invoke-WebSignIn $Base $script:SecOwner.Sub
    $library = Invoke-Json $Base POST '/api/v1/libraries' @{ name = 'Scan library' } -Jar $ownerJar
    Assert-SecStatus $library 201 'Creating the library'
    $invite = Invoke-Json $Base POST "/api/v1/libraries/$($library.Json.id)/invites" @{ name = 'Scan editor'; role = 'editor' } -Jar $ownerJar
    Assert-SecStatus $invite 200, 201 'Creating the editor invite'

    $editorToken = New-FakeAuth0Token $Auth0 $script:SecEditor.Sub
    Assert-SecStatus (Invoke-Json $Base GET '/api/v1/me' $null $editorToken) 200 "The editor's first call"
    $joined = Invoke-Json $Base POST '/api/v1/invites:accept' @{ invite = $invite.Json.code } $editorToken
    Assert-SecStatus $joined 200 'Accepting the invite'
    if ($joined.Json.role -ne 'Editor' -and $joined.Json.role -ne 'editor') { Stop-SecFailure "The editor joined as '$($joined.Json.role)', not an editor." }
    [pscustomobject]@{ Token = $editorToken; LibraryId = $library.Json.id }
}
