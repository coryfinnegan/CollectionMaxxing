# The throwaway stack the ZAP scans run against, and a software passkey that signs the owner in on it.
# Dot-sourced after Security.Common.ps1. Nothing here is written into the repository; secrets live in a temp folder that is
# deleted at the end of the run, and the database is on tmpfs (no volume to clean up).

function ConvertTo-Base64Url { param([byte[]]$Bytes) [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') }
function ConvertFrom-Base64Url {
    param([string]$Text)
    $s = $Text.Replace('-', '+').Replace('_', '/')
    switch ($s.Length % 4) { 2 { $s += '==' } 3 { $s += '=' } }
    , [Convert]::FromBase64String($s)
}

# ---- a WebAuthn authenticator in software (a port of tests/CollectionMaxxing.Api.Tests/Auth/SoftwareAuthenticator.cs) ----------

function New-SoftwareAuthenticator {
    param([string]$RpId = 'localhost', [string]$Origin = 'https://localhost')
    $key = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
    $id = [byte[]]::new(32)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($id)
    [pscustomobject]@{ Key = $key; CredentialId = $id; RpId = $RpId; Origin = $Origin; SignCount = [uint32]0; UserHandle = $null }
}

function Get-AuthenticatorData {
    param($Auth, [byte]$Flags, [bool]$IncludeCredential)
    $data = [System.Collections.Generic.List[byte]]::new()
    $data.AddRange([System.Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Auth.RpId)))
    $data.Add($Flags)
    $Auth.SignCount++
    $counter = [BitConverter]::GetBytes([uint32]$Auth.SignCount); [Array]::Reverse($counter)
    $data.AddRange($counter)
    if ($IncludeCredential) {
        $data.AddRange([byte[]]::new(16))
        $data.AddRange([byte[]]@(([byte]($Auth.CredentialId.Length -shr 8)), ([byte]($Auth.CredentialId.Length -band 255))))
        $data.AddRange($Auth.CredentialId)
        $p = $Auth.Key.ExportParameters($false)
        $data.AddRange([byte[]]@(0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20)); $data.AddRange($p.Q.X)
        $data.AddRange([byte[]]@(0x22, 0x58, 0x20)); $data.AddRange($p.Q.Y)
    }
    , $data.ToArray()
}

function Get-ClientData {
    param($Auth, [string]$Type, [string]$Challenge)
    , [Text.Encoding]::UTF8.GetBytes((@{ type = $Type; challenge = $Challenge; origin = $Auth.Origin; crossOrigin = $false } | ConvertTo-Json -Compress))
}

function New-PasskeyCredential {
    <# Answers the creation options the server sent, like navigator.credentials.create() (attestation "none"). #>
    param($Auth, $Options)
    $Auth.UserHandle = $Options.user.id
    $clientData = Get-ClientData $Auth 'webauthn.create' $Options.challenge
    $authData = Get-AuthenticatorData $Auth 0x45 $true
    $cbor = [System.Collections.Generic.List[byte]]::new()
    $cbor.Add(0xA3)
    foreach ($pair in @(@('fmt', 'none'), @('attStmt', $null))) {
        $name = [Text.Encoding]::UTF8.GetBytes($pair[0]); $cbor.Add([byte](0x60 + $name.Length)); $cbor.AddRange($name)
        if ($null -eq $pair[1]) { $cbor.Add(0xA0) } else { $v = [Text.Encoding]::UTF8.GetBytes($pair[1]); $cbor.Add([byte](0x60 + $v.Length)); $cbor.AddRange($v) }
    }
    $name = [Text.Encoding]::UTF8.GetBytes('authData'); $cbor.Add([byte](0x60 + $name.Length)); $cbor.AddRange($name)
    $cbor.AddRange([byte[]]@(0x59, ([byte]($authData.Length -shr 8)), ([byte]($authData.Length -band 255)))); $cbor.AddRange($authData)
    $idText = ConvertTo-Base64Url $Auth.CredentialId
    @{
        id = $idText; rawId = $idText; type = 'public-key'; authenticatorAttachment = 'platform'; clientExtensionResults = @{}
        response = @{ clientDataJSON = (ConvertTo-Base64Url $clientData); attestationObject = (ConvertTo-Base64Url $cbor.ToArray()); transports = @('internal') }
    }
}

# ---- the stack ------------------------------------------------------------------------------------------------------------------

function New-SelfSignedPfx {
    param([string]$Path, [string]$Subject)
    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    $req = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new("CN=$Subject", $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $cert = $req.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddDays(2))
    [IO.File]::WriteAllBytes($Path, $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx))
}

