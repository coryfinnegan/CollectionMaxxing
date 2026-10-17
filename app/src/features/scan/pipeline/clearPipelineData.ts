// Discard, sign-out and unpair: nothing the pipeline kept on the phone survives. The still, every crop and box, the read text
// waiting to be sent and the receipt lines all go, and so does every picture file still registered outside the snap store.
import { discardAllPictures } from '../pictureFiles';
import { getPipelineStore } from './pipelineStore';
import { getSnapStore } from './snapStore';

export async function clearPipelineData(): Promise<void> {
  getPipelineStore().getState().clear();
  await getSnapStore().clear();
  await discardAllPictures(); // the capture, scaled snaps, halves, crops and downloads that are still anywhere outside the store
}
