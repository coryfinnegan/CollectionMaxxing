# ADR-024 tests: the TestFlight signing helpers in deploy/apple. Everything is made up here (a self-signed stand-in for
# Apple's certificate, a hand-written profile), and gh is a recorder. Nothing reaches Apple or GitHub.
# Dot-sourced by Run-Tests.ps1, which loads Cm.Apple.ps1.

$script:AppleRepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:TestTeam = 'ABCDE12345'
$script:TestBundle = 'io.github.coryfinnegan.collectionmaxxing'

function New-TestAppleCertificate {
    <# A self-signed certificate shaped like Apple's: the CN names the kind, the OU carries the team. #>
    param([string]$KeyPem, [string]$Kind = 'Apple Distribution', [string]$Team = $script:TestTeam, [int]$Days = 365)
    $rsa = [System.Security.Cryptography.RSA]::Create()
    $rsa.ImportFromPem($KeyPem)
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        "CN=${Kind}: Test Owner ($Team), OU=$Team, O=Test Owner", $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $start = [DateTimeOffset]::UtcNow.AddDays([Math]::Min(-2, $Days - 2))
    , $request.CreateSelfSigned($start, [DateTimeOffset]::UtcNow.AddDays($Days)).RawData
}

function New-TestProfileBytes {
    <# A .mobileprovision stand-in: the plist between some binary bytes, as in Apple's signed envelope. #>
    param(
        [byte[][]]$Certificates = @(),
        [string]$Name = 'CollectionMaxxing App Store',
        [string]$Team = $script:TestTeam,
        [string]$BundleId = $script:TestBundle,
        [int]$Days = 300,
        [switch]$Development
    )
    $certs = ($Certificates | ForEach-Object { "<data>$([Convert]::ToBase64String($_))</data>" }) -join ''
    $devices = if ($Development) { '<key>ProvisionedDevices</key><array><string>00008130-000000000000001E</string></array>' } else { '' }
    $debug = if ($Development) { '<true/>' } else { '<false/>' }
    $expires = [DateTime]::UtcNow.AddDays($Days).ToString('yyyy-MM-ddTHH:mm:ssZ')
    $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>AppIDName</key><string>CollectionMaxxing</string>
<key>DeveloperCertificates</key><array>$certs</array>
<key>Entitlements</key><dict><key>application-identifier</key><string>$Team.$BundleId</string><key>get-task-allow</key>$debug<key>beta-reports-active</key><true/></dict>
<key>ExpirationDate</key><date>$expires</date>
<key>Name</key><string>$Name</string>
$devices
<key>TeamIdentifier</key><array><string>$Team</string></array>
<key>UUID</key><string>11111111-2222-3333-4444-555555555555</string>
<key>Version</key><integer>1</integer>
</dict></plist>
"@
    , ([byte[]](0x30, 0x82, 0x01, 0x00) + [Text.Encoding]::UTF8.GetBytes($plist) + [byte[]](0xA0, 0x82, 0x00))
}

function New-TestApiKeyText {
    $ec = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    $ec.ExportPkcs8PrivateKeyPem()
}

function New-TestPieces {
    <# A key, a certificate from it and a profile listing that certificate: everything fits. #>
    $pair = New-CmSigningRequest
    $cert = New-TestAppleCertificate -KeyPem $pair.KeyPem
    [pscustomobject]@{ KeyPem = $pair.KeyPem; Request = $pair.RequestPem; Cert = $cert; Profile = (New-TestProfileBytes -Certificates @(, $cert)) }
}

