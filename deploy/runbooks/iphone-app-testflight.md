# The iPhone app on TestFlight: setup, family and the yearly renewal

## When to use this

- Setting up TestFlight the first time, and moving your phone off AltStore.
- Adding or removing a family member's iPhone.
- A new version did not reach the phones, or TestFlight says the build expired.
- The yearly certificate renewal. The iOS build warns 30 days before, and fails once it has expired.

Why TestFlight, and what it replaced: ADR-024 in `docs/12-decision-log.md`. Your library data lives on the PC, so nothing here can lose it. A phone only needs pairing again.

## One-time setup (about 30 minutes)

Do steps 1 to 9 on the PC, in a browser signed in to your Apple Developer account. You need `gh` signed in as the repo owner (`gh auth status`).

1. Make the signing key and the request Apple asks for. This changes nothing until you add `-Apply`:
   ```powershell
   pwsh C:\CollectionMaxxing\repo\deploy\apple\New-SigningRequest.ps1 -Apply
   ```
   It writes `distribution.key.pem` and `distribution.csr` to `C:\CollectionMaxxing\secrets\apple`. The key never leaves the PC on its own.
2. **Register the app ID.** At developer.apple.com, go to Account > Certificates, IDs & Profiles > **Identifiers** > **+** > App IDs > App.
   - Description: `CollectionMaxxing`.
   - Bundle ID: **Explicit**, `io.github.coryfinnegan.collectionmaxxing`.
   - Leave every capability off and register it.
3. **Make the certificate.** Go to **Certificates** > **+** > **Apple Distribution**, upload `distribution.csr` from step 1, and download the `.cer`.
4. **Make the profile.** Go to **Profiles** > **+** > Distribution: **App Store Connect**.
   - Choose the app ID from step 2, then the certificate from step 3.
   - Name it `CollectionMaxxing App Store` and download it.
5. **Give TestFlight a home.** At appstoreconnect.apple.com, go to Apps > **+** > New App.
   - Platform: iOS. Name: `CollectionMaxxing` (it must be unique on the App Store, so add a word if it is taken; testers see this name). Choose a language.
   - Bundle ID: the one from step 2. SKU: `collectionmaxxing`. User access: Full Access.
   - Nothing is published. The record only holds the TestFlight builds.
