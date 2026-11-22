// Discard, sign-out and unpair: nothing the pipeline kept on the phone survives. The still, every crop and box, the read text
// waiting to be sent and the receipt lines all go, and so does every picture file still registered outside the snap store.
import { discardAllPictures, discardPicturesOfSnaps } from '../pictureFiles';
import { getPipelineStore } from './pipelineStore';
import { recheckShelfUploads, stopShelfUploads } from './shelfUpgrade';
import { getSnapStore } from './snapStore';

/**
 * With a library (Discard) only that library's snaps go: their receipt lines, their stills and crops, and their picture
 * files. Without one (sign-out, unpair) everything goes.
 */
export async function clearPipelineData(libraryId?: string): Promise<void> {
  if (libraryId === undefined) {
    stopShelfUploads(getPipelineStore()); // sign-out or unpair: a read in flight is aborted, nothing waits
    getPipelineStore().getState().clear();
    await getSnapStore().clear();
    await discardAllPictures(); // the capture, scaled snaps, halves, crops and downloads that are still anywhere outside the store
    return;
  }
  const doomed = getPipelineStore()
    .getState()
    .snaps.filter((s) => s.libraryId === libraryId)
    .map((s) => s.snapId);
  getPipelineStore().getState().clearLibrary(libraryId);
  recheckShelfUploads(getPipelineStore(), libraryId); // the snaps are gone: a read for them is aborted
  await Promise.all(doomed.map((snapId) => getSnapStore().forget(snapId)));
  await discardPicturesOfSnaps(doomed);
}
