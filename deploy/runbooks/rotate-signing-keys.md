# Rotate the sign-in secrets (Auth0 rotates its own keys)

## When to use this

Auth0 signs and rotates the token keys itself, so there is no yearly key drill and no certificate on this PC. Use this runbook when a **client secret** might have leaked (a backup of the `secrets` folder or `.env` in the wrong place, a lost PC), or after anyone else has had access to them. The secrets are the web app's and the server's (Management API) client secrets. **Cost: the API restarts, and signed-in browsers stay signed in** (the cookie is ours). Pick a quiet moment.

## Steps

1. In the Auth0 dashboard, open **Applications** and choose **CollectionMaxxing Web**, then **Settings > Rotate** the client secret. Do the same for **CollectionMaxxing Server**.
2. Re-run the tenant script so `secrets\auth0\api.env` holds the new values (a dry run first, then `-Apply`; run it from PowerShell, not Git Bash):
   ```powershell
   pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\auth0\Apply-Auth0.ps1
   pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\auth0\Apply-Auth0.ps1 -Apply
   ```
3. Copy the new `CM_AUTH0_WEB_CLIENT_SECRET` and `CM_AUTH0_MANAGEMENT_CLIENT_SECRET` lines from `C:\CollectionMaxxing\secrets\auth0\api.env` into `C:\CollectionMaxxing\.env`.
4. Restart the API so it reads them (after the shell preamble in [README.md](README.md)):
   ```powershell
   docker @cm up -d api
   ```

## How to know it worked

- `Invoke-RestMethod http://127.0.0.1:5480/health/ready` says Healthy.
- You can press **Sign in** on the web and reach the library.
- **Settings > Devices > Sign out all iPhones** works (it uses the server secret). If it fails, check the API log for an Auth0 error.
