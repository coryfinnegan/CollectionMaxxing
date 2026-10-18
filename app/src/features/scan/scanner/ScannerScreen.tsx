// The scanner (docs/06 M3): camera behind everything, "Nice scan!" flash, result cards, the receipt bar.
// It is given its sources, so tests and the dev menu drive it with a FakeScannerSource and no camera.
import { useRouter } from 'expo-router';
import { useContext, useEffect, useMemo, useRef, useState, type MutableRefObject, type ReactNode } from 'react';
import { Platform, Pressable, StyleSheet, View } from 'react-native';
import { SafeAreaInsetsContext } from 'react-native-safe-area-context';
import { NiceScanFlash } from '../autosnap/NiceScanFlash';
import { DEFAULT_AUTO_SNAP_CONFIG } from '../autosnap/defaults';
import { createCountingSnapHandler, useAutoSnap, type AutoSnapTuning, type SnapEvent } from '../autosnap/useAutoSnap';
import type { AutoSnapConfig } from '../autosnap/types';
import type { CandidateDto } from '@/api/generated/models';
import { ResultCards, useReduceMotion, type CardActions } from '../cards/ResultCards';
import { deviceBarcodes, devicePicker } from '../import/devicePicker';
import type { BarcodeDetector, ImportDeps, PhotoPicker } from '../import/photoImport';
import { usePhotoImport } from '../import/usePhotoImport';
import type { Resizer } from '../autosnap/snapPhoto';
import { FixItSheet } from '../pipeline/FixItSheet';
import type { PipelineDeps } from '../pipeline/SnapPipeline';
import { usePipeline } from '../pipeline/usePipeline';
import { useCardActions } from '../session/useCardActions';
import { useLibraryRole, useScanSession, type SessionTuning } from '../session/useScanSession';
import { ReceiptBar } from '../tray/ReceiptBar';
import { spacing, useColors } from '@/theme/tokens';
import { Button, Chip, Text } from '@/ui';
import { DEV_SIX_CODES } from './devCodes';
import { FakeScannerSource, type ScannerSource } from './ScannerSource';

type Props = {
  libraryId: string;
  source: ScannerSource;
  /** The camera, drawn behind everything. Absent in tests. */
  preview?: ReactNode;
  /** In dev builds only: the fake camera the "Scan these 6 codes" button drives. */
  devSource?: FakeScannerSource;
  tuning?: SessionTuning;
  /** What Close does; defaults to going back. The route passes the close-scanner sheet here. */
  onClose?: () => void;
  /** Each picture, from the shutter or auto-snap. Without this and without a `pipeline`, pictures are only counted. */
  onSnap?: (event: SnapEvent) => void;
  /** The picture pipeline: read on the phone, identified from text, close calls compared by artwork. The route builds it. */
  pipeline?: PipelineDeps;
  /** The auto-snap thresholds. The route passes the server's values (cached on the phone). */
  autoSnapConfig?: AutoSnapConfig;
  autoSnapTuning?: AutoSnapTuning;
  /** Filled with a function that tells auto-snap whether the last picture found anything (misfires raise the thresholds). */
  outcomeRef?: MutableRefObject<((found: boolean) => void) | null>;
  /** Import from Photos. Tests pass a scripted picker; the phone uses the system one. */
  photoImport?: { picker?: PhotoPicker; vision?: () => BarcodeDetector | Promise<BarcodeDetector>; resize?: Resizer };
};

