# Signing pieces for the TestFlight build (ADR-024), made and checked on Windows with .NET only: no Mac, no Keychain
# Access, no openssl. Dot-sourced by New-SigningRequest.ps1, Publish-SigningSecrets.ps1 and the tests; needs Cm.Common.ps1.
#Requires -Version 7.0

Add-Type -AssemblyName System.Security.Cryptography.Pkcs

$script:CmAppleKeyFile = 'distribution.key.pem'
$script:CmAppleRequestFile = 'distribution.csr'

function New-CmSigningRequest {
    <# A new RSA 2048 key and the certificate signing request Apple asks for. Apple reads only the public key from it. #>
    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=CollectionMaxxing distribution', $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    [pscustomobject]@{ KeyPem = $rsa.ExportPkcs8PrivateKeyPem(); RequestPem = $request.CreateSigningRequestPem() }
}

function ConvertFrom-CmPlistNode {
    <# One plist value as PowerShell: dict -> hashtable, array -> array, data -> byte[], date -> DateTime (UTC). #>
    param([System.Xml.XmlElement]$Node)
    switch ($Node.LocalName) {
        'dict' {
            $result = @{}
            $children = @($Node.ChildNodes | Where-Object { $_ -is [System.Xml.XmlElement] })
            for ($i = 0; $i + 1 -lt $children.Count; $i += 2) { $result[$children[$i].InnerText] = ConvertFrom-CmPlistNode $children[$i + 1] }
            return $result
        }
        'array' { return , @($Node.ChildNodes | Where-Object { $_ -is [System.Xml.XmlElement] } | ForEach-Object { , (ConvertFrom-CmPlistNode $_) }) }
        'data' { return , [Convert]::FromBase64String(($Node.InnerText -replace '\s', '')) }
        'date' { return [DateTime]::Parse($Node.InnerText, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal) }
        'true' { return $true }
        'false' { return $false }
        'integer' { return [long]$Node.InnerText }
        default { return $Node.InnerText }
    }
}

function Read-CmProvisioningProfile {
    <#
      The parts of a .mobileprovision the build needs. The file is a signed envelope around a plain XML plist, so the
      plist is read straight out of the bytes; Apple's signature on it is checked by Xcode on the Mac, not here.
    #>
    param([Parameter(Mandatory)][byte[]]$Bytes)
    $text = [Text.Encoding]::UTF8.GetString($Bytes)
    $start = $text.IndexOf('<?xml')
    $end = $text.IndexOf('</plist>')
    if ($start -lt 0 -or $end -lt $start) { throw 'this is not a provisioning profile (no plist inside)' }
    $xml = [xml]::new()
    $xml.XmlResolver = $null
    $xml.LoadXml($text.Substring($start, $end + '</plist>'.Length - $start))
    $root = ConvertFrom-CmPlistNode ($xml.DocumentElement.ChildNodes | Where-Object { $_ -is [System.Xml.XmlElement] } | Select-Object -First 1)
    $entitlements = if ($root['Entitlements']) { $root['Entitlements'] } else { @{} }
    $team = @($root['TeamIdentifier']) | Select-Object -First 1
    $appId = "$($entitlements['application-identifier'])"
    [pscustomobject]@{
        Name         = "$($root['Name'])"
        Uuid         = "$($root['UUID'])"
        TeamId       = "$team"
        BundleId     = if ($team -and $appId.StartsWith("$team.")) { $appId.Substring("$team.".Length) } else { $appId }
        Expires      = $root['ExpirationDate']
        # An App Store profile lists no devices and can't be debugged (get-task-allow is false).
        IsAppStore   = -not $root.ContainsKey('ProvisionedDevices') -and -not $root['ProvisionsAllDevices'] -and -not $entitlements['get-task-allow']
        Certificates = @($root['DeveloperCertificates'])
    }
}

