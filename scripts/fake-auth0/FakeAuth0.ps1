# A fake Auth0 for the security and load harnesses (QA-6, #242). Dot-sourced by scripts/security and scripts/perf; it has no side effects.
#
# Why this shape: the API checks bearer tokens against https://{Auth:Auth0:Domain}/ (discovery, JWKS) and asks that same host's /userinfo
# once for a new account. Production code has no switch that weakens that, and none was added. Instead the harness serves a fake Auth0 over
# TLS inside the throwaway Docker network, and the API container trusts a per-run CA (SSL_CERT_FILE), so the real checks run unchanged:
#   - one RSA signing key, one CA and one server certificate per run, made here and written to a temp folder (never the repo);
#   - the fake (server.py, on python:alpine) serves discovery, JWKS and /userinfo for the harness's own users;
#   - the harness signs the users' access tokens itself (New-FakeAuth0Token) and keeps them in memory or in the run's temp folder.

$script:FakeAuth0Alias = 'fake-auth0'
$script:FakeAuth0Port = 8443
$script:FakeAuth0Audience = 'urn:collectionmaxxing:api'
$script:FakeAuth0WebClientId = 'cm-fake-web-client'
$script:FakeAuth0Image = 'python:3.13-alpine'

function ConvertTo-FakeBase64Url {
    param([byte[]]$Bytes)
    [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Get-FakeAuth0Domain { "$($script:FakeAuth0Alias):$($script:FakeAuth0Port)" }

function New-FakeAuth0Material {
    <#
      Makes everything the fake and the API need for one run, in $Directory (which must be outside the repository):
        ca/ca.pem                            the per-run CA, the only thing the API container is given
        server.pem, server.key               a server certificate for the fake's name (DNS SAN), signed by that CA
        openid-configuration.json, jwks.json the discovery document and the public signing key
        users.json                           sub -> name and email, for /userinfo and the web sign-in's ID token
        signing.json                         the signing key's n and d, so the fake can sign the web sign-in's ID token (throwaway key)
        server.py                            the fake itself
      Returns the object New-FakeAuth0Token signs with. $Users is a list of @{ Sub; Name; Email }.
    #>
    param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][object[]]$Users)
    $sha256 = [System.Security.Cryptography.HashAlgorithmName]::SHA256
    $pkcs1 = [System.Security.Cryptography.RSASignaturePadding]::Pkcs1
    New-Item -ItemType Directory -Force -Path (Join-Path $Directory 'ca') | Out-Null
    $notBefore = [DateTimeOffset]::UtcNow.AddMinutes(-5)

    $caKey = [System.Security.Cryptography.RSA]::Create(2048)
    $caRequest = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=cm fake auth0 CA', $caKey, $sha256, $pkcs1)
    $caRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
    $caUsage = [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign
    $caRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new($caUsage, $true))
    $caRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($caRequest.PublicKey, $false))
    $ca = $caRequest.CreateSelfSigned($notBefore, [DateTimeOffset]::UtcNow.AddDays(2))

    $leafKey = [System.Security.Cryptography.RSA]::Create(2048)
    $leafRequest = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new("CN=$($script:FakeAuth0Alias)", $leafKey, $sha256, $pkcs1)
    $san = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $san.AddDnsName($script:FakeAuth0Alias)
    $leafRequest.CertificateExtensions.Add($san.Build())
    $leafRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    $leafUsage = [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment
    $leafRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new($leafUsage, $true))
    $serverAuth = [System.Security.Cryptography.OidCollection]::new()
    [void]$serverAuth.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
    $leafRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($serverAuth, $false))
    $leafRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509AuthorityKeyIdentifierExtension]::CreateFromCertificate($ca, $true, $false))
    $serial = [byte[]]::new(16)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($serial)
    $serial[0] = $serial[0] -band 0x7f
    $leaf = $leafRequest.Create($ca, $notBefore, [DateTimeOffset]::UtcNow.AddDays(1), $serial)

    $signing = [System.Security.Cryptography.RSA]::Create(2048)
    $keyId = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(8)).ToLowerInvariant()
    $p = $signing.ExportParameters($true)
    $issuer = "https://$(Get-FakeAuth0Domain)/"

    Set-Content -LiteralPath (Join-Path $Directory 'ca/ca.pem') -Value $ca.ExportCertificatePem()
    Set-Content -LiteralPath (Join-Path $Directory 'server.pem') -Value $leaf.ExportCertificatePem()
    Set-Content -LiteralPath (Join-Path $Directory 'server.key') -Value $leafKey.ExportPkcs8PrivateKeyPem()
    @{
        issuer = $issuer
        authorization_endpoint = "${issuer}authorize"
        token_endpoint = "${issuer}oauth/token"
        userinfo_endpoint = "${issuer}userinfo"
        jwks_uri = "${issuer}.well-known/jwks.json"
        response_types_supported = @('code')
        subject_types_supported = @('public')
        id_token_signing_alg_values_supported = @('RS256')
        scopes_supported = @('openid', 'profile', 'email')
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Directory 'openid-configuration.json')
    @{ keys = @(@{ kty = 'RSA'; use = 'sig'; alg = 'RS256'; kid = $keyId; n = (ConvertTo-FakeBase64Url $p.Modulus); e = (ConvertTo-FakeBase64Url $p.Exponent) }) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Directory 'jwks.json')
    $table = [ordered]@{}
    foreach ($u in $Users) { $table[$u.Sub] = @{ name = $u.Name; email = $u.Email } }
    $table | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Directory 'users.json')
    @{ n = (ConvertTo-FakeBase64Url $p.Modulus); d = (ConvertTo-FakeBase64Url $p.D) } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Directory 'signing.json')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'server.py') -Destination (Join-Path $Directory 'server.py') -Force

    foreach ($disposable in $caKey, $leafKey, $ca, $leaf) { $disposable.Dispose() }   # the files hold what the containers need; the signing key stays here
    [pscustomobject]@{ Directory = $Directory; Issuer = $issuer; Audience = $script:FakeAuth0Audience; KeyId = $keyId; Signing = $signing; Users = @($Users) }
}

