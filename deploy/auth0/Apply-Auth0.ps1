<#
.SYNOPSIS
  Applies the Auth0 tenant configuration in deploy/auth0/objects (docs/14, AUTH0-0). A dry run unless -Apply.

.DESCRIPTION
  The tenant (the API, the web, iPhone and server apps, Google and Apple, tenant settings and branding) is described by
  the JSON files in deploy/auth0/objects. This script fills their {{PLACEHOLDERS}} from <secrets>\auth0\*.env, finds
  each object by name, and creates or patches only what differs, so it's safe to run again at any time. Without -Apply
  it only lists what it would change. With -Apply it also writes:
    <secrets>\auth0\api.env     CM_PUBLIC_ORIGIN and CM_AUTH0_* for C:\CollectionMaxxing\.env (secret)
    <secrets>\auth0\iphone.env  EXPO_PUBLIC_* for the testflight environment's secrets
  Secrets are never printed.

  Set-up, once (docs/14, "What you do"):
    1. <secrets>\auth0\tenant.env with AUTH0_DOMAIN=<tenant>.us.auth0.com (and PUBLIC_ORIGIN=https://<this PC's Tailscale
       name>, if .env has no CM_RELYING_PARTY_ID).
    2. C:\CollectionMaxxing\tools\auth0\auth0.exe login  (the owner approves the device code in their browser)
    3. Apply-Auth0.ps1 -Bootstrap -Apply  creates the provisioner app, writes auth0\provisioner.env and logs the CLI out.
    4. Apply-Auth0.ps1, then Apply-Auth0.ps1 -Apply.
  Run it from PowerShell, not Git Bash: under Git Bash the CLI couldn't find its login for -Bootstrap.
  The provisioner was later granted read/update:connections_options too: Auth0 hides and locks connection options
  without them. A new -Bootstrap grants the whole list in Cm.Auth0.ps1.
  Optional files: auth0\google.env (GOOGLE_CLIENT_ID, GOOGLE_CLIENT_SECRET; without it Google uses Auth0's developer keys)
  and auth0\apple.env (APPLE_SERVICES_ID, APPLE_TEAM_ID, APPLE_KEY_ID, APPLE_KEY_FILE; without it Apple stays off).

.EXAMPLE
  pwsh -NoProfile -File deploy/auth0/Apply-Auth0.ps1
  pwsh -NoProfile -File deploy/auth0/Apply-Auth0.ps1 -Apply
  pwsh -NoProfile -File deploy/auth0/Apply-Auth0.ps1 -Bootstrap -Apply
#>
[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$Bootstrap,
    [string]$Root = 'C:\CollectionMaxxing',
    [string]$SecretsDir = '',
    [string]$Auth0Exe = ''
)
$ErrorActionPreference = 'Stop'
. ([IO.Path]::Combine($PSScriptRoot, '..', 'deployer', 'Cm.Common.ps1'))
. ([IO.Path]::Combine($PSScriptRoot, 'Cm.Auth0.ps1'))

$envFile = [IO.Path]::Combine($Root, '.env')
if (-not $SecretsDir) {
    $fromEnv = "$((Read-CmEnvFile $envFile)['CM_SECRETS_DIR'])".Trim()
    $SecretsDir = if ($fromEnv) { $fromEnv } else { [IO.Path]::Combine($Root, 'secrets') }
}
if (-not $Auth0Exe) { $Auth0Exe = [IO.Path]::Combine($Root, 'tools', 'auth0', 'auth0.exe') }
Initialize-Cm -Apply $Apply.IsPresent

$result = if ($Bootstrap) { Invoke-CmAuth0Bootstrap -SecretsDir $SecretsDir -Auth0Exe $Auth0Exe }
else { Sync-CmAuth0Tenant -SecretsDir $SecretsDir -EnvFile $envFile }
exit $result.ExitCode
