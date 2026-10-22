# A lost or stolen phone, and removing a member or a device

## When to use this

An iPhone with the app is lost or stolen, a family member leaves, or you just want one device or browser signed out. Revoking is instant for the refresh token; an access token already issued lasts at most 10 minutes more. Background: `docs/07-authentication-and-authorization.md`, "Devices" and "Members".

## Steps

1. From any signed-in browser, open `https://pc.tailnet.ts.net/` and go to **Me > Devices** (signed-in devices and browsers, by name with last-seen time).
2. Find the lost phone by name and last-seen time and press **Revoke**. Its tokens stop working at once and the app on it returns to the pairing screen if anyone opens it.
3. If the person is leaving the family: the **Members** screen, choose the person, **Remove**. That deletes their membership and revokes all their devices and sessions immediately. The items they added stay and still show who added them. Also remove them in App Store Connect (Users and Access), which ends their TestFlight access ([iphone-app-testflight.md](iphone-app-testflight.md)).
4. If the phone was yours and might be in someone else's hands, also:
   - sign out other browsers in **Me > Devices** (revoke every row you do not recognise);
   - in the Tailscale admin console (the machines page), disable or remove that phone, so it can no longer reach the PC at all;
   - on the phone, use Find My to lock or erase it, and change your Apple account password. That also signs it out of TestFlight.
5. If the stolen phone had the only copy of your passkey and you have no other device, make a recovery code on the PC (after the shell preamble in [README.md](README.md)):
   ```powershell
   docker @cm exec api cm-admin recovery-code
   ```
   Open the app in a browser, choose recovery, enter the one-time code and register a new passkey. Physical access to the PC is the recovery factor, so do not paste the code anywhere.
6. Pair the replacement phone from **Me > Devices** (add a device) (the QR code) and install the app from TestFlight ([iphone-app-testflight.md](iphone-app-testflight.md)).

## How to know it worked

- The old phone's row is gone from **Me > Devices** (or shows revoked) and the phone can no longer load the library.
- A removed member is missing from the **Members** screen and cannot sign in.
- In the activity log (Aspire dashboard at `http://127.0.0.1:18888`, search `device revoked` or `membership`) you see the event.
- Your new device appears in the list with a fresh last-seen time.