function New-FakeAuth0Token {
    <# An RS256 access token for $Subject, issued by the fake, for our API's audience. Lives an hour unless asked otherwise. #>
    param([Parameter(Mandatory)]$Material, [Parameter(Mandatory)][string]$Subject, [int]$LifetimeMinutes = 60, [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow)
    $header = [ordered]@{ alg = 'RS256'; typ = 'JWT'; kid = $Material.KeyId }
    $payload = [ordered]@{
        iss = $Material.Issuer; sub = $Subject; aud = $Material.Audience; azp = 'cm-fake-client'
        iat = $Now.ToUnixTimeSeconds(); exp = $Now.AddMinutes($LifetimeMinutes).ToUnixTimeSeconds(); scope = 'openid profile email'
    }
    $encode = { param($o) ConvertTo-FakeBase64Url ([Text.Encoding]::UTF8.GetBytes(($o | ConvertTo-Json -Compress))) }
    $signingInput = "$(& $encode $header).$(& $encode $payload)"
    $signature = $Material.Signing.SignData([Text.Encoding]::ASCII.GetBytes($signingInput), [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    "$signingInput.$(ConvertTo-FakeBase64Url $signature)"
}

function New-FakeAuth0WebCode {
    <#
      The "code" the fake's /oauth/token turns into an ID token: base64url({sub, nonce}). The harness plays the browser: it starts
      GET /account/login, reads the state and nonce from the redirect to Auth0, and sends the API's callback this code with that state.
    #>
    param([Parameter(Mandatory)][string]$Subject, [Parameter(Mandatory)][string]$Nonce)
    ConvertTo-FakeBase64Url ([Text.Encoding]::UTF8.GetBytes((@{ sub = $Subject; nonce = $Nonce } | ConvertTo-Json -Compress)))
}

function Get-FakeAuth0ApiEnvironment {
    <#
      What the API container needs to sign people in through the fake: the tenant settings, a public origin (the production validator wants
      https), and the per-run CA as the only trusted root (SSL_CERT_FILE, honoured by .NET on Linux). The web client values are placeholders:
      the web code flow is not run here.
    #>
    [ordered]@{
        Auth__Auth0__Domain = (Get-FakeAuth0Domain)
        Auth__Auth0__Audience = $script:FakeAuth0Audience
        Auth__Auth0__WebClientId = $script:FakeAuth0WebClientId
        Auth__Auth0__WebClientSecret = 'cm-fake-web-client-secret'
        Auth__PublicOrigin = 'https://pc.tailnet.ts.net'
        SSL_CERT_FILE = '/fake-auth0-ca/ca.pem'
    }
}

function Get-FakeAuth0ContainerArgs {
    <#
      The docker arguments after "run -d" for the fake: its name, network and the alias the certificate is for, a read-only root
      file system, no capabilities, a non-root user and the run's folder mounted read-only. The caller adds its own labels.
    #>
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Network, [Parameter(Mandatory)][string]$Directory)
    @('--name', $Name, '--network', $Network, '--network-alias', $script:FakeAuth0Alias,
        '--user', '65534:65534', '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true', '--memory', '128m',
        '-e', 'PYTHONDONTWRITEBYTECODE=1', '-e', "FAKE_AUTH0_PORT=$($script:FakeAuth0Port)", '-e', "FAKE_AUTH0_CLIENT_ID=$($script:FakeAuth0WebClientId)",
        '--mount', "type=bind,source=$Directory,target=/fake,readonly",
        $script:FakeAuth0Image, 'python', '-u', '/fake/server.py')
}

function Get-FakeAuth0ApiMount {
    <# The API container gets only the CA certificate (the ca/ folder), never the server key or the signing material. #>
    param([Parameter(Mandatory)][string]$Directory)
    @('--mount', "type=bind,source=$(Join-Path $Directory 'ca'),target=/fake-auth0-ca,readonly")
}