function Set-FakeGh {
    param([bool]$Admin = $true, [string[]]$Policies = @(), [string[]]$FailOn = @())
    $global:CmGh = @{ Admin = $Admin; Policies = $Policies; FailOn = $FailOn }
    $global:CmRunner = {
        param($Command, $Arguments, $Env, $Mutating, $InputText)
        $display = Format-CmCommand -Command $Command -Arguments $Arguments -Env $Env
        $global:CmCalls.Add([pscustomobject]@{ Command = $Command; Arguments = $Arguments; Mutating = $Mutating; Display = $display; InputText = $InputText })
        $line = $Arguments -join ' '
        foreach ($pattern in $global:CmGh.FailOn) { if ($line -like $pattern) { return [pscustomobject]@{ ExitCode = 1; Output = @() } } }
        if ($line -match '^api repos/[^/\s]+/[^/\s]+ --jq') { return [pscustomobject]@{ ExitCode = 0; Output = @("$($global:CmGh.Admin)".ToLowerInvariant()) } }
        if ($line -match 'deployment-branch-policies --jq') { return [pscustomobject]@{ ExitCode = 0; Output = @($global:CmGh.Policies) } }
        [pscustomobject]@{ ExitCode = 0; Output = @() }
    }
}

function Invoke-TestPublish {
    param($Pieces, [switch]$WithApiKey, [string]$KeyId = 'ABCDE12345', [string]$IssuerId = '69a6de70-0000-47e3-e053-5b8c7c11a4d1')
    $args2 = @{ KeyPem = $Pieces.KeyPem; CertificateBytes = $Pieces.Cert; ProfileBytes = $Pieces.Profile; Repo = 'owner/repo'; BundleId = $script:TestBundle }
    if ($WithApiKey) { $args2.ApiKeyText = (New-TestApiKeyText); $args2.KeyId = $KeyId; $args2.IssuerId = $IssuerId }
    $out = Publish-CmSigningSecrets @args2 6>&1
    [pscustomobject]@{ Result = @($out | Where-Object { $_ -isnot [System.Management.Automation.InformationRecord] -and $_.PSObject.Properties["ExitCode"] }) | Select-Object -Last 1; Text = (@($out | Where-Object { $_ -is [System.Management.Automation.InformationRecord] } | ForEach-Object { "$($_.MessageData)" }) -join "`n") }
}

function Get-SecretSets { @(Get-Calls 'gh' '*secret set*') }

# ---- the request and the pieces -------------------------------------------------------------------------------------

