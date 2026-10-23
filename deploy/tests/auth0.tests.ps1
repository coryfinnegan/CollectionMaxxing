# AUTH0-0 tests (#226): the tenant-as-code sync in deploy/auth0. Auth0 is an in-memory fake behind $global:CmAuth0Runner
# and the auth0 CLI is the usual recorder, so nothing reaches Auth0. Dot-sourced by Run-Tests.ps1, which loads Cm.Auth0.ps1.

$script:FakeDomain = 'cm-test.us.auth0.com'
$script:FakeProvisionerSecret = 'provisioner-secret-0123456789'

function New-FakeAuth0 {
    <# A fresh tenant as Auth0 makes it: the Management API, the password database and Google with developer keys. #>
    $global:FakeAuth0 = [pscustomobject]@{
        Next            = 0
        Calls           = [System.Collections.Generic.List[string]]::new()
        Token           = 'mgmt-token-' + [guid]::NewGuid().ToString('N')
        Secrets         = [System.Collections.Generic.List[string]]::new()
        ResourceServers = [System.Collections.Generic.List[object]]::new()
        Clients         = [System.Collections.Generic.List[object]]::new()
        Grants          = [System.Collections.Generic.List[object]]::new()
        Connections     = [System.Collections.Generic.List[object]]::new()
        Enabled         = @{}
        Singletons      = @{
            '/tenants/settings'                         = @{ friendly_name = ''; flags = @{ enable_client_connections = $true } }
            '/branding'                                 = @{}
            '/attack-protection/brute-force-protection' = @{ enabled = $true }
        }
    }
    $global:FakeAuth0.ResourceServers.Add(@{ id = 'rs-mgmt'; name = 'Auth0 Management API'; identifier = "https://$($script:FakeDomain)/api/v2/" })
    $global:FakeAuth0.Connections.Add(@{ id = 'con-db'; name = 'Username-Password-Authentication'; strategy = 'auth0'; options = @{} })
    $global:FakeAuth0.Connections.Add(@{ id = 'con-google'; name = 'google-oauth2'; strategy = 'google-oauth2'; options = @{ email = $true; profile = $true; scope = @('email', 'profile') } })
    $global:FakeAuth0.Enabled['con-db'] = [System.Collections.Generic.List[string]]::new()
    $global:FakeAuth0.Enabled['con-google'] = [System.Collections.Generic.List[string]]::new()
    $global:CmAuth0Runner = { param($Method, $Uri, $Json, $Mutating) Invoke-FakeAuth0 $Method $Uri $Json }
    $global:FakeAuth0
}

function Copy-FakeJson { param($Value) if ($null -eq $Value) { return $null } ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $Value -Depth 20) -AsHashtable -NoEnumerate }

function Hide-FakeWriteOnly {
    <# Auth0 never shows a connection's secrets again; neither does the fake. #>
    param($Connection)
    $c = Copy-FakeJson $Connection
    foreach ($k in 'client_secret', 'app_secret') { if ($c['options'] -and $c['options'].Contains($k)) { $c['options'].Remove($k) } }
    $c
}

