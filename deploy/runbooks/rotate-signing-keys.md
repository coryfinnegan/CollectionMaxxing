# Rotate the token signing keys (the yearly drill)

## When to use this

Once a year, after anyone else has had access to the `secrets` folder, or if the keys might have leaked (a backup of that folder in the wrong place, a lost PC). The two key files are `openiddict-signing.pfx` and `openiddict-encryption.pfx`. They are valid for 5 years, so a yearly rotation is practice as much as protection. **Cost: everyone is signed out and signs in again** (passkeys are unaffected, because they live in the database and the key ring). Pick a quiet moment.

## Steps

1. Make sure last night's backup is good (the keys are not in it, but a surprise is easier with a safety net): run the restore drill from [../RESTORE.md](../RESTORE.md) or at least check `F:\Backups\CollectionMaxxing\db` has a dump from last night.
2. Move the old keys aside (a move, not a delete; you can put them back). The shell preamble in [README.md](README.md) is needed later:
   ```powershell
   $old = "C:\CollectionMaxxing\secrets-old-$(Get-Date -Format yyyyMMdd)"
   New-Item -ItemType Directory $old
   Move-Item C:\CollectionMaxxing\secrets\openiddict-signing.pfx, C:\CollectionMaxxing\secrets\openiddict-encryption.pfx $old
   ```
   Leave `setup-code.txt` and anything else in `secrets` where it is.
3. Make the new keys. The script never overwrites an existing file; run the dry run first and read it:
   ```powershell
   pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\deployer\New-Secrets.ps1 -Path C:\CollectionMaxxing\secrets
   pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\deployer\New-Secrets.ps1 -Path C:\CollectionMaxxing\secrets -Apply
   ```
   If your `.env` sets a password for the files (`CM_CERT_PASSWORD`, see `deploy/.env.example`), pass the same value with `-Password`.
4. Restart only the api so it reads them:
   ```powershell
   docker @cm up -d --force-recreate api
   ```
5. Check the folder is still readable only by your account (folder Properties > Security) and on a BitLocker drive.
6. Sign in again on the web (passkey) and re-pair each phone from **Me > Devices** if the app asks.
7. After a week with no problems, delete the `secrets-old-<date>` folder yourself in File Explorer. Until then it is your way back: stop the api, move the old files back into `secrets`, start the api.

## How to know it worked

- `Invoke-RestMethod http://127.0.0.1:5480/health/ready` prints `Healthy` after the restart.
- Signing in with your passkey works and the library loads; each phone signs in again.
- The two new `.pfx` files have today's date (`Get-ChildItem C:\CollectionMaxxing\secrets`).
- Write the date in the drill log ([../drills/README.md](../drills/README.md)) so next year you know it was done.
