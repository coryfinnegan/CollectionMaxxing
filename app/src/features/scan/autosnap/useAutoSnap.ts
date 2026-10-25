// Wires the pure machine to a ScannerSource: signals in, snaps out. A snap shows "Nice scan!" and buzzes at once (no
// network, no waiting for the picture), then takes the still, scales it and hands it to `onSnap`. The camera is free
// for the next picture as soon as the still is taken; the scaling continues behind it.
import { useCallback, useEffect, useRef, useState } from 'react';
import { haptics } from '@/lib/haptics';
import type { ScannerSource } from '../scanner/ScannerSource';
import { DEFAULT_AUTO_SNAP_CONFIG } from './defaults';
import { initialState, recordOutcome, shutter as machineShutter, step, type MachineState } from './machine';
import { prepareSnapPhoto, type Resizer, type SnapPhoto } from './snapPhoto';
import type { AutoSnapConfig, FrameSignals, SnapTrigger } from './types';

export interface SnapEvent {
  photo: SnapPhoto;
  trigger: SnapTrigger;
}

export interface AutoSnapTuning {
  flashMs?: number;
  /** How often the "nothing useful" clock is checked. */
  hintCheckMs?: number;
  resize?: Resizer;
}

/** The default `onSnap`: does nothing but count (the pipeline is CAM-3b). */
export function createCountingSnapHandler() {
  const handler = Object.assign((_event: SnapEvent) => void (handler.count += 1), { count: 0 });
  return handler;
}

/** A barcode seen this recently still counts as in view. */
const BARCODE_MEMORY_MS = 600;

export function useAutoSnap({
  source,
  onSnap,
  config = DEFAULT_AUTO_SNAP_CONFIG,
  tuning = {},
}: {
  source: ScannerSource;
  onSnap: (event: SnapEvent) => void;
  config?: AutoSnapConfig;
  tuning?: AutoSnapTuning;
}) {
  const { flashMs = 1000, hintCheckMs = 500, resize } = tuning;
  const [flash, setFlash] = useState(false);
  const [hint, setHint] = useState(false);
  const machine = useRef<MachineState>(initialState());
  const latest = useRef<FrameSignals | null>(null);
  const lastBarcodeAt = useRef(-Infinity);
  const lastBarcodeCount = useRef(0);
  const lastUsefulAt = useRef(0);
  const capturing = useRef(false);
  const flashTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const live = useRef({ onSnap, config, resize });
  useEffect(() => {
    live.current = { onSnap, config, resize };
  });

  const useful = useCallback(() => {
    lastUsefulAt.current = Date.now();
    setHint(false);
  }, []);

  const snap = useCallback(
    (trigger: SnapTrigger) => {
      if (capturing.current) return;
      capturing.current = true;
      // Feedback first, inside the 100 ms budget: nothing below waits on the camera or the network.
      setFlash(true);
      clearTimeout(flashTimer.current);
      flashTimer.current = setTimeout(() => setFlash(false), flashMs);
      void haptics.scanned();
      useful();
      void (async () => {
        let still;
        try {
          still = await source.capturePhoto();
        } catch {
          return; // no picture this time; the camera is ready again straight away
        } finally {
          capturing.current = false;
        }
        try {
          const photo = await prepareSnapPhoto(still, live.current.resize);
          live.current.onSnap({ photo, trigger });
        } catch {
          // a picture that cannot be scaled is dropped, never a crash
        }
      })();
    },
    [source, flashMs, useful],
  );

  useEffect(() => {
    const offCodes = source.start((codes) => {
      if (codes.length === 0) return;
      lastBarcodeAt.current = Date.now();
      lastBarcodeCount.current = codes.length;
      useful();
    });
    const offSignals = source.onFrameSignals((signals) => {
      const now = Date.now();
      const recentCodes = now - lastBarcodeAt.current < BARCODE_MEMORY_MS ? lastBarcodeCount.current : 0;
      const seen = { ...signals, barcodesInView: Math.max(signals.barcodesInView, recentCodes) };
      latest.current = seen;
      if (seen.barcodesInView > 0) useful();
      const out = step(machine.current, seen, now, live.current.config);
      machine.current = out.state;
      if (out.state.phase === 'candidate') useful();
      if (out.snap) snap('auto');
    });
    return () => {
      offCodes();
      offSignals();
    };
  }, [source, snap, useful]);

  // "Nothing useful" for a while: show the hint chip.
  useEffect(() => {
    lastUsefulAt.current = Date.now();
    const timer = setInterval(() => {
      if (Date.now() - lastUsefulAt.current >= live.current.config.hintAfterMs) setHint(true);
    }, hintCheckMs);
    return () => clearInterval(timer);
  }, [hintCheckMs]);

  useEffect(() => () => clearTimeout(flashTimer.current), []);

  /** The big button: a picture now, whatever the machine thinks. */
  const takePicture = useCallback(() => {
    machine.current = machineShutter(machine.current, latest.current, Date.now());
    snap('shutter');
  }, [snap]);

  /** CAM-3b tells the machine whether a picture found anything, so repeated misfires raise the thresholds. */
  const reportOutcome = useCallback((found: boolean) => {
    machine.current = recordOutcome(machine.current, found, live.current.config);
  }, []);

  return { flash, hint, takePicture, reportOutcome };
}