function Get-CmCertificateTeam {
    <# The team ID Apple puts in a distribution certificate's OU. #>
    param([System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)
    $m = [regex]::Match($Certificate.Subject, '(?:^|,\s*)OU=([A-Z0-9]{10})(?:,|$)')
    if ($m.Success) { $m.Groups[1].Value } else { '' }
}

function Test-CmSigningPieces {
    <# Every reason the key, certificate and profile won't sign this app, in words. Empty when they fit together. #>
    param(
        [Parameter(Mandatory)][string]$KeyPem,
        [Parameter(Mandatory)][byte[]]$CertificateBytes,
        [Parameter(Mandatory)]$ProvisioningProfile,
        [Parameter(Mandatory)][string]$BundleId,
        [DateTime]$Now = [DateTime]::UtcNow
    )
    $problems = [System.Collections.Generic.List[string]]::new()
    try { $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificateBytes) }
    catch { $problems.Add('the certificate file could not be read: download the .cer again from the Apple Developer site'); return , $problems }

    $rsa = [System.Security.Cryptography.RSA]::Create()
    $rsa.ImportFromPem($KeyPem)
    $certKey = $cert.PublicKey.GetRSAPublicKey()
    if (-not $certKey -or [Convert]::ToBase64String($certKey.ExportSubjectPublicKeyInfo()) -ne [Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo())) {
        $problems.Add('the certificate was not made from this key: upload the distribution.csr from this PC when you create it')
    }
    if ($cert.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -notmatch '^(Apple|iPhone) Distribution:') {
        $problems.Add("the certificate is not an Apple Distribution certificate ($($cert.Subject))")
    }
    if ($cert.NotAfter.ToUniversalTime() -le $Now) { $problems.Add("the certificate expired on $($cert.NotAfter.ToUniversalTime().ToString('yyyy-MM-dd'))") }
    if (-not $ProvisioningProfile.IsAppStore) { $problems.Add("the profile '$($ProvisioningProfile.Name)' is not an App Store Connect distribution profile") }
    if ($ProvisioningProfile.BundleId -ne $BundleId) { $problems.Add("the profile is for '$($ProvisioningProfile.BundleId)', not '$BundleId'") }
    if (-not $ProvisioningProfile.Expires -or $ProvisioningProfile.Expires -le $Now) { $problems.Add("the profile '$($ProvisioningProfile.Name)' has expired") }
    $team = Get-CmCertificateTeam $cert
    if ($team -and $ProvisioningProfile.TeamId -and $team -ne $ProvisioningProfile.TeamId) { $problems.Add("the certificate is for team $team but the profile is for team $($ProvisioningProfile.TeamId)") }
    $listed = @($ProvisioningProfile.Certificates | Where-Object { $_ -is [byte[]] -and [Convert]::ToBase64String($_) -eq [Convert]::ToBase64String($cert.RawData) })
    if ($listed.Count -eq 0) { $problems.Add("the profile '$($ProvisioningProfile.Name)' does not include this certificate: edit the profile, tick the new certificate, and download it again") }
    , $problems
}

function New-CmSigningBundle {
    <#
      The key and certificate as one password-protected .p12, the form `security import` on the Mac runner reads. It uses
      3DES and SHA-1 inside the file because macOS's keychain import does not read every newer PKCS#12 cipher, and it
      tags both with the same localKeyID (the certificate's SHA-1, as OpenSSL does): without it macOS drops the key.
    #>
    param([Parameter(Mandatory)][string]$KeyPem, [Parameter(Mandatory)][byte[]]$CertificateBytes, [Parameter(Mandatory)][string]$Password)
    $rsa = [System.Security.Cryptography.RSA]::Create()
    $rsa.ImportFromPem($KeyPem)
    $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificateBytes)
    $pbe = [System.Security.Cryptography.PbeParameters]::new(
        [System.Security.Cryptography.PbeEncryptionAlgorithm]::TripleDes3KeyPkcs12, [System.Security.Cryptography.HashAlgorithmName]::SHA1, 2048)
    $localKeyId = [System.Security.Cryptography.Pkcs.Pkcs9LocalKeyId]::new($cert.GetCertHash())
    $keys = [System.Security.Cryptography.Pkcs.Pkcs12SafeContents]::new()
    $keys.AddShroudedKey($rsa, $Password, $pbe).Attributes.Add($localKeyId) | Out-Null
    $certs = [System.Security.Cryptography.Pkcs.Pkcs12SafeContents]::new()
    $certs.AddCertificate($cert).Attributes.Add($localKeyId) | Out-Null
    $builder = [System.Security.Cryptography.Pkcs.Pkcs12Builder]::new()
    $builder.AddSafeContentsUnencrypted($keys)
    $builder.AddSafeContentsEncrypted($certs, $Password, $pbe)
    $builder.SealWithMac($Password, [System.Security.Cryptography.HashAlgorithmName]::SHA1, 2048)
    , $builder.Encode()
}

