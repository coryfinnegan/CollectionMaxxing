# The PC rebooted, or Docker Desktop is off

## When to use this

The app does not answer after a Windows update, a power cut, or you quit Docker Desktop. Phones keep scanning and queue their results meanwhile; nothing is lost.

## Steps

1. Sign in to Windows. Docker Desktop and the deployer's scheduled task both start when you sign in, not before.
2. Start **Docker Desktop** if its whale icon is missing, and wait until it says "Engine running" (about a minute).
3. Run the shell preamble from [README.md](README.md), then look at the containers:
   ```powershell
   docker @cm ps
   ```
   The containers use `restart: unless-stopped`, so they come back by themselves. If one is missing, start the stack (this keeps all data):
   ```powershell
   docker @cm up -d api backup aspire-dashboard
   ```
4. Check the app answers locally, then over Tailscale from the PC:
   ```powershell
   Invoke-RestMethod http://127.0.0.1:5480/health/ready
   tailscale serve status
   ```
5. Check the deployer task is registered, and that the log has a recent line:
   ```powershell
   schtasks /Query /TN CollectionMaxxingDeployer
   Get-Content C:\CollectionMaxxing\logs\deployer.log -Tail 5
   ```
6. If Docker Desktop did not start on sign-in, turn that on in its Settings > General ("Start Docker Desktop when you sign in"). Do not change anything else about Docker or WSL.

## How to know it worked

- `/health/ready` prints `Healthy`.
- `docker @cm ps` shows `postgres`, `api`, `aspire-dashboard` and `backup` as running.
- `tailscale serve status` shows `https://pc.tailnet.ts.net` proxying to `http://127.0.0.1:5480`, and the phone (Tailscale on) opens the app.
- Queued scans on the phone resolve within a minute or two.

If the database container will not start, do not delete its volume: see [../RESTORE.md](../RESTORE.md) and ask for help.
