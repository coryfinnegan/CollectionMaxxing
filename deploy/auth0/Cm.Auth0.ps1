# The Auth0 tenant as code (docs/14, AUTH0-0 #226). Dot-source after deploy/deployer/Cm.Common.ps1; it has no side
# effects when loaded.
#
# The tenant's apps, API, connections and settings are the JSON bodies in deploy/auth0/objects. Their {{PLACEHOLDERS}}
# come from the PC's secrets folder (<secrets>\auth0\*.env), so no hostname, tenant or secret is ever in the repo.
# Sync-CmAuth0Tenant finds each object by name, compares it with its body, and creates or patches only what differs.
#
# SAFETY MODEL: Cm.Common's. Every call that changes the tenant (or writes a file) is -Mutating, so without -Apply it is
# printed, not made. Every Auth0 call goes through Invoke-CmAuth0Api, which the tests replace with a fake tenant
# ($global:CmAuth0Runner). Secrets (client secrets, the Apple key, Management API tokens) join $global:CmSecrets as soon
# as they are read, so Write-CmLog masks them, and no response body is ever printed.
#Requires -Version 7.0

$script:CmAuth0Objects = [IO.Path]::Combine($PSScriptRoot, 'objects')
$script:CmAuth0BundleId = 'io.github.coryfinnegan.collectionmaxxing'
$script:CmAuth0Provisioner = 'CollectionMaxxing Provisioner'
# What the provisioner may do (D-5): exactly the calls Sync-CmAuth0Tenant makes. Nothing about users or their tokens.
$script:CmAuth0ProvisionerScopes = @(
    'read:clients', 'create:clients', 'update:clients', 'read:client_keys',
    'read:client_grants', 'create:client_grants', 'update:client_grants',
    'read:resource_servers', 'create:resource_servers', 'update:resource_servers',
    # Auth0 shows and changes a connection's options only with the *_connections_options scopes.
    'read:connections', 'create:connections', 'update:connections', 'read:connections_options', 'update:connections_options',
    'read:tenant_settings', 'update:tenant_settings', 'read:branding', 'update:branding',
    'read:attack_protection', 'update:attack_protection')
# Keys Auth0 accepts but never shows again; they're compared only when a read includes them.
$script:CmAuth0WriteOnlyKeys = @('client_secret', 'app_secret')

# ---- settings and templates -------------------------------------------------------------------------------------

