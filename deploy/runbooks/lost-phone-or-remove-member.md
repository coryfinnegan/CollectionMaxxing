# A lost or stolen phone, and removing a member or a device

## When to use this

An iPhone with the app is lost or stolen, a family member leaves, or you just want a browser signed out. Background: `docs/07-authentication-and-authorization.md`, "App sign-ins, revocation and deletion" and "Family members".

## Steps

1. From any signed-in browser, open `https://pc.tailnet.ts.net/` and go to **Settings > Devices**. It lists signed-in browsers and iPhones by name, with last-seen time.
2. Lost iPhone: press **Sign out all iPhones**. Access tokens issued before that moment stop working at once, and the refresh tokens are ended at Auth0. Auth0's Free plan can't sign out one phone alone, so this signs out **all of your iPhones**; each signs in again with one tap. For a browser, press **Revoke** on its row.
3. If the person is leaving the family: the **Members** screen, choose the person, **Remove**. That deletes their membership and signs out their browsers and iPhones. The items they added stay and still show who added them. Also remove them in App Store Connect (Users and Access), which ends their TestFlight access ([iphone-app-testflight.md](iphone-app-testflight.md)).
4. If the phone was yours and might be in someone else's hands, also:
   - revoke every browser row you do not recognise;
   - in the Tailscale admin console (the machines page), disable or remove that phone, so it can no longer reach the PC at all;
   - on the phone, use Find My to lock or erase it, and change your Apple account password. That also signs it out of TestFlight. If you signed in with Google, check that account's security page too.
5. If you have lost access to every Google or Apple account linked to you, make a recovery code on the PC (after the shell preamble in [README.md](README.md)):
   ```powershell
   docker @cm exec api cm-admin recovery-code
   ```
   Open the app in a browser, choose recovery, enter the one-time code and sign in with the account you now want to use. Physical access to the PC is the recovery factor, so do not paste the code anywhere.
6. Sign in on the replacement phone: install the app from TestFlight ([iphone-app-testflight.md](iphone-app-testflight.md)) and press **Sign in**.

## How to know it worked

- The phone can no longer load the library until someone signs in on it again, and its row in **Settings > Devices** is gone or shows a new sign-in time.
- A removed member is missing from the **Members** screen and cannot sign in.
- In the activity log (Aspire dashboard at `http://127.0.0.1:18888`, search `membership`) you see the event.
- Your new phone appears in the list with a fresh last-seen time.
