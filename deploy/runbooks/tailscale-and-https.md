# Tailscale and HTTPS certificate trouble

## When to use this

The app opens at home but not out and about, the browser warns about the certificate, `https://pc.tailnet.ts.net/` times out, or the phone says it cannot reach the server. HTTPS comes from Tailscale Serve, which gets a real certificate for the PC's own `ts.net` name; there is nothing to renew by hand.

## Steps

1. On the PC, check the app itself first. If this fails, it is not Tailscale: go to [reboot-or-docker-off.md](reboot-or-docker-off.md).
   ```powershell
   Invoke-RestMethod http://127.0.0.1:5480/health/ready
   ```
2. Check Tailscale is signed in and the PC is online:
   ```powershell
   tailscale status
   ```
   If it says logged out or stopped, sign in from the Tailscale tray icon. If the PC's name changed, the web address changed too: `CM_RELYING_PARTY_ID` in `C:\CollectionMaxxing\.env` must still match the old name, or passkeys stop working, so rename the machine back instead of editing `.env`.
3. Check Serve is still set up and still the only thing configured (read-only):
   ```powershell
   tailscale serve status
   ```
   Expect `https://pc.tailnet.ts.net` proxying to `http://127.0.0.1:5480`, and no Funnel.
4. If the configuration is missing, re-apply ours. The script refuses to overwrite anything that is not exactly ours, so this is safe; read the dry run first:
   ```powershell
   pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\tailscale\Set-TailscaleServe.ps1
   pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\tailscale\Set-TailscaleServe.ps1 -Apply
   ```
5. Certificate warning: in the Tailscale admin console check HTTPS Certificates is still enabled for the tailnet and the machine name is unchanged, then reload. Certificates are fetched and renewed by Tailscale on its own.
6. On the iPhone: open the Tailscale app, make sure it is connected, and that **VPN On Demand** is on for the CollectionMaxxing app's domain (turn on Connect On Demand in the Tailscale app's settings). Then open `https://pc.tailnet.ts.net/health/ready` in Safari.
7. Never use Funnel for this app; it would publish it to the whole internet.

## How to know it worked

- `tailscale serve status` shows our one proxy and no Funnel.
- `https://pc.tailnet.ts.net/health/ready` from the phone, over cellular with Tailscale on, answers `Healthy` with a padlock and no warning.
- The app syncs and queued scans resolve.