function Read-CmAuth0Settings {
    <#
      Reads <SecretsDir>\auth0\{tenant,provisioner,google,apple}.env into the placeholder values. Returns
      @{ Values; Problems; HasApple; ProvisionerId; ProvisionerSecret; Dir }. Never logs a value.
      tenant.env:      AUTH0_DOMAIN (<tenant>.us.auth0.com), PUBLIC_ORIGIN (https://<this PC's Tailscale name>; falls back
                       to CM_RELYING_PARTY_ID in .env)
      provisioner.env: AUTH0_PROVISIONER_CLIENT_ID, AUTH0_PROVISIONER_CLIENT_SECRET (Apply-Auth0.ps1 -Bootstrap writes it)
      google.env:      GOOGLE_CLIENT_ID, GOOGLE_CLIENT_SECRET. Optional: without it Google uses Auth0's developer keys.
      apple.env:       APPLE_SERVICES_ID, APPLE_TEAM_ID, APPLE_KEY_ID, APPLE_KEY_FILE (the .p8, relative to auth0\).
                       Optional: without it Sign in with Apple stays off.
    #>
    param([Parameter(Mandatory)][string]$SecretsDir, [string]$EnvFile = '')
    $dir = [IO.Path]::Combine($SecretsDir, 'auth0')
    $tenant = Read-CmEnvFile ([IO.Path]::Combine($dir, 'tenant.env'))
    $prov = Read-CmEnvFile ([IO.Path]::Combine($dir, 'provisioner.env'))
    $google = Read-CmEnvFile ([IO.Path]::Combine($dir, 'google.env'))
    $apple = Read-CmEnvFile ([IO.Path]::Combine($dir, 'apple.env'))
    $problems = [System.Collections.Generic.List[string]]::new()

    $domain = "$($tenant['AUTH0_DOMAIN'])".Trim().ToLowerInvariant()
    if ($domain -notmatch '^[a-z0-9][a-z0-9-]*(\.[a-z0-9-]+)*\.auth0\.com$') {
        $problems.Add("auth0\tenant.env needs AUTH0_DOMAIN, the tenant domain like <tenant>.us.auth0.com")
    }
    $origin = "$($tenant['PUBLIC_ORIGIN'])".Trim().TrimEnd('/')
    if (-not $origin -and $EnvFile) {
        $name = "$((Read-CmEnvFile $EnvFile)['CM_RELYING_PARTY_ID'])".Trim()
        if ($name) { $origin = "https://$name" }
    }
    if ($origin -notmatch '^https://[A-Za-z0-9.-]+$') {
        $problems.Add("auth0\tenant.env needs PUBLIC_ORIGIN, the https:// address people browse to, with no path")
    }

    $values = @{
        AUTH0_DOMAIN         = $domain
        PUBLIC_ORIGIN        = $origin
        BUNDLE_ID            = $script:CmAuth0BundleId
        GOOGLE_CLIENT_ID     = "$($google['GOOGLE_CLIENT_ID'])".Trim()
        GOOGLE_CLIENT_SECRET = "$($google['GOOGLE_CLIENT_SECRET'])".Trim()
    }
    if (($values.GOOGLE_CLIENT_ID -eq '') -ne ($values.GOOGLE_CLIENT_SECRET -eq '')) {
        $problems.Add('auth0\google.env needs both GOOGLE_CLIENT_ID and GOOGLE_CLIENT_SECRET (or neither, for Auth0''s developer keys)')
    }

    $hasApple = $false
    if ($apple.Count -gt 0) {
        $keyFile = "$($apple['APPLE_KEY_FILE'])".Trim()
        if ($keyFile -and -not [IO.Path]::IsPathRooted($keyFile)) { $keyFile = [IO.Path]::Combine($dir, $keyFile) }
        $keyText = if ($keyFile -and (Test-Path -LiteralPath $keyFile)) { (Get-Content -LiteralPath $keyFile -Raw).Trim() } else { '' }
        $before = $problems.Count
        try { [System.Security.Cryptography.ECDsa]::Create().ImportFromPem($keyText) }
        catch { $problems.Add('auth0\apple.env: APPLE_KEY_FILE should name the Sign in with Apple .p8 key file') }
        if ("$($apple['APPLE_TEAM_ID'])" -cnotmatch '^[A-Z0-9]{10}$') { $problems.Add('auth0\apple.env: APPLE_TEAM_ID should be 10 letters and digits') }
        if ("$($apple['APPLE_KEY_ID'])" -cnotmatch '^[A-Z0-9]{10}$') { $problems.Add('auth0\apple.env: APPLE_KEY_ID should be 10 letters and digits') }
        if ("$($apple['APPLE_SERVICES_ID'])" -notmatch '^[A-Za-z0-9.-]+$') { $problems.Add('auth0\apple.env: APPLE_SERVICES_ID should be the Services ID, like io.github.<you>.collectionmaxxing.signin') }
        $hasApple = $problems.Count -eq $before
        $values.APPLE_SERVICES_ID = "$($apple['APPLE_SERVICES_ID'])".Trim()
        $values.APPLE_TEAM_ID = "$($apple['APPLE_TEAM_ID'])".Trim()
        $values.APPLE_KEY_ID = "$($apple['APPLE_KEY_ID'])".Trim()
        $values.APPLE_PRIVATE_KEY = $keyText
    }

    $global:CmSecrets = @(@($global:CmSecrets) + @($prov['AUTH0_PROVISIONER_CLIENT_SECRET'], $values.GOOGLE_CLIENT_SECRET, $values['APPLE_PRIVATE_KEY']) | Where-Object { $_ })
    [pscustomobject]@{
        Values            = $values
        Problems          = $problems
        HasApple          = $hasApple
        ProvisionerId     = "$($prov['AUTH0_PROVISIONER_CLIENT_ID'])".Trim()
        ProvisionerSecret = "$($prov['AUTH0_PROVISIONER_CLIENT_SECRET'])".Trim()
        Dir               = $dir
    }
}

function Expand-CmAuth0Node {
    param($Node, [hashtable]$Values)
    if ($Node -is [System.Collections.IDictionary]) {
        $out = [ordered]@{}
        foreach ($key in $Node.Keys) { $out[$key] = Expand-CmAuth0Node $Node[$key] $Values }
        return $out
    }
    if ($Node -is [System.Collections.IList]) { return , @(foreach ($item in $Node) { Expand-CmAuth0Node $item $Values }) }
    if ($Node -is [string]) {
        $text = $Node
        foreach ($m in [regex]::Matches($Node, '\{\{([A-Z0-9_]+)\}\}')) {
            $name = $m.Groups[1].Value
            if (-not $Values.ContainsKey($name)) { throw "deploy/auth0 has no value for {{$name}}" }
            $text = $text.Replace($m.Value, [string]$Values[$name])
        }
        return $text
    }
    $Node
}

