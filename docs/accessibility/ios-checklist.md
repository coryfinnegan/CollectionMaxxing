# iPhone accessibility checklist (about 30 minutes)

For the owner, at a checkpoint. Everything here needs the iPhone; the web side (axe, keyboard, contrast) is checked in CI.
Tick each line. If something is wrong, write what VoiceOver said instead of the expected phrase and tell the lead.

## Set up (2 minutes)

- [ ] Settings > Accessibility > VoiceOver on (or triple-click the side button once you have set it as the shortcut).
- [ ] Settings > Accessibility > Display & Text Size > Larger Text on, slider to the very right, "Larger Accessibility Sizes" on.
- [ ] Open CollectionMaxxing. Leave Reduce Motion for part 3.

VoiceOver basics: swipe right/left = next/previous item, double-tap = press, three-finger swipe = scroll, two-finger tap = pause.

## Part 1: VoiceOver (15 minutes)

One line per screen. "Reads" is what it should say when you land on it; the exact words can differ slightly, the meaning must not.

| Screen or flow | Do this | Expected |
| --- | --- | --- |
| [ ] Pair | Swipe through the screen | Heading "Pair with your server"; the code field is "…, text field"; Pair is "button" |
| [ ] Scan tab | Swipe through | The camera viewfinder with the session bar: "Add it by hand, button" (and a "Waiting: …, button" badge when the tray is non-empty). First run shows the "Scan to add" coach once with a "Start scanning, button" |
| [ ] Add it by hand | Double-tap "Add it by hand" | Library opens with the "Add an item" panel showing; focus lands in it |
| [ ] Scanner: top bar | Swipe through | "Close, button"; heading "Scanner"; "Torch, button" and, after turning it on, "Torch on, button" |
| [ ] Scanner: scan a barcode | Hold a barcode in view | Hears "Nice scan!", then "New result: <title>", then the answer, for example "<title>. You own 1" |
| [ ] Scanner: a not-found code | Scan a barcode the catalogue lacks | "New result: Code …" then "… Not in the catalogue. Try the front cover." |
| [ ] Scanner: result card | Swipe to a card | "Open <title>, <format>, <status>, button"; then "Add <title>, button" and "Add <title> to wishlist, button" |
| [ ] Scanner: shutter | Swipe to the round button | "Take picture, button" |
| [ ] Scanner: Photos | Swipe to Photos | "Photos, button" with the hint "Choose pictures from your library to read, with no camera needed". Works with the camera covered |
| [ ] Scanner: offline | Turn on Airplane Mode, scan | Hears "Queued. 1 item waiting for your server."; the offline line is read once |
| [ ] Receipt bar | Swipe to the bar | "<N items>. Show the receipt, button" (collapsed); after opening, "Hide the receipt" |
| [ ] Receipt line | Swipe to a line in the open receipt | "<title>, 1 copy"; swipe up or down shows the action "Remove from this scan"; "Add another copy of <title>, button" |
| [ ] Review | Open Review | Heading "Review"; each row reads its title and state; the checkbox-style choices say "selected" or not; "Add N items" button |
| [ ] Review: commit | Add the items | Hears "Added 3 items" (and "Undo, button" is next) |
| [ ] Review: Undo | Double-tap Undo | Hears "Undone. What you scanned is back in Review." |
| [ ] Result detail | Open a card | Heading is the title; cover reads "Cover of <title>"; Add and Wishlist buttons named as above |
| [ ] Fix it | On a not-found picture card | "Fix it: <title>, button"; the search field is "…, text field" |
| [ ] Library | Swipe through | Heading "Library"; "Add item, button"; "Search, text field"; filter chips read "All, selected, button" etc.; each tile "<title>, <format>, button" |
| [ ] Item detail | Open an item | Heading is the title; Edit, Delete and Lend are buttons; "Deleted <title>" then "Undo" is announced after deleting |
| [ ] Item edit | Open Edit | Every field has its name; "Save changes, button" |
| [ ] Wishlist, Lent out | Open each | Heading; rows read title; empty state reads its heading then its text |
| [ ] Me | Open the Me tab | Heading "Me"; each row is a button with its name |
| [ ] Locations | Open Locations | Rows read "<name>, <N> items" as one stop |
| [ ] Import and export | Open it | Heading "Export my data"; buttons named |
| [ ] Devices and browsers | Open it | Each device row reads its name and "last used"; Revoke buttons say which device |
| [ ] Recently deleted | Open it | Rows read title and days left; Restore is a button |
| [ ] Delete account | Open it | Heading; the warning is read before the button; the button says "Delete my account" |
| [ ] Settings | Open it | Switches read "on" or "off"; "About and privacy, button" |
| [ ] About | Open it | Headings "About" and "Provider data"; each provider credit reads as a link |
| [ ] Members (owner) | Open it | "Cory (you)"; "Invite someone, button"; in the panel "Name, text field", "Invite as Editor, button", "Invite as Viewer, button" |
| [ ] Run evals | Open it | Heading "Run evals"; progress is announced as it changes |
| [ ] App lock | Turn on in Settings, background and reopen | The lock screen reads what to do, and Face ID works with VoiceOver on |
| [ ] Nothing is silent | While scanning five items | Every card and every answer is spoken; no card appears without a sound |
| [ ] Nothing reads twice | Swipe slowly through the scanner | No control is read as "button, button" or announced as an unlabelled image |

## Part 2: Larger Text (8 minutes)

At the largest accessibility size, on each screen below nothing should be cut off, overlap, or need sideways scrolling, and every button must still be fully visible and tappable.

- [ ] Scan tab: both buttons visible, labels wrap.
- [ ] Scanner: top bar still usable; the result cards strip grows taller and the card text wraps (titles may stop at 2 lines, Add and Wishlist stay visible).
- [ ] Receipt bar: the summary line wraps; Review stays visible; open the receipt, long titles wrap onto more lines.
- [ ] Review: rows grow; the Add button is not clipped.
- [ ] Library: tiles reflow; chips scroll sideways (that one is expected) but are not cut off.
- [ ] Item detail and Item edit: fields grow; the keyboard does not hide the field being typed in.
- [ ] Settings, About, Members, Me, Locations: rows grow, no truncated words.

## Part 3: Reduce Motion (3 minutes)

Settings > Accessibility > Motion > Reduce Motion on, then reopen the app.

- [ ] Scan a barcode: "Nice scan!" simply appears (no pop), then goes away.
- [ ] A new result card appears in place (no slide-in).
- [ ] Swipe a receipt line half way and let go: it jumps back (no spring).
- [ ] Nothing else moves in a way that bothers you.

## Part 4: other settings (2 minutes)

- [ ] Bold Text on: nothing is cut off.
- [ ] Dark appearance: text is readable everywhere; the receipt stays light paper on purpose.
- [ ] Increase Contrast on: borders and button edges are easy to see.
- [ ] Button Shapes on: nothing looks broken.
- [ ] Switch Control or Voice Control (optional): say "Tap Add it by hand" and it works.

## Result

Date: ____  Device and iOS version: ____  Everything ticked: yes / no
Problems found (screen, what it said, what it should say):
