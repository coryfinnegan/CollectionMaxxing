// The hook for "items you have confirmed before match instantly, offline" (CAM-3e). It compares the crop's feature print with the
// prints of items this person has confirmed, on the phone, and answers with the release id of a close match (sent as
// `localMatchReleaseId`; the server verifies it). A hit is a hint only. The work is in `../fingerprints`.
import { activeLocalMatch } from '../fingerprints/active';

export async function localMatch(crop: { uri: string }): Promise<string | undefined> {
  return activeLocalMatch(crop);
}