function Expand-CmAuth0Template {
    <# Reads deploy/auth0/objects/<Name>.json and fills its placeholders. Values are filled after parsing, so any text is safe. #>
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][hashtable]$Values)
    $path = [IO.Path]::Combine($script:CmAuth0Objects, "$Name.json")
    Expand-CmAuth0Node (ConvertFrom-Json -InputObject (Get-Content -LiteralPath $path -Raw) -AsHashtable -NoEnumerate) $Values
}

function Remove-CmAuth0Empty {
    <# Drops empty-string values from a dictionary, so an unset optional secret means "not set" rather than "". #>
    param([System.Collections.IDictionary]$Map)
    foreach ($key in @($Map.Keys)) { if ($Map[$key] -is [string] -and $Map[$key] -eq '') { $Map.Remove($key) } }
}

function Test-CmAuth0Matches {
    <#
      True when every value in Desired is the same in Current. Keys that Current lacks count as equal only when they're
      write-only secrets or empty in Desired. Lists are compared in order; text ignores case (Auth0 lowercases colours).
    #>
    param($Desired, $Current)
    if ($Desired -is [System.Collections.IDictionary]) {
        if ($Current -isnot [System.Collections.IDictionary]) { return $false }
        foreach ($key in $Desired.Keys) {
            if (-not $Current.Contains($key)) {
                if ($script:CmAuth0WriteOnlyKeys -contains $key -or "$($Desired[$key])" -eq '') { continue }
                return $false
            }
            if (-not (Test-CmAuth0Matches $Desired[$key] $Current[$key])) { return $false }
        }
        return $true
    }
    if ($Desired -is [System.Collections.IList]) {
        if ($Current -isnot [System.Collections.IList] -or $Desired.Count -ne $Current.Count) { return $false }
        for ($i = 0; $i -lt $Desired.Count; $i++) { if (-not (Test-CmAuth0Matches $Desired[$i] $Current[$i])) { return $false } }
        return $true
    }
    "$Desired" -eq "$Current"
}

# ---- the one seam for Auth0 calls ----------------------------------------------------------------------------------

function Invoke-CmAuth0Real {
    param([string]$Method, [string]$Uri, [string]$Json, [string]$Token)
    $headers = @{ Accept = 'application/json' }
    if ($Token) { $headers.Authorization = "Bearer $Token" }
    $params = @{ Method = $Method; Uri = $Uri; Headers = $headers; SkipHttpErrorCheck = $true; TimeoutSec = 30 }
    if ($Json) { $params.Body = [Text.Encoding]::UTF8.GetBytes($Json); $params.ContentType = 'application/json' }
    try {
        $r = Invoke-WebRequest @params
        $text = if ($r.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($r.Content) } else { "$($r.Content)" }
        $body = if ($text.Trim()) { try { ConvertFrom-Json -InputObject $text -AsHashtable -NoEnumerate } catch { @{ message = 'the reply was not JSON' } } } else { $null }
        [pscustomobject]@{ Status = [int]$r.StatusCode; Body = $body }
    }
    catch {
        [pscustomobject]@{ Status = 0; Body = @{ message = $_.Exception.Message } }
    }
}

function Invoke-CmAuth0Api {
    <#
      One Auth0 call; returns @{ Status; Body } with Body parsed into hashtables. -Mutating calls only go out in apply
      mode; otherwise they're logged and a dry-run result (Status 0, DryRun) comes back. Bodies are never logged.
      Rate limits (429; 2 a second on the Free plan) are waited out, five times at most.
    #>
    param([Parameter(Mandatory)][string]$Method, [Parameter(Mandatory)][string]$Uri, $Body = $null, [string]$Token = '', [switch]$Mutating)
    $display = "$Method $($Uri -replace '^https://[^/]+', '')"
    if ($Mutating -and -not $global:CmApply) {
        Write-CmLog "dry run, would call: $display"
        return [pscustomobject]@{ Status = 0; Body = $null; DryRun = $true }
    }
    $json = if ($null -ne $Body) { ConvertTo-Json -InputObject $Body -Depth 20 -Compress } else { '' }
    for ($attempt = 1; ; $attempt++) {
        $r = if ($global:CmAuth0Runner) { & $global:CmAuth0Runner $Method $Uri $json $Mutating.IsPresent }
        elseif ($env:CM_TEST -eq '1') { throw "CM_TEST=1 but no fake Auth0 is installed; refusing to call '$display' for real." }
        else { Invoke-CmAuth0Real -Method $Method -Uri $Uri -Json $json -Token $Token }
        if ($r.Status -ne 429 -or $attempt -ge 5) { return $r }
        Start-CmSleep 2
    }
}

