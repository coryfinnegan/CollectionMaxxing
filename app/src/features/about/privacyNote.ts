// The privacy note. Each paragraph maps to a row of docs/08 "Privacy" and "On-device model risks".
// What leaves the phone, as the app actually does it:
//  - Recognition runs on the phone; only the text read from covers goes to your PC.
//  - A close call between editions posts the scan photo and the cover's outline to :verify-cover (coverVerify.ts).
//    The server compares it in memory with the candidates' covers and discards it; only the scores are kept.
//  - "Use my photo" posts one item's cover crop, which the server keeps (EXIF stripped).
//  - "Keep as unverified" (a row kept for later, in Needs attention) posts that row's crop too (UploadCandidateCrop).
//    The server keeps it, private to the library and EXIF-stripped, until the row is deleted or linked to an item
//    (the crop is deleted); adding the row to the library makes the crop its cover (PromoteProvisional).
//    An upload nobody claims by keeping the row is swept by the daily clean-up.
export const PRIVACY_NOTE: string[] = [
  'Covers are read on your iPhone by Apple’s on-device model, and nothing goes to an AI service.',
  'Your own PC keeps your library: text read from covers, barcodes, the items you confirm and picture fingerprints for instant repeats (not the pictures).',
  'A photo goes to your own PC in three cases. For a close call between editions, your scan photo and an outline of the cover are sent to be compared with each candidate’s cover. They are checked, then thrown away, never kept.',
  'The other two send the photo of one cover, which your PC keeps, private to your library, with its hidden photo details removed. One is Use my photo. The other is a row you keep as unverified: its photo stays until you delete the row or link it to an item, and adding the row makes it the cover.',
  'Catalog services get only barcodes and titles from your PC, never your photos or your account.',
  'There is no tracking or ads. You can export or delete your data in Me.',
];
