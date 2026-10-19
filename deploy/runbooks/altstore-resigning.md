# AltStore will not re-sign, and a family member's iPhone

## When to use this

The app on an iPhone stops opening after 7 days ("unable to verify" or the icon greys out), AltStore shows a red or expired badge, or you are setting up a family member. The plan and its limits are in `docs/10-hosting-and-devops.md`, "Getting the app on your iPhone for $0". Your data stays in the app and on the PC across re-signing.

## Steps

1. Check the usual cause first. AltStore re-signs only when the phone and the PC running **AltServer** are on the same Wi-Fi and both awake. On the PC: is AltServer running (tray icon), and is iTunes/Apple Devices installed as AltServer requires? On the phone: is Wi-Fi on, and Background App Refresh on for AltStore?
2. Open AltStore on the phone > My Apps and press **Refresh All** while on home Wi-Fi with the PC on. A fresh signature lasts 7 days.
3. If it fails with a sign-in error, sign in to AltStore again with that person's own free Apple ID. If it says too many app IDs, the free ID has used its 10 app IDs for the week; wait until they free up.
4. If the app already expired and will not open, install the newest `.ipa` again from AltStore (My Apps > "+" > choose the file). Your account on the phone is kept by pairing (see [lost-phone-or-remove-member.md](lost-phone-or-remove-member.md) if it asks to pair again). The newest `.ipa` is in the web app under **Settings > Get the iPhone app**, copied there by the deployer, or from the latest release on GitHub.
5. Limits to remember: 3 sideloaded apps per free Apple ID (AltStore, CollectionMaxxing, one more), 10 app IDs a week, Developer Mode on, and the developer certificate trusted (Settings > General > VPN & Device Management).
6. **A family member's iPhone** (needs their iPhone on your home Wi-Fi once):
   1. Install AltServer on the PC if it is not (it is yours already) and plug their iPhone in once to trust the computer, or use Wi-Fi sync.
   2. On their phone turn on Developer Mode (Settings > Privacy & Security), install AltStore with **their own** free Apple ID, then install the `.ipa` from the web app (**Settings > Get the iPhone app**).
   3. Pair it to the family library with an invite and the QR code ([lost-phone-or-remove-member.md](lost-phone-or-remove-member.md) lists the Devices screen). Their phone re-signs whenever it is home on Wi-Fi.
   4. Family who live elsewhere can use SideStore, which re-signs on the phone itself; family who only browse need no app (the web app works in Safari).
7. Apple changes how sideloading works from time to time. If AltStore itself stops working after an iOS update, check the AltStore site for news before changing anything, and ask the lead; the web app keeps working meanwhile.

## How to know it worked

- AltStore > My Apps shows CollectionMaxxing with a new expiry 7 days away.
- The app opens, shows the library, and a test scan resolves.
- Two or three days later (a nightly cycle at home) the expiry date has moved again without you doing anything.