function Invoke-FakeAuth0 {
    param([string]$Method, [string]$Uri, [string]$Json)
    $t = $global:FakeAuth0
    $path = ([uri]$Uri).AbsolutePath
    $query = ([uri]$Uri).Query
    $t.Calls.Add("$Method $path")
    $body = if ($Json) { ConvertFrom-Json -InputObject $Json -AsHashtable -NoEnumerate } else { $null }
    function Ok($value, $status = 200) { [pscustomobject]@{ Status = $status; Body = (Copy-FakeJson $value) } }
    function NotFound { [pscustomobject]@{ Status = 404; Body = @{ message = 'not found' } } }
    function NewId($prefix) { $t.Next++; "$prefix-$($t.Next)" }

    if ($path -eq '/oauth/token') {
        if ($body['client_secret'] -ne $script:FakeProvisionerSecret) { return [pscustomobject]@{ Status = 401; Body = @{ error_description = 'Unauthorized' } } }
        return Ok @{ access_token = $t.Token; expires_in = 86400 }
    }
    $api = $path -replace '^/api/v2', ''
    # A lookup, not a switch: a switch would unroll the lists into arrays.
    $lists = @{ 'resource-servers' = $t.ResourceServers; clients = $t.Clients; connections = $t.Connections }
    switch -Regex ($api) {
        '^/(tenants/settings|branding|attack-protection/brute-force-protection)$' {
            if ($Method -eq 'GET') { return Ok $t.Singletons[$api] }
            foreach ($k in $body.Keys) { $t.Singletons[$api][$k] = $body[$k] }
            return Ok $t.Singletons[$api]
        }
        '^/(resource-servers|clients|connections)$' {
            $list = $lists[$Matches[1]]
            if ($Method -eq 'GET') {
                if ($Matches[1] -eq 'connections') { return Ok (@($list | ForEach-Object { Hide-FakeWriteOnly $_ })) }
                return Ok (@($list))
            }
            $new = $body
            if ($Matches[1] -eq 'clients') {
                $new['client_id'] = NewId 'client'
                if ($new['app_type'] -ne 'native') { $new['client_secret'] = 'secret-' + [guid]::NewGuid().ToString('N'); $t.Secrets.Add($new['client_secret']) }
            }
            else { $new['id'] = NewId $Matches[1] }
            if ($Matches[1] -eq 'connections') { $t.Enabled[$new['id']] = [System.Collections.Generic.List[string]]::new() }
            $list.Add($new)
            return Ok $new 201
        }
        '^/(resource-servers|clients|connections)/([^/]+)$' {
            $kind = $Matches[1]; $id = $Matches[2]
            $list = $lists[$kind]
            $key = if ($kind -eq 'clients') { 'client_id' } else { 'id' }
            $item = $list | Where-Object { $_[$key] -eq $id } | Select-Object -First 1
            if (-not $item) { return NotFound }
            if ($Method -eq 'GET') { if ($kind -eq 'connections') { return Ok (Hide-FakeWriteOnly $item) } else { return Ok $item } }
            if ($kind -eq 'resource-servers' -and $body.Contains('identifier')) { return [pscustomobject]@{ Status = 400; Body = @{ message = 'identifier is not allowed' } } }
            foreach ($k in $body.Keys) { $item[$k] = $body[$k] }
            return Ok $item
        }
        '^/connections/([^/]+)/clients$' {
            $on = $t.Enabled[$Matches[1]]
            if ($null -eq $on) { return NotFound }
            if ($Method -eq 'GET') { return Ok @{ clients = @($on | ForEach-Object { @{ client_id = $_ } }) } }
            foreach ($c in $body) { if ($c['status']) { if (-not $on.Contains($c['client_id'])) { $on.Add($c['client_id']) } } else { $null = $on.Remove($c['client_id']) } }
            return [pscustomobject]@{ Status = 204; Body = $null }
        }
        '^/client-grants$' {
            if ($Method -eq 'GET') {
                $q = [System.Web.HttpUtility]::ParseQueryString($query)
                return Ok (@($t.Grants | Where-Object { $_['client_id'] -eq $q['client_id'] -and $_['audience'] -eq $q['audience'] }))
            }
            $body['id'] = NewId 'grant'
            $t.Grants.Add($body)
            return Ok $body 201
        }
        '^/client-grants/([^/]+)$' {
            $g = $t.Grants | Where-Object { $_['id'] -eq $Matches[1] } | Select-Object -First 1
            if (-not $g) { return NotFound }
            foreach ($k in $body.Keys) { $g[$k] = $body[$k] }
            return Ok $g
        }
    }
    [pscustomobject]@{ Status = 404; Body = @{ message = "the fake has no $Method $path" } }
}

function Get-FakeMutations { @($global:FakeAuth0.Calls | Where-Object { $_ -notmatch '^GET ' -and $_ -ne 'POST /oauth/token' }) }
function Get-FakeClient { param([string]$Name) $global:FakeAuth0.Clients | Where-Object { $_['name'] -eq $Name } | Select-Object -First 1 }
function Get-FakeConnection { param([string]$Name) $global:FakeAuth0.Connections | Where-Object { $_['name'] -eq $Name } | Select-Object -First 1 }

