// The privacy note. Each paragraph maps to a row of docs/08 "Privacy" and "On-device model risks".
// What leaves the phone, as the app actually does it:
//  - Recognition runs on the phone; only the text read from covers goes to your PC.
//  - A shelf photo is also posted to :read-shelf when the server reports a shelf reader (shelfUpgrade.ts). It is shrunk and
//    re-encoded first (hidden details gone); the server reads it with a free model and throws it away.
//  - A close call between editions posts the scan photo and the cover's outline to :verify-cover (coverVerify.ts).
//    The server compares it in memory with the candidates' covers and discards it; only the scores are kept.
//  - "Use my photo" posts one item's cover crop, which the server keeps (EXIF stripped).
//  - "Keep as unverified" (a row kept for later, in Needs attention) posts that row's crop too (UploadCandidateCrop).
//    The server keeps it, private to the library and EXIF-stripped, until the row is deleted or linked to an item
//    (the crop is deleted); adding the row to the library makes the crop its cover (PromoteProvisional).
//    An upload nobody claims by keeping the row is swept by the daily clean-up.
export const PRIVACY_NOTE: string[] = [
  'Covers are read on your iPhone with Apple’s on-device tools, and nothing goes to an AI service.',
  'When the server that hosts your library can read shelves, a shelf photo goes there too. A free model running on that server reads the spines, then the photo is thrown away, never kept. It leaves your iPhone smaller, with its hidden photo details removed.',
  'Your own PC keeps your library: text read from covers, barcodes, the items you confirm and picture fingerprints for instant repeats (not the pictures).',
  'A photo goes to your own PC in three cases. For a close call between editions, your scan photo and an outline of the cover are sent to be compared with each candidate’s cover. They are checked, then thrown away, never kept.',
  'The other two send the photo of one cover, which your PC keeps, private to your library, with its hidden photo details removed. One is Use my photo. The other is a row you keep as unverified: its photo stays until you delete the row or link it to an item, and adding the row makes it the cover.',
  'Catalog services get only barcodes and titles from your PC, never your photos or your account.',
  'There is no tracking or ads. You can export or delete your data in Me.',
];