function Start-SecStack {
    <#
      Starts Postgres (tmpfs), applies the migrations, and starts the API image on the isolated network. Publishes the API on
      127.0.0.1:<random> only so this script can sign in. Returns the names and the loopback port.
      The environment is Production-like (fake providers are not needed: no provider has a key, so none is ever called).
    #>
    param([string]$RunId, [int]$Port, [string]$ApiImage, [string]$WorkDir, [int]$ReadyAttempts = 45)
    $net = "$($script:NamePrefix)-net-$RunId"; $pg = "$($script:NamePrefix)-pg-$RunId"; $api = "$($script:NamePrefix)-api-$RunId"
    $secrets = Join-Path $WorkDir 'secrets'
    New-Item -ItemType Directory -Force -Path $secrets | Out-Null
    Assert-OutsideRepo $secrets
    New-SelfSignedPfx (Join-Path $secrets 'openiddict-signing.pfx') 'cm-sec signing'
    New-SelfSignedPfx (Join-Path $secrets 'openiddict-encryption.pfx') 'cm-sec encryption'
    Open-SecFolderForContainer $secrets 'the API'   # the API writes setup-code.txt here

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

    $publish = if ($Port -gt 0) { "127.0.0.1:${Port}:5480" } else { '127.0.0.1::5480' }
    $r = Invoke-Sec docker (@('run', '-d', '--name', $api) + (Get-LabelArgs) + @('--network', $net, '--network-alias', 'api', '-p', $publish, '--security-opt', 'no-new-privileges:true', '--cap-drop', 'ALL',
            '-v', "${secrets}:/secrets",
            '-e', 'ASPNETCORE_ENVIRONMENT=Production',
            '-e', "ConnectionStrings__collectionmaxxing=Host=$pg;Database=cm;Username=cm;Password=$dbPassword",
            '-e', 'Auth__RelyingPartyId=localhost', '-e', 'Auth__SecretsPath=/secrets',
            '-e', 'Auth__Keys__SigningCertificatePath=/secrets/openiddict-signing.pfx',
            '-e', 'Auth__Keys__EncryptionCertificatePath=/secrets/openiddict-encryption.pfx',
            '-e', 'Auth__KeyRing__Path=/data/files/keyring', '-e', 'Catalog__CoversRoot=/data/files/covers', '-e', 'Files__Root=/data/files',
            '-e', 'Exports__Path=/data/files/exports', '-e', 'Evals__Enabled=false',
            $ApiImage))
    if ($r.ExitCode -ne 0) { Stop-SecFailure "Could not start the API: $($r.Output -join ' ')" }
    $hostPort = Get-PublishedPort $api
    Assert-PortAllowed $hostPort 'published port'
    for ($i = 0; $i -lt $ReadyAttempts; $i++) {
        try { if ((Invoke-WebRequest -Uri "http://127.0.0.1:$hostPort/health/ready" -SkipHttpErrorCheck -TimeoutSec 2).StatusCode -eq 200) { break } } catch { }
        if ($i -eq $ReadyAttempts - 1) { Stop-SecFailure "The API did not become ready.`n$((Invoke-Sec docker @('logs', '--tail', '15', $api)).Output -join "`n")" }
        Start-Sleep -Seconds 2
    }
    [pscustomobject]@{ Network = $net; Postgres = $pg; Api = $api; HostPort = $hostPort; Secrets = $secrets }
}

function Read-CookieJar {
    param($Response, [hashtable]$Jar)
    $headers = $null
    if ($Response.Headers.TryGetValue('Set-Cookie', [ref]$headers)) {
        foreach ($h in @($headers)) {
            if ($h -match '^([^=;\s]+)=([^;]*)') { if ($Matches[2]) { $Jar[$Matches[1]] = $Matches[2] } else { $Jar.Remove($Matches[1]) } }
        }
    }
}

function Invoke-Json {
    param([string]$Base, [string]$Method, [string]$Path, $Body, [hashtable]$Jar, [string]$Bearer, [string]$HostHeader)
    $headers = @{ 'X-CSRF' = '1'; 'Accept' = 'application/json' }
    if ($HostHeader) { $headers['Host'] = $HostHeader }
    if ($Jar.Count -gt 0) { $headers['Cookie'] = ($Jar.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join '; ' }
    if ($Bearer) { $headers['Authorization'] = "Bearer $Bearer" }
    $args = @{ Uri = "$Base$Path"; Method = $Method; Headers = $headers; SkipHttpErrorCheck = $true; TimeoutSec = 30 }
    if ($null -ne $Body) { $args['Body'] = ($Body | ConvertTo-Json -Depth 12 -Compress); $args['ContentType'] = 'application/json' }
    $resp = Invoke-WebRequest @args
    Read-CookieJar $resp $Jar
    [pscustomobject]@{ Status = [int]$resp.StatusCode; Json = $(if ($resp.Content) { try { $resp.Content | ConvertFrom-Json -Depth 20 } catch { $null } } else { $null }) }
}

function Register-Passkey {
    param([string]$Base, [hashtable]$Jar, $Auth, $Start, [string]$Invite)
    if ($Start.Status -ne 200) { Stop-SecFailure "Passkey options were refused (HTTP $($Start.Status))." }
    $credential = New-PasskeyCredential $Auth $Start.Json.options
    $path = '/account/passkey/register' + $(if ($Invite) { '?invite=' + [uri]::EscapeDataString($Invite) } else { '' })
    $done = Invoke-Json $Base POST $path @{ credential = $credential; name = 'cm-sec passkey' } $Jar
    if ($done.Status -ne 201) { Stop-SecFailure "Passkey registration failed (HTTP $($done.Status))." }
    $done.Json
}

function Get-DeviceToken {
    <# Signed in with the cookie in $Jar: pair a "phone" and trade the pairing for a bearer token. The token stays in memory. #>
    param([string]$Base, [hashtable]$Jar)
    $pair = Invoke-Json $Base POST '/api/v1/me/devices/pair' $null $Jar
    if ($pair.Status -ne 200 -and $pair.Status -ne 201) { Stop-SecFailure "Pairing a device failed (HTTP $($pair.Status))." }
    $form = "grant_type=urn%3Acm%3Adevice_pairing&pairing_token=$([uri]::EscapeDataString($pair.Json.token))&pairing_code=$([uri]::EscapeDataString($pair.Json.code))&device_name=cm-sec&scope=api"
    $resp = Invoke-WebRequest -Uri "$Base/connect/token" -Method Post -Body $form -ContentType 'application/x-www-form-urlencoded' -Headers @{ Host = 'api:5480' } -SkipHttpErrorCheck -TimeoutSec 30
    if ($resp.StatusCode -ne 200) { Stop-SecFailure "Trading the pairing for a token failed (HTTP $($resp.StatusCode))." }
    ($resp.Content | ConvertFrom-Json).access_token
}

function New-ThrowawayEditorToken {
    <#
      Signs the throwaway owner in with a software passkey (first-run setup), invites a second member as an editor, signs that
      member in the same way, and returns an editor's bearer token. Everything is in memory; nothing is written to disk or logged.
    #>
    param([string]$Base, [string]$SecretsDir)
    $codeFile = Join-Path $SecretsDir 'setup-code.txt'
    $code = $null
    for ($i = 0; $i -lt 30 -and -not $code; $i++) {
        if (Test-Path -LiteralPath $codeFile) { $code = (Get-Content -LiteralPath $codeFile -TotalCount 1).Trim() } else { Start-Sleep -Seconds 1 }
    }
    if (-not $code) { Stop-SecFailure 'The API did not write a setup code (is the database empty and migrated?).' }

    $ownerJar = @{}; $owner = New-SoftwareAuthenticator
    $signedIn = Register-Passkey $Base $ownerJar $owner (Invoke-Json $Base POST '/setup' @{ code = $code; displayName = 'Scan owner' } $ownerJar)

    $invite = Invoke-Json $Base POST "/api/v1/libraries/$($signedIn.libraryId)/invites" @{ name = 'Scan editor'; role = 'editor' } $ownerJar
    if ($invite.Status -notin 200, 201) { Stop-SecFailure "Creating an editor invite failed (HTTP $($invite.Status): $($invite.Json.detail) $($invite.Json.errors | ConvertTo-Json -Compress -Depth 3))." }
    $inviteToken = ($invite.Json.link -replace '^.*/join/', '')

    $editorJar = @{}; $editor = New-SoftwareAuthenticator
    $start = Invoke-Json $Base POST '/account/passkey/creation-options' @{ displayName = 'Scan editor'; inviteToken = $inviteToken } $editorJar
    Register-Passkey $Base $editorJar $editor $start $inviteToken | Out-Null
    $token = Get-DeviceToken $Base $editorJar
    # The token's issuer is the host it was asked for, and ZAP reaches the API as api:5480, so the token is asked for under that name.
    $me = Invoke-Json $Base GET '/api/v1/me' $null @{} $token 'api:5480'
    if ($me.Status -ne 200) { Stop-SecFailure "The editor's token was not accepted by /api/v1/me (HTTP $($me.Status))." }
    [pscustomobject]@{ Token = $token; LibraryId = $signedIn.libraryId }
}
