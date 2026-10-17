// The hook for "items you have confirmed before match instantly, offline" (CAM-3e). It returns nothing for now, so every
// item goes to the server. CAM-3e replaces the body: compare the crop's feature print with the user's own store and answer
// with the release id of a close match (sent as `localMatchReleaseId`).
export async function localMatch(_crop: { uri: string }): Promise<string | undefined> {
  return undefined;
}