function New-CmRandomPassword {
    $bytes = [byte[]]::new(24)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    [Convert]::ToBase64String($bytes) -replace '[+/=]', ''
}

function Test-CmApiKey {
    <# Reasons an App Store Connect API key, its key ID and its issuer ID won't work. Empty when they look right. #>
    param([string]$KeyText, [string]$KeyId, [string]$IssuerId)
    $problems = [System.Collections.Generic.List[string]]::new()
    try {
        $ec = [System.Security.Cryptography.ECDsa]::Create()
        $ec.ImportFromPem($KeyText)
    }
    catch { $problems.Add('the API key is not an App Store Connect .p8 key file') }
    if ($KeyId -cnotmatch '^[A-Z0-9]{10}$') { $problems.Add("the key ID should be 10 letters and digits, like the AuthKey_<ID>.p8 file name (got '$KeyId')") }
    if ($IssuerId -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') { $problems.Add("the issuer ID should look like a GUID (got '$IssuerId')") }
    , $problems
}

function Publish-CmSigningSecrets {
    <#
      Checks that the key, certificate, profile (and API key, when given) fit together, then stores them in the repo's
      `testflight` environment with gh: the environment is limited to main and v* tags, so pull requests never see them.
      Secret values go to gh on standard input and are masked in every log line. Returns @{ ExitCode; Expires }.
      Exit codes: 0 stored (or a dry run), 1 a gh write failed, 2 the pieces don't fit (nothing changed),
      3 gh can't reach the repo as an admin (nothing changed).
    #>
    param(
        [Parameter(Mandatory)][string]$KeyPem,
        [Parameter(Mandatory)][byte[]]$CertificateBytes,
        [Parameter(Mandatory)][byte[]]$ProfileBytes,
        [string]$ApiKeyText = '',
        [string]$KeyId = '',
        [string]$IssuerId = '',
        [Parameter(Mandatory)][string]$Repo,
        [string]$Environment = 'testflight',
        [Parameter(Mandatory)][string]$BundleId,
        [DateTime]$Now = [DateTime]::UtcNow
    )
    function Result($code, $expires = $null) { [pscustomobject]@{ ExitCode = $code; Expires = $expires } }

    $problems = [System.Collections.Generic.List[string]]::new()
    $prov = try { Read-CmProvisioningProfile -Bytes $ProfileBytes } catch { $null }
    if (-not $prov) { $problems.Add('the provisioning profile could not be read: download the .mobileprovision again') }
    else { foreach ($p in (Test-CmSigningPieces -KeyPem $KeyPem -CertificateBytes $CertificateBytes -ProvisioningProfile $prov -BundleId $BundleId -Now $Now)) { $problems.Add($p) } }
    if ($ApiKeyText -or $KeyId -or $IssuerId) { foreach ($p in (Test-CmApiKey -KeyText $ApiKeyText -KeyId $KeyId -IssuerId $IssuerId)) { $problems.Add($p) } }
    if ($problems.Count -gt 0) {
        foreach ($p in $problems) { Write-CmLog $p 'ERROR' }
        Write-CmLog 'nothing was changed on GitHub' 'ERROR'
        return Result 2
    }

    $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificateBytes)
    $expires = @($cert.NotAfter.ToUniversalTime(), $prov.Expires) | Sort-Object | Select-Object -First 1
    $password = New-CmRandomPassword
    $secrets = [ordered]@{
        APPLE_CERTIFICATE_P12      = [Convert]::ToBase64String((New-CmSigningBundle -KeyPem $KeyPem -CertificateBytes $CertificateBytes -Password $password))
        APPLE_CERTIFICATE_PASSWORD = $password
        APPLE_PROVISIONING_PROFILE = [Convert]::ToBase64String($ProfileBytes)
    }
    if ($ApiKeyText) {
        $secrets['ASC_API_KEY_P8'] = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($ApiKeyText))
        $secrets['ASC_API_KEY_ID'] = $KeyId
        $secrets['ASC_API_ISSUER_ID'] = $IssuerId
    }
    # Not secret, and the workflow logs them anyway; variables keep them readable on the environment's settings page.
    $variables = [ordered]@{ APPLE_TEAM_ID = $prov.TeamId; APPLE_PROFILE_NAME = $prov.Name; APPLE_SIGNING_EXPIRES = $expires.ToString('yyyy-MM-dd') }
    $global:CmSecrets = @(@($global:CmSecrets) + @($secrets.Values) | Where-Object { $_ })

    # Environments and their secrets need an admin of the repo.
    $admin = Invoke-Cm -Command 'gh' -Arguments @('api', "repos/$Repo", '--jq', '.permissions.admin')
    if ($admin.ExitCode -ne 0 -or "$(@($admin.Output) -join '')".Trim() -ne 'true') {
        Write-CmLog "gh could not reach $Repo as an admin; run 'gh auth login' as the repo owner first. Nothing was changed" 'ERROR'
        return Result 3
    }

    $base = "repos/$Repo/environments/$Environment"
    $failed = [System.Collections.Generic.List[string]]::new()
    $created = Invoke-Cm -Mutating -Command 'gh' -Arguments @('api', '--method', 'PUT', $base,
        '-F', 'deployment_branch_policy[protected_branches]=false', '-F', 'deployment_branch_policy[custom_branch_policies]=true')
    if ($created.ExitCode -ne 0) { $failed.Add("environment $Environment") }
    $listed = Invoke-Cm -Command 'gh' -Arguments @('api', "$base/deployment-branch-policies", '--jq', '.branch_policies[] | .type + ":" + .name')
    $have = if ($listed.ExitCode -eq 0) { @($listed.Output | ForEach-Object { "$_".Trim() }) } else { @() }
    foreach ($rule in @(@{ Type = 'branch'; Name = 'main' }, @{ Type = 'tag'; Name = 'v*' })) {
        if ($have -contains "$($rule.Type):$($rule.Name)") { continue }
        $r = Invoke-Cm -Mutating -Command 'gh' -Arguments @('api', '--method', 'POST', "$base/deployment-branch-policies", '-f', "name=$($rule.Name)", '-f', "type=$($rule.Type)")
        if ($r.ExitCode -ne 0) { $failed.Add("the $($rule.Type) rule $($rule.Name)") }
    }
    foreach ($name in $secrets.Keys) {
        $r = Invoke-Cm -Mutating -Command 'gh' -Arguments @('secret', 'set', $name, '--repo', $Repo, '--env', $Environment) -InputText $secrets[$name]
        if ($r.ExitCode -ne 0) { $failed.Add("secret $name") }
    }
    foreach ($name in $variables.Keys) {
        $r = Invoke-Cm -Mutating -Command 'gh' -Arguments @('variable', 'set', $name, '--repo', $Repo, '--env', $Environment, '--body', $variables[$name])
        if ($r.ExitCode -ne 0) { $failed.Add("variable $name") }
    }
    if ($failed.Count -gt 0) {
        Write-CmLog "gh could not store: $($failed -join ', '). Run the script again; it is safe to repeat" 'ERROR'
        return Result 1 $expires
    }
    $verb = if ($global:CmApply) { 'stored' } else { 'dry run, would store' }
    Write-CmLog "$verb the signing pieces in the $Environment environment of $Repo (team $($prov.TeamId), profile '$($prov.Name)')"
    Write-CmLog "the certificate and profile are good until $($expires.ToString('yyyy-MM-dd')); the iOS build warns 30 days before"
    Result 0 $expires
}