function New-Auth0TestSecrets {
    <# A throwaway secrets folder with tenant.env and provisioner.env (and, with -Apple, apple.env and a .p8). #>
    param([switch]$Apple, [switch]$NoProvisioner, [string]$Domain = $script:FakeDomain, [string]$Origin = 'https://pc.tailnet.ts.net')
    $root = New-TestRoot
    $dir = Join-Path (Join-Path $root 'secrets') 'auth0'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Set-Content -LiteralPath (Join-Path $dir 'tenant.env') -Value @("AUTH0_DOMAIN=$Domain", "PUBLIC_ORIGIN=$Origin")
    if (-not $NoProvisioner) {
        Set-Content -LiteralPath (Join-Path $dir 'provisioner.env') -Value @('AUTH0_PROVISIONER_CLIENT_ID=prov-id', "AUTH0_PROVISIONER_CLIENT_SECRET=$($script:FakeProvisionerSecret)")
    }
    if ($Apple) {
        $ec = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
        Set-Content -LiteralPath (Join-Path $dir 'AuthKey_ABCDE12345.p8') -Value $ec.ExportPkcs8PrivateKeyPem()
        Set-Content -LiteralPath (Join-Path $dir 'apple.env') -Value @('APPLE_SERVICES_ID=io.github.example.collectionmaxxing.signin', 'APPLE_TEAM_ID=TEAM123456', 'APPLE_KEY_ID=ABCDE12345', 'APPLE_KEY_FILE=AuthKey_ABCDE12345.p8')
    }
    [pscustomobject]@{ Root = $root; Secrets = (Join-Path $root 'secrets'); Dir = $dir }
}

function Invoke-Auth0Sync {
    <# Runs Sync-CmAuth0Tenant and returns @{ Result; Text } with everything it printed. #>
    param([string]$SecretsDir, [bool]$Apply)
    Initialize-Cm -Apply $Apply
    $out = Sync-CmAuth0Tenant -SecretsDir $SecretsDir 6>&1
    [pscustomobject]@{
        Result = @($out | Where-Object { $_ -isnot [System.Management.Automation.InformationRecord] -and $_.PSObject.Properties['ExitCode'] }) | Select-Object -Last 1
        Text   = (@($out | Where-Object { $_ -is [System.Management.Automation.InformationRecord] } | ForEach-Object { "$($_.MessageData)" }) -join "`n")
    }
}

Test-Case 'Auth0 dry run on a new tenant lists every object to create and changes nothing' {
    $s = New-Auth0TestSecrets
    try {
        $null = New-FakeAuth0
        $run = Invoke-Auth0Sync -SecretsDir $s.Secrets -Apply $false
        Assert-Equal 0 $run.Result.ExitCode
        Assert-Equal 0 (Get-FakeMutations).Count "a dry run sent: $((Get-FakeMutations) -join ', ')"
        foreach ($name in 'CollectionMaxxing API', 'CollectionMaxxing Web', 'CollectionMaxxing iPhone', 'CollectionMaxxing Server') { Assert-Match $run.Text "'$name': create" }
        Assert-Match $run.Text 'dry run: \d+ change'
        Assert-False (Test-Path -LiteralPath (Join-Path $s.Dir 'api.env')) 'a dry run wrote api.env'
    }
    finally { Remove-TestRoot $s.Root }
}

Test-Case 'Auth0 apply builds the tenant from deploy/auth0, and a second dry run finds nothing to change' {
    $s = New-Auth0TestSecrets
    try {
        $t = New-FakeAuth0
        $first = Invoke-Auth0Sync -SecretsDir $s.Secrets -Apply $true
        Assert-Equal 0 $first.Result.ExitCode $first.Text
        Assert-Equal 1 @($t.ResourceServers | Where-Object { $_['identifier'] -eq 'urn:collectionmaxxing:api' }).Count
        $web = Get-FakeClient 'CollectionMaxxing Web'; $iphone = Get-FakeClient 'CollectionMaxxing iPhone'; $server = Get-FakeClient 'CollectionMaxxing Server'
        Assert-Equal 'https://pc.tailnet.ts.net/signin-oidc' $web['callbacks'][0]
        Assert-Equal "io.github.coryfinnegan.collectionmaxxing.auth0://$($script:FakeDomain)/ios/io.github.coryfinnegan.collectionmaxxing/callback" $iphone['callbacks'][0]
        Assert-Equal 'rotating' $iphone['refresh_token']['rotation_type']
        $grant = @($t.Grants | Where-Object { $_['client_id'] -eq $server['client_id'] })
        Assert-Equal 1 $grant.Count
        Assert-Equal 'delete:grants delete:users' ((@($grant[0]['scope']) | Sort-Object) -join ' ')
        Assert-Equal "https://$($script:FakeDomain)/api/v2/" $grant[0]['audience']
        Assert-Equal "$($web['client_id']) $($iphone['client_id'])" (($t.Enabled['con-google'] | Sort-Object) -join ' ') 'Google should be on for web and iPhone only'
        Assert-Equal 0 $t.Enabled['con-db'].Count 'the password database must stay off for our apps'
        Assert-Equal $false $t.Singletons['/tenants/settings']['flags']['enable_client_connections']

        $t.Calls.Clear()
        $second = Invoke-Auth0Sync -SecretsDir $s.Secrets -Apply $false
        Assert-Equal 0 $second.Result.ExitCode
        Assert-Equal 0 $second.Result.Changes $second.Text
        Assert-Match $second.Text 'no changes'
    }
    finally { Remove-TestRoot $s.Root }
}

