// One session's drain in the reconnect order (docs/16 §9): barcode batches first (fast, exact), then identify payloads
// in shoot order. Art-scores chain off identify's answers inside the pipeline and crops stay on the phone, so by the
// time this resolves, everything that can move has moved in order.
import { flushTray, type FlushDeps, type TrayStore } from '../tray/trayStore';
import type { StoreApi } from 'zustand/vanilla';

export interface SessionFlusher {
  flush(): Promise<void>;
}

export async function flushSession({
  tray,
  trayDeps,
  pipeline,
}: {
  tray: StoreApi<TrayStore>;
  trayDeps: FlushDeps;
  pipeline: SessionFlusher;
}): Promise<void> {
  await flushTray(tray, trayDeps);
  await pipeline.flush();
}