6. **Make the upload key.** In App Store Connect, go to Users and Access > Integrations > App Store Connect API > Team Keys > **+**.
   - Name: `GitHub TestFlight upload`. Access: **App Manager** (it can upload builds, but it can't manage certificates or users).
   - Download the `.p8`. Apple offers it only once.
   - Note the **Issuer ID** shown above the list.
7. **Store everything in GitHub.** First a dry run, which checks the pieces fit and prints what it would store:
   ```powershell
   pwsh C:\CollectionMaxxing\repo\deploy\apple\Publish-SigningSecrets.ps1 -Certificate ~\Downloads\distribution.cer -ProvisioningProfile ~\Downloads\CollectionMaxxing_App_Store.mobileprovision -ApiKey ~\Downloads\AuthKey_<KEY ID>.p8 -IssuerId <issuer id>
   ```
   - Then run it again with `-Apply`. It creates the `testflight` environment and limits it to `main` and `v*` tags, so pull requests never see the secrets. It then sets the secrets and the team, profile and expiry variables.
   - Afterwards, move the downloaded `.p8` into `C:\CollectionMaxxing\secrets\apple`, or delete it: GitHub has it now.
8. **Make the tester group.** In App Store Connect, open your app > TestFlight > Internal Testing > **+**.
   - Name the group `Household` and tick **Enable automatic distribution**.
   - Add yourself.
9. **Check the pipeline before you tag anything.** On GitHub, go to Actions > **iOS build (TestFlight)** > Run workflow, on `main`.
   - It takes about 30 minutes. Apple then processes the build for up to half an hour.
   - If a step fails, its message names what is missing or wrong. Fix it and run the workflow again.
10. **Install it on your iPhone.**
    - Install **TestFlight** from the App Store, open the invite email on the phone, and tap Install.
    - In TestFlight, open CollectionMaxxing and turn on **Automatic Updates**.
11. **Move off AltStore.** The TestFlight app is a separate install from the AltStore copy.
    - First open the AltStore copy and let its scan tray finish uploading, so nothing is waiting.
    - Pair the new app from the web app: **Settings > Devices > Add a device**.
    - Then delete the AltStore copy and AltStore itself from the phone, and revoke the old phone entry under **Devices**.
    - You can turn Developer Mode off (Settings > Privacy & Security). On the PC you can uninstall AltServer, and iTunes and iCloud too if nothing else needs them.
12. **Release new versions from now on:**
    - Bump `version` in `app/app.config.ts`, merge, then tag the merge commit (`git tag v1.0.5`, `git push origin v1.0.5`).
    - `.github/workflows/ios-build.yml` uploads it and creates the GitHub release, and the phones update themselves.

## Add a family member's iPhone

1. In App Store Connect, go to Users and Access > **+**.
   - Enter their name and the email of their Apple account.
   - Role: **Marketing**, the smallest role TestFlight accepts. Under Apps, choose only CollectionMaxxing.
   - They accept the email invite.
2. Go to TestFlight > Internal Testing > Household and add them.
3. On their iPhone: install TestFlight, accept the invite, tap Install, and turn on Automatic Updates.
4. Invite them to the library (**Members**) and pair their phone with the QR code from **Settings > Devices**, as in [lost-phone-or-remove-member.md](lost-phone-or-remove-member.md). Family who only browse need no app: the web app works in Safari.

To remove someone, do both steps. Remove them from the library ([lost-phone-or-remove-member.md](lost-phone-or-remove-member.md)), and remove them from App Store Connect (Users and Access), which ends their TestFlight access.

## A new version did not reach the phones

1. On GitHub, look at Actions > iOS build (TestFlight) for the tag.
   - Red: the failing step says what to fix.
   - "missing" means the secrets (step 7). "expired" means the renewal below.
   - "does not match app.config.ts version" means the tag and `version` differ: bump `version`, merge, and tag again.
2. In App Store Connect, check the build under TestFlight.
   - **Processing:** wait.
   - **Missing Compliance:** it should not happen, because the app declares HTTPS-only encryption. Answer "None of the algorithms mentioned above" once.
   - The build is ready but not in Household: automatic distribution is off, so add the build to the group.
3. On the phone, open TestFlight and tap Update.

## TestFlight says the build expired

A TestFlight build stops opening 90 days after upload. The workflow rebuilds the latest release on the 1st of every other month, so this only happens if those runs failed.

1. On GitHub, go to Actions > iOS build (TestFlight) and look at the latest scheduled run. Fix what its failing step says.
2. Run the workflow by hand (Run workflow, on `main`), or tag a new version.
3. Once the build is ready, open TestFlight on the phone and tap Install.

## Yearly renewal of the certificate and profile

1. Make a new key and request. The old key is kept beside it:
   ```powershell
   pwsh C:\CollectionMaxxing\repo\deploy\apple\New-SigningRequest.ps1 -Renew -Apply
   ```
2. At developer.apple.com, go to **Certificates** > **+** > Apple Distribution. Upload the new `distribution.csr` and download the `.cer`.
3. Go to **Profiles** > CollectionMaxxing App Store > Edit. Tick the new certificate, save, and download the profile.
4. Store them. Leave out the API key parameters: the stored key keeps working.
   ```powershell
   pwsh C:\CollectionMaxxing\repo\deploy\apple\Publish-SigningSecrets.ps1 -Certificate ~\Downloads\distribution.cer -ProvisioningProfile ~\Downloads\CollectionMaxxing_App_Store.mobileprovision -Apply
   ```
5. Run the workflow by hand on `main` to check it.
   - After that build reaches TestFlight, you may revoke the old certificate. Builds already in TestFlight keep working.

## How to know it worked

- The Actions run is green, and its summary says "Uploaded to TestFlight" with the version and build number.
- App Store Connect shows the build as ready under TestFlight, in the Household group.
- On the phone, TestFlight shows that version installed. The app opens, shows the library, and a test scan resolves.
- In the web app, **Settings > About** shows the same build commit as the release tag.