export function ScannerScreen({
  libraryId,
  source,
  preview,
  devSource,
  tuning,
  onClose,
  onSnap,
  pipeline,
  autoSnapConfig = DEFAULT_AUTO_SNAP_CONFIG,
  autoSnapTuning,
  outcomeRef,
  photoImport,
}: Props) {
  const router = useRouter();
  const colors = useColors();
  const insets = useContext(SafeAreaInsetsContext) ?? { top: 0, bottom: 0, left: 0, right: 0 };
  // Barcodes found in imported pictures arrive here and take the same path as the camera's.
  const importCodes = useMemo(() => new FakeScannerSource(), []);
  const sources = useMemo(() => (devSource ? [source, devSource, importCodes] : [source, importCodes]), [source, devSource, importCodes]);
  const session = useScanSession({ libraryId, sources, tuning });
  const { canAdd } = useLibraryRole(libraryId);
  const cardActions = useCardActions({ libraryId, sessionId: session.sessionId, canAdd });
  const [fixing, setFixing] = useState<CandidateDto | null>(null);
  const actions: CardActions = useMemo(() => ({ ...cardActions, onFixIt: setFixing }), [cardActions]);
  const [torch, setTorch] = useState(false);
  const reduceMotion = useReduceMotion();
  const [counter] = useState(() => createCountingSnapHandler());
  const reportOutcome = useRef<(found: boolean) => void>(() => undefined);
  const pictures = usePipeline({ deps: pipeline, source, resendMs: tuning?.resendMs, onOutcome: (found) => reportOutcome.current(found) });
  const importDeps = useMemo<ImportDeps | undefined>(
    () =>
      pictures.enabled && Platform.OS !== 'web' && pictures.recognition
        ? {
            picker: photoImport?.picker ?? devicePicker,
            vision: photoImport?.vision ?? deviceBarcodes,
            snap: pictures.snapWith,
            recognition: pictures.recognition,
            resize: photoImport?.resize,
            onCodes: (codes) => importCodes.inject(codes),
          }
        : undefined,
    [pictures.enabled, pictures.snapWith, pictures.recognition, photoImport, importCodes],
  );
  const photos = usePhotoImport(importDeps);
  const autoSnap = useAutoSnap({
    source,
    onSnap: onSnap ?? (pictures.enabled ? pictures.onSnap : counter),
    config: autoSnapConfig,
    tuning: { flashMs: tuning?.flashMs, ...autoSnapTuning },
  });
  useEffect(() => {
    reportOutcome.current = autoSnap.reportOutcome;
  }, [autoSnap.reportOutcome]);
  useEffect(() => {
    if (!outcomeRef) return;
    outcomeRef.current = autoSnap.reportOutcome;
    return () => {
      outcomeRef.current = null;
    };
  }, [outcomeRef, autoSnap.reportOutcome]);

  const toggleTorch = () => {
    const next = !torch;
    setTorch(next);
    source.setTorch(next);
  };

  return (
    <View style={{ flex: 1, backgroundColor: '#000000' }}>
      {preview}
      <View pointerEvents="box-none" style={[StyleSheet.absoluteFill, { paddingTop: insets.top + spacing.sm }]}>
        <View style={{ flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', paddingHorizontal: spacing.lg }}>
          <Button label="Close" variant="secondary" onPress={onClose ?? (() => router.back())} style={{ backgroundColor: 'rgba(0,0,0,0.5)' }} />
          <Text variant="heading" accessibilityRole="header" style={{ color: '#FFFFFF' }}>
            Scanner
          </Text>
          <Button
            label={torch ? 'Torch on' : 'Torch'}
            variant="secondary"
            accessibilityState={{ selected: torch }}
            onPress={toggleTorch}
            style={{ backgroundColor: torch ? colors.scan : 'rgba(0,0,0,0.5)' }}
          />
        </View>

        <View pointerEvents="none" style={{ flex: 1, alignItems: 'center', justifyContent: 'center' }}>
          <NiceScanFlash visible={session.showNiceScan || autoSnap.flash} reduceMotion={reduceMotion} />
        </View>

        {devSource ? (
          <View style={{ alignItems: 'center', gap: spacing.sm, paddingBottom: spacing.sm }}>
            <Button
              label="Scan these 6 codes"
              variant="secondary"
              onPress={() => devSource.inject(DEV_SIX_CODES)}
              style={{ backgroundColor: 'rgba(0,0,0,0.5)' }}
            />
            {pictures.enabled ? (
              <Button label="Snap a fixture shelf" variant="secondary" onPress={pictures.replayFixture} style={{ backgroundColor: 'rgba(0,0,0,0.5)' }} />
            ) : null}
          </View>
        ) : null}

        <View pointerEvents="box-none" style={{ alignItems: 'center', gap: spacing.md, paddingBottom: spacing.md }}>
          {pictures.enabled && pictures.unreadableCount > 0 ? (
            <Chip label={`Get closer for these ${pictures.unreadableCount}`} onPress={pictures.dismissUnreadable} />
          ) : null}
          {photos.running && photos.progress ? (
            <View style={{ flexDirection: 'row', alignItems: 'center', gap: spacing.md }}>
              <Text style={{ color: '#FFFFFF' }}>{photos.progress}</Text>
              <Chip label="Cancel the rest" onPress={photos.cancel} />
            </View>
          ) : null}
          {!photos.running && photos.message ? <Text style={{ color: '#FFFFFF' }}>{photos.message}</Text> : null}
          {autoSnap.hint ? (
            <Chip
              label="Try the torch or move closer"
              onPress={() => {
                if (!torch) toggleTorch();
              }}
            />
          ) : null}
          {photos.enabled ? (
            <Button
              label="Photos"
              variant="secondary"
              accessibilityHint="Choose pictures from your library to read, with no camera needed"
              disabled={photos.running}
              onPress={photos.start}
              style={{ backgroundColor: 'rgba(0,0,0,0.5)' }}
            />
          ) : null}
          <Pressable
            accessibilityRole="button"
            accessibilityLabel="Take picture"
            onPress={autoSnap.takePicture}
            style={{ width: 72, height: 72, borderRadius: 36, borderWidth: 4, borderColor: '#FFFFFF', alignItems: 'center', justifyContent: 'center' }}
          >
            <View style={{ width: 56, height: 56, borderRadius: 28, backgroundColor: '#FFFFFF' }} />
          </Pressable>
        </View>

        {fixing ? (
          <View style={{ paddingHorizontal: spacing.lg, paddingBottom: spacing.sm }}>
            <FixItSheet candidate={fixing} sessionId={session.sessionId} onClose={() => setFixing(null)} />
          </View>
        ) : null}
        <ResultCards cards={session.cards} actions={actions} />
        <View style={{ paddingBottom: insets.bottom, backgroundColor: colors.surface }}>
          <ReceiptBar
            lines={session.lines}
            pictureLines={pictures.lines}
            onRemovePicture={pictures.removeLine}
            cards={session.cards}
            online={session.online}
            items={session.items + pictures.items}
            processing={session.processing + pictures.processing}
            problem={session.problem}
            notice={session.notice}
            onPlusOne={session.plusOne}
            onRemove={session.remove}
            onReview={() => router.push('/review')}
          />
        </View>
      </View>
    </View>
  );
}