Test-Case 'Auth0 apply writes api.env and iphone.env with the new ids and secrets, and never prints a secret' {
    $s = New-Auth0TestSecrets
    try {
        $t = New-FakeAuth0
        $run = Invoke-Auth0Sync -SecretsDir $s.Secrets -Apply $true
        Assert-Equal 0 $run.Result.ExitCode $run.Text
        $api = Read-CmEnvFile (Join-Path $s.Dir 'api.env')
        $web = Get-FakeClient 'CollectionMaxxing Web'; $server = Get-FakeClient 'CollectionMaxxing Server'
        Assert-Equal $web['client_id'] $api['CM_AUTH0_WEB_CLIENT_ID']
        Assert-Equal $web['client_secret'] $api['CM_AUTH0_WEB_CLIENT_SECRET']
        Assert-Equal $server['client_secret'] $api['CM_AUTH0_MANAGEMENT_CLIENT_SECRET']
        Assert-Equal 'https://pc.tailnet.ts.net' $api['CM_PUBLIC_ORIGIN']
        $iphone = Read-CmEnvFile (Join-Path $s.Dir 'iphone.env')
        Assert-Equal (Get-FakeClient 'CollectionMaxxing iPhone')['client_id'] $iphone['EXPO_PUBLIC_AUTH0_CLIENT_ID']
        Assert-Equal $script:FakeDomain $iphone['EXPO_PUBLIC_AUTH0_DOMAIN']
        foreach ($secret in @($t.Secrets) + $t.Token + $script:FakeProvisionerSecret) {
            Assert-False ($run.Text.Contains($secret)) 'a secret was printed'
        }
    }
    finally { Remove-TestRoot $s.Root }
}

Test-Case 'Auth0 sync refuses incomplete settings and calls nothing' {
    foreach ($case in @(@{ Domain = 'not-a-tenant.example.com' }, @{ Origin = 'http://pc.tailnet.ts.net' }, @{ NoProvisioner = $true })) {
        $s = New-Auth0TestSecrets @case
        try {
            $t = New-FakeAuth0
            $run = Invoke-Auth0Sync -SecretsDir $s.Secrets -Apply $true
            Assert-Equal 2 $run.Result.ExitCode "for $($case.Keys)"
            Assert-Equal 0 $t.Calls.Count "for $($case.Keys)"
        }
        finally { Remove-TestRoot $s.Root }
    }
}

Test-Case 'Auth0 sync stops when Auth0 refuses the provisioner' {
    $s = New-Auth0TestSecrets
    try {
        Set-Content -LiteralPath (Join-Path $s.Dir 'provisioner.env') -Value @('AUTH0_PROVISIONER_CLIENT_ID=prov-id', 'AUTH0_PROVISIONER_CLIENT_SECRET=wrong')
        $null = New-FakeAuth0
        $run = Invoke-Auth0Sync -SecretsDir $s.Secrets -Apply $true
        Assert-Equal 3 $run.Result.ExitCode
        Assert-Equal 0 (Get-FakeMutations).Count
    }
    finally { Remove-TestRoot $s.Root }
}

Test-Case 'Auth0 turns Apple on with its key only when apple.env is there' {
    $s = New-Auth0TestSecrets -Apple
    try {
        $t = New-FakeAuth0
        $run = Invoke-Auth0Sync -SecretsDir $s.Secrets -Apply $true
        Assert-Equal 0 $run.Result.ExitCode $run.Text
        $apple = Get-FakeConnection 'apple'
        Assert-Match $apple['options']['app_secret'] 'BEGIN PRIVATE KEY'
        Assert-Equal 'TEAM123456' $apple['options']['team_id']
        Assert-Equal 2 $t.Enabled[$apple['id']].Count
        Assert-False ($run.Text.Contains('BEGIN PRIVATE KEY')) 'the Apple key was printed'
        $again = Invoke-Auth0Sync -SecretsDir $s.Secrets -Apply $false
        Assert-Equal 0 $again.Result.Changes "Auth0 never shows app_secret again, so it must not count as a change: $($again.Text)"

        Remove-Item -LiteralPath (Join-Path $s.Dir 'apple.env')
        $off = Invoke-Auth0Sync -SecretsDir $s.Secrets -Apply $true
        Assert-Equal 0 $off.Result.ExitCode
        Assert-Equal 0 $t.Enabled[$apple['id']].Count 'without apple.env our apps should be off on Apple'
    }
    finally { Remove-TestRoot $s.Root }
}