function Get-CmAuth0Token {
    <# A Management API token for a client-credentials app, or $null. The token joins the masked secrets. #>
    param([Parameter(Mandatory)][string]$Domain, [string]$ClientId, [string]$ClientSecret)
    if (-not $ClientId -or -not $ClientSecret) { return $null }
    $r = Invoke-CmAuth0Api -Method POST -Uri "https://$Domain/oauth/token" -Body ([ordered]@{
            grant_type = 'client_credentials'; client_id = $ClientId; client_secret = $ClientSecret; audience = "https://$Domain/api/v2/"
        })
    if ($r.Status -ne 200 -or -not $r.Body -or -not $r.Body['access_token']) {
        $why = if ($r.Body -and $r.Body['error_description']) { $r.Body['error_description'] } else { "status $($r.Status)" }
        Write-CmLog "Auth0 refused the provisioner's credentials ($why)" 'ERROR'
        return $null
    }
    $global:CmSecrets = @(@($global:CmSecrets) + $r.Body['access_token'])
    $r.Body['access_token']
}

function Invoke-CmAuth0Mgmt {
    <# A Management API call for Sync-CmAuth0Tenant. A failure is recorded on the context (with Auth0's message) and gives $null. #>
    param([Parameter(Mandatory)]$Ctx, [Parameter(Mandatory)][string]$Method, [Parameter(Mandatory)][string]$Path, $Body = $null, [switch]$Mutating)
    $r = Invoke-CmAuth0Api -Method $Method -Uri "$($Ctx.Base)$Path" -Body $Body -Token $Ctx.Token -Mutating:$Mutating
    if ($r.PSObject.Properties['DryRun'] -and $r.DryRun) { return $r }
    if ($r.Status -lt 200 -or $r.Status -ge 300) {
        $why = if ($r.Body -is [System.Collections.IDictionary] -and $r.Body['message']) { $r.Body['message'] } else { 'no details' }
        $Ctx.Failures.Add("$Method $($Path -replace '\?.*$', ''): $($r.Status) $why")
        return $null
    }
    $r
}

# ---- syncing ---------------------------------------------------------------------------------------------------------

function Sync-CmAuth0Named {
    <#
      Makes the object called Desired.name look like Desired: creates it when missing, patches it when it differs.
      Returns its id, '(new)' in a dry run that would create it, or $null on a failure. CreateOnly keys can't be patched.
    #>
    param(
        [Parameter(Mandatory)]$Ctx,
        [Parameter(Mandatory)][string]$Kind,
        [Parameter(Mandatory)][string]$Collection,
        [Parameter(Mandatory)][string]$IdKey,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Desired,
        [string[]]$CreateOnly = @()
    )
    $name = $Desired['name']
    $list = Invoke-CmAuth0Mgmt $Ctx GET "/$Collection`?per_page=100&page=0"
    if (-not $list) { return $null }
    $found = @(@($list.Body) | Where-Object { $_ -is [System.Collections.IDictionary] -and $_['name'] -eq $name })
    if ($found.Count -gt 1) { $Ctx.Failures.Add("$Kind '$name': there are $($found.Count) with this name; delete the extra ones in the Auth0 dashboard"); return $null }
    if ($found.Count -eq 0) {
        Write-CmLog "$Kind '$name': create"
        $Ctx.Changes++
        $made = Invoke-CmAuth0Mgmt $Ctx POST "/$Collection" $Desired -Mutating
        if (-not $made) { return $null }
        if ($made.PSObject.Properties['DryRun'] -and $made.DryRun) { return '(new)' }
        return $made.Body[$IdKey]
    }
    $id = $found[0][$IdKey]
    $current = Invoke-CmAuth0Mgmt $Ctx GET "/$Collection/$id"
    if (-not $current) { return $null }
    if (Test-CmAuth0Matches $Desired $current.Body) {
        Write-CmLog "$Kind '$name': no change"
        return $id
    }
    $patch = [ordered]@{}
    foreach ($key in $Desired.Keys) { if ($CreateOnly -notcontains $key) { $patch[$key] = $Desired[$key] } }
    Write-CmLog "$Kind '$name': update"
    $Ctx.Changes++
    if (-not (Invoke-CmAuth0Mgmt $Ctx PATCH "/$Collection/$id" $patch -Mutating)) { return $null }
    $id
}

function Sync-CmAuth0Singleton {
    <# Tenant settings, branding and the like: one object, read and patched in place. #>
    param([Parameter(Mandatory)]$Ctx, [Parameter(Mandatory)][string]$Kind, [Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][System.Collections.IDictionary]$Desired)
    $current = Invoke-CmAuth0Mgmt $Ctx GET $Path
    if (-not $current) { return }
    if (Test-CmAuth0Matches $Desired $current.Body) { Write-CmLog "${Kind}: no change"; return }
    Write-CmLog "${Kind}: update"
    $Ctx.Changes++
    $null = Invoke-CmAuth0Mgmt $Ctx PATCH $Path $Desired -Mutating
}