Test-Case 'apple: the signing request is an RSA 2048 request made from the key it comes with' {
    $pair = New-CmSigningRequest
    Assert-Match $pair.KeyPem '^-----BEGIN PRIVATE KEY-----'
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::LoadSigningRequestPem(
        $pair.RequestPem, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    $rsa = [System.Security.Cryptography.RSA]::Create()
    $rsa.ImportFromPem($pair.KeyPem)
    Assert-Equal 2048 $rsa.KeySize
    Assert-Equal ([Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo())) ([Convert]::ToBase64String($request.PublicKey.ExportSubjectPublicKeyInfo()))
}

Test-Case 'apple: a profile is read out of its envelope: name, team, bundle, expiry, kind and certificates' {
    $p = New-TestPieces
    $profile = Read-CmProvisioningProfile -Bytes $p.Profile
    Assert-Equal 'CollectionMaxxing App Store' $profile.Name
    Assert-Equal $script:TestTeam $profile.TeamId
    Assert-Equal $script:TestBundle $profile.BundleId
    Assert-Equal '11111111-2222-3333-4444-555555555555' $profile.Uuid
    Assert-True $profile.IsAppStore 'no devices and no get-task-allow: an App Store profile'
    Assert-True ($profile.Expires -gt [DateTime]::UtcNow.AddDays(299)) 'the expiry date is read'
    Assert-Equal 1 $profile.Certificates.Count
    Assert-False (Read-CmProvisioningProfile -Bytes (New-TestProfileBytes -Development)).IsAppStore 'a profile that lists devices is not'
}

Test-Case 'apple: a file that is not a profile is refused' {
    $threw = $false
    try { Read-CmProvisioningProfile -Bytes ([Text.Encoding]::UTF8.GetBytes('not a profile')) | Out-Null } catch { $threw = $true }
    Assert-True $threw
}

Test-Case 'apple: a key, its certificate and a profile that lists it fit together' {
    $p = New-TestPieces
    $problems = Test-CmSigningPieces -KeyPem $p.KeyPem -CertificateBytes $p.Cert -ProvisioningProfile (Read-CmProvisioningProfile -Bytes $p.Profile) -BundleId $script:TestBundle
    Assert-Equal 0 $problems.Count ($problems -join '; ')
}

Test-Case 'apple: each way the pieces can fail to fit is named' {
    $p = New-TestPieces
    $other = (New-CmSigningRequest).KeyPem
    $cases = @(
        @{ Why = 'another key'; Key = $other; Cert = $p.Cert; Profile = $p.Profile; Want = 'not made from this key' },
        @{ Why = 'a development certificate'; Key = $p.KeyPem; Cert = ($c = New-TestAppleCertificate -KeyPem $p.KeyPem -Kind 'Apple Development'); Profile = (New-TestProfileBytes -Certificates @(, $c)); Want = 'not an Apple Distribution' },
        @{ Why = 'an expired certificate'; Key = $p.KeyPem; Cert = ($e = New-TestAppleCertificate -KeyPem $p.KeyPem -Days -1); Profile = (New-TestProfileBytes -Certificates @(, $e)); Want = 'certificate expired' },
        @{ Why = 'a development profile'; Key = $p.KeyPem; Cert = $p.Cert; Profile = (New-TestProfileBytes -Certificates @(, $p.Cert) -Development); Want = 'not an App Store Connect' },
        @{ Why = 'another app'; Key = $p.KeyPem; Cert = $p.Cert; Profile = (New-TestProfileBytes -Certificates @(, $p.Cert) -BundleId 'com.example.other'); Want = "for 'com.example.other'" },
        @{ Why = 'an expired profile'; Key = $p.KeyPem; Cert = $p.Cert; Profile = (New-TestProfileBytes -Certificates @(, $p.Cert) -Days -1); Want = 'has expired' },
        @{ Why = 'another team'; Key = $p.KeyPem; Cert = $p.Cert; Profile = (New-TestProfileBytes -Certificates @(, $p.Cert) -Team 'ZZZZZ99999' -BundleId "$script:TestBundle"); Want = 'team ABCDE12345 but the profile is for team ZZZZZ99999' },
        @{ Why = 'a profile without this certificate'; Key = $p.KeyPem; Cert = $p.Cert; Profile = (New-TestProfileBytes); Want = 'does not include this certificate' }
    )
    foreach ($c in $cases) {
        $problems = Test-CmSigningPieces -KeyPem $c.Key -CertificateBytes $c.Cert -ProvisioningProfile (Read-CmProvisioningProfile -Bytes $c.Profile) -BundleId $script:TestBundle
        Assert-True (($problems -join ' | ').Contains($c.Want)) "$($c.Why): expected [$($c.Want)] in [$($problems -join ' | ')]"
    }
}

Test-Case 'apple: the .p12 opens with its password and holds the key and the certificate' {
    $p = New-TestPieces
    $bytes = New-CmSigningBundle -KeyPem $p.KeyPem -CertificateBytes $p.Cert -Password 'pw-for-test'
    $loaded = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($bytes, 'pw-for-test',
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    Assert-True $loaded.HasPrivateKey 'the key is inside'
    Assert-Equal ([System.Security.Cryptography.X509Certificates.X509Certificate2]::new($p.Cert).Thumbprint) $loaded.Thumbprint
    $threw = $false
    try { [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($bytes, 'wrong', [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet) | Out-Null } catch { $threw = $true }
    Assert-True $threw 'the wrong password does not open it'
}

function Get-TestBagKeyIds {
    <# The localKeyID attribute (DER, as hex) of the key bag and the certificate bag inside a .p12. #>
    param([byte[]]$Bytes, [string]$Password)
    $consumed = 0
    $info = [System.Security.Cryptography.Pkcs.Pkcs12Info]::Decode([ReadOnlyMemory[byte]]::new($Bytes), [ref]$consumed, $false)
    $ids = @{ key = ''; cert = '' }
    foreach ($safe in $info.AuthenticatedSafe) {
        if ($safe.ConfidentialityMode -eq [System.Security.Cryptography.Pkcs.Pkcs12ConfidentialityMode]::Password) { $safe.Decrypt($Password) }
        foreach ($bag in $safe.GetBags()) {
            $kind = if ($bag -is [System.Security.Cryptography.Pkcs.Pkcs12ShroudedKeyBag]) { 'key' } elseif ($bag -is [System.Security.Cryptography.Pkcs.Pkcs12CertBag]) { 'cert' } else { continue }
            $attr = @($bag.Attributes | Where-Object { $_.Oid.Value -eq '1.2.840.113549.1.9.21' }) | Select-Object -First 1
            if ($attr) { $ids[$kind] = [Convert]::ToHexString($attr.Values[0].RawData) }
        }
    }
    $ids
}

Test-Case 'apple: the key and the certificate in the .p12 share a localKeyID, which macOS needs to import the key with its certificate' {
    $p = New-TestPieces
    $bytes = New-CmSigningBundle -KeyPem $p.KeyPem -CertificateBytes $p.Cert -Password 'pw-for-test'
    $ids = Get-TestBagKeyIds -Bytes $bytes -Password 'pw-for-test'
    # As OpenSSL and Keychain Access write it: an OCTET STRING (04 14) holding the certificate's SHA-1 hash.
    $want = '0414' + [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($p.Cert).Thumbprint
    Assert-Equal $want $ids.key 'the key bag'
    Assert-Equal $want $ids.cert 'the certificate bag'
}

Test-Case 'apple: an API key, key ID and issuer ID are checked' {
    Assert-Equal 0 (Test-CmApiKey -KeyText (New-TestApiKeyText) -KeyId 'ABCDE12345' -IssuerId '69a6de70-0000-47e3-e053-5b8c7c11a4d1').Count
    $bad = Test-CmApiKey -KeyText 'nope' -KeyId 'abc' -IssuerId 'x'
    Assert-Equal 3 $bad.Count ($bad -join '; ')
}

# ---- publishing to GitHub --------------------------------------------------------------------------------------------

Test-Case 'publish: stores the environment, both branch rules, six secrets on standard input and three variables' {
    Initialize-Cm -Apply $true
    Set-FakeGh
    $r = Invoke-TestPublish -Pieces (New-TestPieces) -WithApiKey
    Assert-Equal 0 $r.Result.ExitCode $r.Text
    Assert-Equal 1 (Get-Calls 'gh' '*--method PUT repos/owner/repo/environments/testflight*').Count
    Assert-Equal 1 (Get-Calls 'gh' '*deployment-branch-policies -f name=main -f type=branch*').Count
    Assert-Equal 1 (Get-Calls 'gh' '*deployment-branch-policies -f name=v`* -f type=tag*').Count
    $names = @(Get-SecretSets | ForEach-Object { $_.Arguments[2] })
    Assert-Equal 'APPLE_CERTIFICATE_P12 APPLE_CERTIFICATE_PASSWORD APPLE_PROVISIONING_PROFILE ASC_API_KEY_P8 ASC_API_KEY_ID ASC_API_ISSUER_ID' ($names -join ' ')
    foreach ($s in Get-SecretSets) {
        Assert-True ([bool]$s.InputText) "$($s.Arguments[2]) has its value on standard input"
        Assert-False ($s.Display.Contains($s.InputText)) "$($s.Arguments[2]) is not on the command line"
        Assert-Match $s.Display '--env testflight'
    }
    $vars = @(Get-Calls 'gh' '*variable set*' | ForEach-Object { "$($_.Arguments[2])=$($_.Arguments[-1])" })
    Assert-Equal 3 $vars.Count
    Assert-True ($vars -contains "APPLE_TEAM_ID=$script:TestTeam") ($vars -join ', ')
    Assert-True ($vars -contains 'APPLE_PROFILE_NAME=CollectionMaxxing App Store') ($vars -join ', ')
    Assert-True ([bool]($vars | Where-Object { $_ -match '^APPLE_SIGNING_EXPIRES=\d{4}-\d{2}-\d{2}$' })) ($vars -join ', ')
}

Test-Case 'publish: the stored .p12 opens with the stored password, and no secret reaches the output' {
    Initialize-Cm -Apply $true
    Set-FakeGh
    $r = Invoke-TestPublish -Pieces (New-TestPieces) -WithApiKey
    $p12 = (Get-SecretSets | Where-Object { $_.Arguments[2] -eq 'APPLE_CERTIFICATE_P12' }).InputText
    $password = (Get-SecretSets | Where-Object { $_.Arguments[2] -eq 'APPLE_CERTIFICATE_PASSWORD' }).InputText
    $loaded = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new([Convert]::FromBase64String($p12), $password,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    Assert-True $loaded.HasPrivateKey
    Assert-False ($r.Text.Contains($password)) 'the password is not printed'
    Assert-False ($r.Text.Contains($p12.Substring(0, 40))) 'the .p12 is not printed'
}

Test-Case 'publish: the expiry it records is the earlier of the certificate and the profile' {
    Initialize-Cm -Apply $true
    Set-FakeGh
    $p = New-TestPieces
    $p.Profile = New-TestProfileBytes -Certificates @(, $p.Cert) -Days 40
    $r = Invoke-TestPublish -Pieces $p
    Assert-Equal ([DateTime]::UtcNow.AddDays(40).ToString('yyyy-MM-dd')) $r.Result.Expires.ToString('yyyy-MM-dd')
}

Test-Case 'publish: the yearly renewal (no API key) replaces the certificate and profile and leaves the API key alone' {
    Initialize-Cm -Apply $true
    Set-FakeGh -Policies @('branch:main', 'tag:v*')
    $r = Invoke-TestPublish -Pieces (New-TestPieces)
    Assert-Equal 0 $r.Result.ExitCode $r.Text
    Assert-Equal 'APPLE_CERTIFICATE_P12 APPLE_CERTIFICATE_PASSWORD APPLE_PROVISIONING_PROFILE' ((Get-SecretSets | ForEach-Object { $_.Arguments[2] }) -join ' ')
    Assert-Equal 0 (Get-Calls 'gh' '*--method POST*').Count 'branch rules that exist are not added again'
}

Test-Case 'publish: pieces that do not fit change nothing on GitHub (exit 2)' {
    Initialize-Cm -Apply $true
    Set-FakeGh
    $p = New-TestPieces
    $p.Profile = New-TestProfileBytes -Certificates @(, $p.Cert) -BundleId 'com.example.other'
    $r = Invoke-TestPublish -Pieces $p
    Assert-Equal 2 $r.Result.ExitCode
    Assert-Equal 0 $global:CmCalls.Count 'not even a read'
    Assert-Match $r.Text 'nothing was changed'
}

Test-Case 'publish: a bad API key ID changes nothing (exit 2)' {
    Initialize-Cm -Apply $true
    Set-FakeGh
    $r = Invoke-TestPublish -Pieces (New-TestPieces) -WithApiKey -KeyId 'lower'
    Assert-Equal 2 $r.Result.ExitCode
    Assert-Equal 0 $global:CmCalls.Count
}

Test-Case 'publish: gh that is not a repo admin stops before any write (exit 3)' {
    Initialize-Cm -Apply $true
    Set-FakeGh -Admin $false
    $r = Invoke-TestPublish -Pieces (New-TestPieces) -WithApiKey
    Assert-Equal 3 $r.Result.ExitCode
    Assert-Equal 0 (Get-MutatingCalls).Count
}

Test-Case 'publish: a failed write is reported and exits 1, and the rest still runs' {
    Initialize-Cm -Apply $true
    Set-FakeGh -FailOn @('secret set APPLE_PROVISIONING_PROFILE*')
    $r = Invoke-TestPublish -Pieces (New-TestPieces)
    Assert-Equal 1 $r.Result.ExitCode
    Assert-Match $r.Text 'could not store: secret APPLE_PROVISIONING_PROFILE'
    Assert-Equal 3 (Get-Calls 'gh' '*variable set*').Count
}

Test-Case 'publish: a dry run checks the pieces, reads, and writes nothing' {
    Set-FakeGh
    $r = Invoke-TestPublish -Pieces (New-TestPieces) -WithApiKey
    Assert-Equal 0 $r.Result.ExitCode
    Assert-Equal 0 (Get-MutatingCalls).Count
    Assert-Match $r.Text 'dry run, would run: gh secret set APPLE_CERTIFICATE_P12'
    Assert-Match $r.Text 'dry run, would store'
}

# ---- the scripts themselves -------------------------------------------------------------------------------------------

function Invoke-AppleScript {
    param([string]$Name, [string[]]$Arguments)
    $pwsh = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
    if (-not $pwsh) { $pwsh = Join-Path $PSHOME 'pwsh.exe' }
    $out = & $pwsh -NoProfile -File (Join-Path $script:AppleRepoRoot "deploy/apple/$Name") @Arguments 2>&1
    [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($out | Out-String) }
}

Test-Case 'New-SigningRequest.ps1: a dry run creates nothing; -Apply creates the key and request once; -Renew sets the old key aside' {
    $root = New-TestRoot
    try {
        $dir = Join-Path $root 'secrets/apple'
        $dry = Invoke-AppleScript 'New-SigningRequest.ps1' @('-Root', $root)
        Assert-Equal 0 $dry.Code $dry.Text
        Assert-False (Test-Path -LiteralPath $dir) 'a dry run creates no folder'

        $first = Invoke-AppleScript 'New-SigningRequest.ps1' @('-Root', $root, '-Apply')
        Assert-Equal 0 $first.Code $first.Text
        $key = Get-Content -LiteralPath (Join-Path $dir 'distribution.key.pem') -Raw
        Assert-Match (Get-Content -LiteralPath (Join-Path $dir 'distribution.csr') -Raw) '^-----BEGIN CERTIFICATE REQUEST-----'

        $again = Invoke-AppleScript 'New-SigningRequest.ps1' @('-Root', $root, '-Apply')
        Assert-Match $again.Text 'exists, leaving it alone'
        Assert-Equal $key (Get-Content -LiteralPath (Join-Path $dir 'distribution.key.pem') -Raw) 'the key is never overwritten'

        $renew = Invoke-AppleScript 'New-SigningRequest.ps1' @('-Root', $root, '-Renew', '-Apply')
        Assert-Equal 0 $renew.Code $renew.Text
        Assert-True ((Get-Content -LiteralPath (Join-Path $dir 'distribution.key.pem') -Raw) -ne $key) 'a new key'
        $aside = @(Get-ChildItem -LiteralPath $dir -Filter 'distribution.key.*.pem')
        Assert-Equal 1 $aside.Count 'the old key is kept beside it'
        Assert-Equal $key (Get-Content -LiteralPath $aside[0].FullName -Raw)
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'Publish-SigningSecrets.ps1: without the key from New-SigningRequest it stops with exit 2 and calls nothing' {
    $root = New-TestRoot
    try {
        $r = Invoke-AppleScript 'Publish-SigningSecrets.ps1' @('-Root', $root, '-Certificate', 'x.cer', '-ProvisioningProfile', 'x.mobileprovision')
        Assert-Equal 2 $r.Code $r.Text
        Assert-Match $r.Text 'run New-SigningRequest.ps1 -Apply first'
    }
    finally { Remove-TestRoot $root }
}
