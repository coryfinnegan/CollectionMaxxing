// The privacy note. Each paragraph maps to a row of docs/08 "Privacy" and "On-device model risks".
// What leaves the phone, as the app actually does it, to the server that hosts the library (never named as the owner's own PC:
// a library may be hosted by someone else):
//  - Recognition runs on the phone; only the text read from covers goes to the server.
//  - Case 1: a shelf photo goes to :read-shelf when the server reports a shelf reader (shelfUpgrade.ts). It is shrunk and
//    re-encoded first (hidden details gone); the server reads it with a free model and throws it away.
//  - Case 2: a close call between editions posts the scan photo and the cover's outline to :verify-cover (coverVerify.ts).
//    The server compares it in memory with the candidates' covers and discards it; only the scores are kept.
//  - Case 3: "Use my photo" posts one item's cover crop, which the server keeps (EXIF stripped).
//  - Case 4: "Keep as unverified" (a row kept for later, in Needs attention) posts that row's crop too (UploadCandidateCrop).
//    The server keeps it, private to the library and EXIF-stripped, until the row is deleted or linked to an item
//    (the crop is deleted); adding the row to the library makes the crop its cover (PromoteProvisional).
//    An upload nobody claims by keeping the row is swept by the daily clean-up.
export const PRIVACY_NOTE: string[] = [
  'Covers are read on your iPhone with Apple’s on-device tools. Nothing goes to an outside AI service.',
  'The server that hosts your library keeps it: text read from covers, barcodes, the items you confirm and picture fingerprints for instant repeats (not the pictures).',
  'A photo goes to that server in four cases. When it can read shelves, a shelf photo is sent smaller, with its hidden photo details removed. A free model running on that server reads the spines, then the photo is thrown away, never kept.',
  'For a close call between editions, your scan photo and an outline of the cover are sent to be compared with each candidate’s cover. They are checked, then thrown away, never kept.',
  'The other two send the photo of one cover, which the server keeps, private to your library, with its hidden photo details removed. One is Use my photo. The other is a row you keep as unverified: its photo stays until you delete the row or link it to an item, and adding the row makes it the cover.',
  'Catalog services get only barcodes and titles from that server, never your photos or your account.',
  'There is no tracking or ads. You can export or delete your data in Me.',
];