function Sync-CmAuth0Grant {
    <# The server app's Management API grant: exactly the scopes in grant-server.json. #>
    param([Parameter(Mandatory)]$Ctx, [Parameter(Mandatory)][System.Collections.IDictionary]$Desired)
    $what = "grant for 'CollectionMaxxing Server'"
    if ($Desired['client_id'] -eq '(new)') { Write-CmLog "${what}: create"; $Ctx.Changes++; return }
    $aud = [uri]::EscapeDataString($Desired['audience'])
    $list = Invoke-CmAuth0Mgmt $Ctx GET "/client-grants?client_id=$($Desired['client_id'])&audience=$aud"
    if (-not $list) { return }
    $found = @(@($list.Body) | Where-Object { $_ -is [System.Collections.IDictionary] })
    if ($found.Count -eq 0) {
        Write-CmLog "${what}: create"
        $Ctx.Changes++
        $null = Invoke-CmAuth0Mgmt $Ctx POST '/client-grants' $Desired -Mutating
        return
    }
    $want = @($Desired['scope'] | Sort-Object)
    $have = @($found[0]['scope'] | Sort-Object)
    if (($want -join ' ') -ceq ($have -join ' ')) { Write-CmLog "${what}: no change"; return }
    Write-CmLog "${what}: update"
    $Ctx.Changes++
    $null = Invoke-CmAuth0Mgmt $Ctx PATCH "/client-grants/$($found[0]['id'])" ([ordered]@{ scope = $Desired['scope'] }) -Mutating
}

function Sync-CmAuth0ConnectionClients {
    <# Turns a connection on for the Enable apps and off for the Disable apps; other apps are left alone. #>
    param([Parameter(Mandatory)]$Ctx, [Parameter(Mandatory)][string]$Name, [string]$ConnectionId, [string[]]$Enable = @(), [string[]]$Disable = @())
    if (-not $ConnectionId) { return }
    $enabled = @()
    if ($ConnectionId -ne '(new)') {
        $r = Invoke-CmAuth0Mgmt $Ctx GET "/connections/$ConnectionId/clients?take=100"
        if (-not $r) { return }
        $enabled = @(@($r.Body['clients']) | Where-Object { $_ } | ForEach-Object { $_['client_id'] })
    }
    $changes = [System.Collections.Generic.List[object]]::new()
    $pending = $false
    foreach ($id in $Enable) {
        if ($id -eq '(new)') { $pending = $true }
        elseif ($id -and $enabled -notcontains $id) { $changes.Add([ordered]@{ client_id = $id; status = $true }) }
    }
    foreach ($id in $Disable) {
        if ($id -and $id -ne '(new)' -and $enabled -contains $id) { $changes.Add([ordered]@{ client_id = $id; status = $false }) }
    }
    if ($changes.Count -eq 0 -and -not $pending) { Write-CmLog "connection '$Name' apps: no change"; return }
    $on = @($changes | Where-Object { $_.status }).Count + $(if ($pending) { 1 } else { 0 })
    $off = @($changes | Where-Object { -not $_.status }).Count
    Write-CmLog "connection '$Name' apps: turn on for $on, off for $off"
    $Ctx.Changes++
    if ($changes.Count -gt 0 -and $ConnectionId -ne '(new)') {
        $null = Invoke-CmAuth0Mgmt $Ctx PATCH "/connections/$ConnectionId/clients" $changes.ToArray() -Mutating
    }
}

function Write-CmAuth0EnvFile {
    <# Writes KEY=VALUE lines when they differ from the file. Returns $true when it changed (or would in a dry run). #>
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][System.Collections.IDictionary]$Values, [string]$Header)
    $lines = @("# $Header") + @(foreach ($key in $Values.Keys) { "$key=$($Values[$key])" })
    $text = ($lines -join "`n") + "`n"
    $old = if (Test-Path -LiteralPath $Path) { (Get-Content -LiteralPath $Path -Raw) -replace "`r`n", "`n" } else { '' }
    if ($old -ceq $text) { return $false }
    if ($global:CmApply) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
        [IO.File]::WriteAllText($Path, $text)
        Write-CmLog "wrote $Path"
    }
    else { Write-CmLog "dry run, would write $Path" }
    $true
}