Test-Case 'Auth0 patches a drifted object without its create-only keys' {
    $s = New-Auth0TestSecrets
    try {
        $t = New-FakeAuth0
        $null = Invoke-Auth0Sync -SecretsDir $s.Secrets -Apply $true
        $rs = $t.ResourceServers | Where-Object { $_['identifier'] -eq 'urn:collectionmaxxing:api' }
        $rs['token_lifetime'] = 86400
        $run = Invoke-Auth0Sync -SecretsDir $s.Secrets -Apply $true
        Assert-Equal 0 $run.Result.ExitCode $run.Text
        Assert-Equal 3600 $rs['token_lifetime']
        Assert-Match $run.Text "API 'CollectionMaxxing API': update"
    }
    finally { Remove-TestRoot $s.Root }
}

Test-Case 'Auth0 templates fill multi-line values safely and refuse an unknown placeholder' {
    $values = @{ AUTH0_DOMAIN = 'a.us.auth0.com'; APPLE_SERVICES_ID = 's'; APPLE_TEAM_ID = 't'; APPLE_KEY_ID = 'k'; APPLE_PRIVATE_KEY = "line1`n`"quoted`"`nline3" }
    $apple = Expand-CmAuth0Template 'connection-apple' $values
    Assert-Equal $values.APPLE_PRIVATE_KEY $apple['options']['app_secret']
    $json = ConvertTo-Json -InputObject $apple -Depth 10
    Assert-False ($json -match '\{\{') 'a placeholder was left'
    $threw = $false
    try { $null = Expand-CmAuth0Template 'client-web' @{ AUTH0_DOMAIN = 'a.us.auth0.com' } } catch { $threw = $true }
    Assert-True $threw 'a missing PUBLIC_ORIGIN should throw'
}

Test-Case 'Auth0 bootstrap creates the provisioner with exactly its scopes, writes provisioner.env and logs the CLI out' {
    $s = New-Auth0TestSecrets -NoProvisioner
    try {
        Initialize-Cm -Apply $true
        $global:CmRunner = {
            param($Command, $Arguments, $EnvVars, $Mutating, $InputText)
            $line = "$Command $($Arguments -join ' ')"
            $global:CmCalls.Add([pscustomobject]@{ Command = $Command; Display = $line; Mutating = $Mutating })
            $out = switch -Regex ($line) {
                ' apps list ' { '[]' }
                ' apps create ' { '{"client_id":"prov-new","client_secret":"prov-new-secret-xyz"}' }
                ' api get client-grants' { '[]' }
                default { '' }
            }
            [pscustomobject]@{ ExitCode = 0; Output = @('notice: some CLI chatter on stderr', $out) }
        }
        $out = Invoke-CmAuth0Bootstrap -SecretsDir $s.Secrets -Auth0Exe 'auth0' 6>&1
        $result = @($out | Where-Object { $_ -isnot [System.Management.Automation.InformationRecord] }) | Select-Object -Last 1
        $text = (@($out | Where-Object { $_ -is [System.Management.Automation.InformationRecord] } | ForEach-Object { "$($_.MessageData)" }) -join "`n")
        Assert-Equal 0 $result.ExitCode $text
        $prov = Read-CmEnvFile (Join-Path $s.Dir 'provisioner.env')
        Assert-Equal 'prov-new' $prov['AUTH0_PROVISIONER_CLIENT_ID']
        Assert-Equal 'prov-new-secret-xyz' $prov['AUTH0_PROVISIONER_CLIENT_SECRET']
        $post = @(Get-Calls -Like '*api post client-grants*')
        Assert-Equal 1 $post.Count
        foreach ($scope in $script:CmAuth0ProvisionerScopes) { Assert-Match $post[0].Display $scope }
        Assert-False ($post[0].Display -match 'users') 'the provisioner must not touch users'
        Assert-Equal 1 @(Get-Calls -Like '* logout *').Count
        Assert-False ($text.Contains('prov-new-secret-xyz')) 'the provisioner secret was printed'
    }
    finally { Remove-TestRoot $s.Root }
}