function Write-CmAuth0Outputs {
    <#
      Puts the values the API and the iPhone build need in the secrets folder: auth0\api.env (CM_* keys for .env) and
      auth0\iphone.env (EXPO_PUBLIC_* for the testflight environment's secrets).
    #>
    param([Parameter(Mandatory)]$Ctx, [Parameter(Mandatory)]$Settings, [string]$WebId, [string]$IPhoneId, [string]$ServerId)
    if (@($WebId, $IPhoneId, $ServerId) -contains '(new)') { Write-CmLog 'dry run, would write auth0\api.env and auth0\iphone.env once the apps exist'; return }
    if (-not $WebId -or -not $IPhoneId -or -not $ServerId) { return }
    $secret = @{}
    foreach ($id in @($WebId, $ServerId)) {
        $r = Invoke-CmAuth0Mgmt $Ctx GET "/clients/$id`?fields=client_id,client_secret&include_fields=true"
        if (-not $r -or -not $r.Body['client_secret']) { $Ctx.Failures.Add("could not read the client secret of $id (the provisioner needs read:client_keys)"); return }
        $secret[$id] = $r.Body['client_secret']
        $global:CmSecrets = @(@($global:CmSecrets) + $secret[$id])
    }
    $v = $Settings.Values
    $api = [ordered]@{
        CM_PUBLIC_ORIGIN                  = $v.PUBLIC_ORIGIN
        CM_AUTH0_DOMAIN                   = $v.AUTH0_DOMAIN
        CM_AUTH0_WEB_CLIENT_ID            = $WebId
        CM_AUTH0_WEB_CLIENT_SECRET        = $secret[$WebId]
        CM_AUTH0_MANAGEMENT_CLIENT_ID     = $ServerId
        CM_AUTH0_MANAGEMENT_CLIENT_SECRET = $secret[$ServerId]
    }
    $iphone = [ordered]@{
        EXPO_PUBLIC_AUTH0_DOMAIN    = $v.AUTH0_DOMAIN
        EXPO_PUBLIC_AUTH0_CLIENT_ID = $IPhoneId
        EXPO_PUBLIC_API_URL         = $v.PUBLIC_ORIGIN
    }
    if (Write-CmAuth0EnvFile -Path ([IO.Path]::Combine($Settings.Dir, 'api.env')) -Values $api -Header 'Written by deploy/auth0/Apply-Auth0.ps1: copy these into .env. Secret.') { $Ctx.Changes++ }
    if (Write-CmAuth0EnvFile -Path ([IO.Path]::Combine($Settings.Dir, 'iphone.env')) -Values $iphone -Header 'Written by deploy/auth0/Apply-Auth0.ps1: the testflight environment secrets for the iPhone build.') { $Ctx.Changes++ }
}

function Sync-CmAuth0Tenant {
    <#
      Brings the tenant in line with deploy/auth0/objects and writes the generated ids and secrets to the secrets folder.
      Returns @{ ExitCode; Changes }. Exit codes: 0 done (or a dry run), 1 some calls failed (re-run; it's safe),
      2 the settings in the secrets folder are incomplete (nothing called), 3 Auth0 refused the provisioner's credentials.
    #>
    param([Parameter(Mandatory)][string]$SecretsDir, [string]$EnvFile = '')
    function Result($code, $changes = 0) { [pscustomobject]@{ ExitCode = $code; Changes = $changes } }

    $s = Read-CmAuth0Settings -SecretsDir $SecretsDir -EnvFile $EnvFile
    if (-not $s.ProvisionerId -or -not $s.ProvisionerSecret) { $s.Problems.Add('auth0\provisioner.env is missing: run Apply-Auth0.ps1 -Bootstrap -Apply once, after auth0 login') }
    if ($s.Problems.Count -gt 0) {
        foreach ($p in $s.Problems) { Write-CmLog $p 'ERROR' }
        Write-CmLog 'nothing was changed in Auth0' 'ERROR'
        return Result 2
    }
    $v = $s.Values
    $token = Get-CmAuth0Token -Domain $v.AUTH0_DOMAIN -ClientId $s.ProvisionerId -ClientSecret $s.ProvisionerSecret
    if (-not $token) { return Result 3 }
    $ctx = [pscustomobject]@{ Base = "https://$($v.AUTH0_DOMAIN)/api/v2"; Token = $token; Changes = 0; Failures = [System.Collections.Generic.List[string]]::new() }

    # Settings first: they stop a new app from getting every connection turned on.
    Sync-CmAuth0Singleton $ctx 'tenant settings' '/tenants/settings' (Expand-CmAuth0Template 'tenant-settings' $v)
    Sync-CmAuth0Singleton $ctx 'branding' '/branding' (Expand-CmAuth0Template 'branding' $v)
    Sync-CmAuth0Singleton $ctx 'brute-force protection' '/attack-protection/brute-force-protection' (Expand-CmAuth0Template 'brute-force-protection' $v)
    $null = Sync-CmAuth0Named $ctx 'API' 'resource-servers' 'id' (Expand-CmAuth0Template 'resource-server' $v) -CreateOnly 'identifier'
    $web = Sync-CmAuth0Named $ctx 'app' 'clients' 'client_id' (Expand-CmAuth0Template 'client-web' $v)
    $iphone = Sync-CmAuth0Named $ctx 'app' 'clients' 'client_id' (Expand-CmAuth0Template 'client-iphone' $v)
    $server = Sync-CmAuth0Named $ctx 'app' 'clients' 'client_id' (Expand-CmAuth0Template 'client-server' $v)
    if ($server) { Sync-CmAuth0Grant $ctx (Expand-CmAuth0Template 'grant-server' ($v + @{ SERVER_CLIENT_ID = $server })) }

    # Google and Apple are on for the web and iPhone apps only; every other connection (the password database that new
    # tenants start with, for one) is off for all three of our apps.
    $ours = @($web, $iphone, $server)
    $managed = @{}
    $google = Expand-CmAuth0Template 'connection-google' $v
    Remove-CmAuth0Empty $google['options']
    $managed['google-oauth2'] = Sync-CmAuth0Named $ctx 'connection' 'connections' 'id' $google -CreateOnly 'name', 'strategy'
    if ($s.HasApple) {
        $managed['apple'] = Sync-CmAuth0Named $ctx 'connection' 'connections' 'id' (Expand-CmAuth0Template 'connection-apple' $v) -CreateOnly 'name', 'strategy'
    }
    else { Write-CmLog "auth0\apple.env isn't there yet: Sign in with Apple stays off" }
    foreach ($name in $managed.Keys) { Sync-CmAuth0ConnectionClients $ctx $name $managed[$name] -Enable $web, $iphone -Disable $server }
    $all = Invoke-CmAuth0Mgmt $ctx GET '/connections?per_page=100&page=0&fields=id,name&include_fields=true'
    if ($all) {
        foreach ($c in @(@($all.Body) | Where-Object { $_ -is [System.Collections.IDictionary] -and -not $managed.ContainsKey($_['name']) })) {
            Sync-CmAuth0ConnectionClients $ctx $c['name'] $c['id'] -Disable $ours
        }
    }

    Write-CmAuth0Outputs $ctx $s $web $iphone $server

    if ($ctx.Failures.Count -gt 0) {
        foreach ($f in $ctx.Failures) { Write-CmLog $f 'ERROR' }
        Write-CmLog "$($ctx.Failures.Count) Auth0 call(s) failed. Fix the cause and run again; it's safe to repeat" 'ERROR'
        return Result 1 $ctx.Changes
    }
    if ($ctx.Changes -eq 0) { Write-CmLog 'the tenant matches deploy/auth0: no changes' }
    elseif ($global:CmApply) { Write-CmLog "made $($ctx.Changes) change(s)" }
    else { Write-CmLog "dry run: $($ctx.Changes) change(s) to make; run again with -Apply" }
    Result 0 $ctx.Changes
}

# ---- one-time bootstrap ------------------------------------------------------------------------------------------

function ConvertFrom-CmCliJson {
    <# The JSON part of an auth0 CLI reply (its notices go to stderr, which Invoke-Cm merges in). $null when there's none. #>
    param([string[]]$Output)
    $text = ($Output -join "`n")
    $start = $text.IndexOfAny([char[]]'[{')
    if ($start -lt 0) { return $null }
    try { ConvertFrom-Json -InputObject $text.Substring($start) -AsHashtable -NoEnumerate } catch { $null }
}

function Invoke-CmAuth0Bootstrap {
    <#
      Run once, after the owner approves `auth0 login` (docs/14 step 2). With the CLI's own session it creates the
      provisioner app, grants it $CmAuth0ProvisionerScopes on the Management API, writes its credentials to
      auth0\provisioner.env, and logs the CLI out. Safe to re-run. Returns @{ ExitCode }: 0 done, 1 a CLI call failed,
      2 settings incomplete.
    #>
    param([Parameter(Mandatory)][string]$SecretsDir, [Parameter(Mandatory)][string]$Auth0Exe)
    function Result($code) { [pscustomobject]@{ ExitCode = $code } }
    $cli = @{ AUTH0_CLI_DISABLE_TELEMETRY = 'true' }
    $s = Read-CmAuth0Settings -SecretsDir $SecretsDir
    $domainProblems = @($s.Problems | Where-Object { $_ -match 'AUTH0_DOMAIN' })
    if ($domainProblems.Count -gt 0) { foreach ($p in $domainProblems) { Write-CmLog $p 'ERROR' }; return Result 2 }
    $domain = $s.Values.AUTH0_DOMAIN

    $apps = Invoke-Cm -Command $Auth0Exe -Arguments @('apps', 'list', '--number', '1000', '--json', '--no-input', '--tenant', $domain) -Env $cli
    $list = ConvertFrom-CmCliJson $apps.Output
    if ($apps.ExitCode -ne 0 -or $null -eq $list) {
        Write-CmLog "auth0 apps list failed (exit $($apps.ExitCode)): is the CLI logged in to ${domain}? Run: auth0 login" 'ERROR'
        foreach ($line in @($apps.Output | Where-Object { "$_" -notmatch 'secret' } | Select-Object -Last 5)) { Write-CmLog "  $line" 'ERROR' }
        return Result 1
    }
    $found = @(@($list) | Where-Object { $_ -is [System.Collections.IDictionary] -and $_['name'] -eq $script:CmAuth0Provisioner })
    if ($found.Count -eq 0) {
        Write-CmLog "app '$($script:CmAuth0Provisioner)': create"
        $made = Invoke-Cm -Mutating -Command $Auth0Exe -Arguments @('apps', 'create', '--name', $script:CmAuth0Provisioner, '--type', 'm2m',
            '--description', 'Applies deploy/auth0 (Apply-Auth0.ps1). Tenant configuration only; no access to users.', '--reveal-secrets', '--json', '--no-input', '--tenant', $domain) -Env $cli
        if ($made.PSObject.Properties['DryRun'] -and $made.DryRun) { return Result 0 }
        $app = ConvertFrom-CmCliJson $made.Output
    }
    else {
        $shown = Invoke-Cm -Command $Auth0Exe -Arguments @('apps', 'show', $found[0]['client_id'], '--reveal-secrets', '--json', '--no-input', '--tenant', $domain) -Env $cli
        $app = ConvertFrom-CmCliJson $shown.Output
    }
    if (-not $app -or -not $app['client_id'] -or -not $app['client_secret']) { Write-CmLog 'could not read the provisioner app back from the CLI' 'ERROR'; return Result 1 }
    $global:CmSecrets = @(@($global:CmSecrets) + $app['client_secret'])
    $id = $app['client_id']

    $audience = "https://$domain/api/v2/"
    $grants = Invoke-Cm -Command $Auth0Exe -Arguments @('api', 'get', "client-grants?client_id=$id&audience=$([uri]::EscapeDataString($audience))", '--no-input', '--tenant', $domain) -Env $cli
    $existing = @(@(ConvertFrom-CmCliJson $grants.Output) | Where-Object { $_ -is [System.Collections.IDictionary] })
    if ($grants.ExitCode -ne 0) { Write-CmLog 'could not read the provisioner grant' 'ERROR'; return Result 1 }
    $scopes = @($script:CmAuth0ProvisionerScopes)
    if ($existing.Count -eq 0) {
        Write-CmLog "grant for '$($script:CmAuth0Provisioner)': create"
        $body = ConvertTo-Json -Compress -InputObject ([ordered]@{ client_id = $id; audience = $audience; scope = $scopes })
        $r = Invoke-Cm -Mutating -Command $Auth0Exe -Arguments @('api', 'post', 'client-grants', '--data', $body, '--no-input', '--tenant', $domain) -Env $cli
    }
    elseif ((@($existing[0]['scope'] | Sort-Object) -join ' ') -cne (@($scopes | Sort-Object) -join ' ')) {
        Write-CmLog "grant for '$($script:CmAuth0Provisioner)': update"
        $body = ConvertTo-Json -Compress -InputObject ([ordered]@{ scope = $scopes })
        $r = Invoke-Cm -Mutating -Command $Auth0Exe -Arguments @('api', 'patch', "client-grants/$($existing[0]['id'])", '--data', $body, '--no-input', '--tenant', $domain) -Env $cli
    }
    else { $r = [pscustomobject]@{ ExitCode = 0 } }
    if ($r.ExitCode -ne 0) { Write-CmLog 'the CLI could not store the provisioner grant' 'ERROR'; return Result 1 }

    $file = [IO.Path]::Combine($s.Dir, 'provisioner.env')
    $null = Write-CmAuth0EnvFile -Path $file -Values ([ordered]@{ AUTH0_PROVISIONER_CLIENT_ID = $id; AUTH0_PROVISIONER_CLIENT_SECRET = $app['client_secret'] }) -Header 'Written by Apply-Auth0.ps1 -Bootstrap: the provisioner app (D-5). Secret.'
    # Agents use the provisioner from now on, so the owner's CLI session isn't kept (docs/14 step 2).
    $null = Invoke-Cm -Mutating -Command $Auth0Exe -Arguments @('logout', $domain, '--no-input') -Env $cli
    Write-CmLog 'the provisioner is ready; the CLI is logged out. Next: Apply-Auth0.ps1 (a dry run), then -Apply'
    Result 0
}
