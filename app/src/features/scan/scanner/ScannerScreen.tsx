// The scanner (docs/06 M3): camera behind everything, "Nice scan!" flash, result cards, the receipt bar.
// It is given its sources, so tests and the dev menu drive it with a FakeScannerSource and no camera.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useRouter } from 'expo-router';
import { useContext, useEffect, useMemo, useRef, useState, type MutableRefObject, type ReactNode } from 'react';
import { Platform, Pressable, StyleSheet, View } from 'react-native';
import { SafeAreaInsetsContext } from 'react-native-safe-area-context';
import { StyleSheet as Unistyles } from 'react-native-unistyles';
import { NiceScanFlash } from '../autosnap/NiceScanFlash';
import { DEFAULT_AUTO_SNAP_CONFIG } from '../autosnap/defaults';
import { createCountingSnapHandler, useAutoSnap, type AutoSnapTuning, type SnapEvent } from '../autosnap/useAutoSnap';
import type { AutoSnapConfig } from '../autosnap/types';
import type { CandidateDto } from '@/api/generated/models';
import { submitBoundBarcode } from '@/features/review/api';
import type { BoundScan } from '@/features/review/boundScan';
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
import { SessionSheet } from '../session/SessionSheet';
import { defaultSessionContext, sessionCreateBody, type SessionContext } from '../session/sessionContext';
import { ReceiptBar } from '../tray/ReceiptBar';
import { fixed, spacing, useColors } from '@/theme/tokens';
import { Button, Chip, Snackbar, Text } from '@/ui';
import { DEV_SIX_CODES } from './devCodes';
import { FakeScannerSource, type ScannerSource } from './ScannerSource';
import { ScannerSearch } from './ScannerSearch';
import { SessionBar } from './SessionBar';

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
  /** False when embedded in the Scan tab: the tab bar is the way out, so there is no Close. */
  showClose?: boolean;
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
  /** Review's "Scan barcode": the camera reads for this row, and leaves once the read is sent. */
  boundScan?: BoundScan & { onDone(): void; onCancel(): void };
};

export function ScannerScreen({
  libraryId,
  source,
  preview,
  devSource,
  tuning,
  onClose,
  showClose = true,
  onSnap,
  pipeline,
  autoSnapConfig = DEFAULT_AUTO_SNAP_CONFIG,
  autoSnapTuning,
  outcomeRef,
  photoImport,
  boundScan,
}: Props) {
  const router = useRouter();
  const colors = useColors();
  const insets = useContext(SafeAreaInsetsContext) ?? { top: 0, bottom: 0, left: 0, right: 0 };
  // Barcodes found in imported pictures arrive here and take the same path as the camera's.
  const importCodes = useMemo(() => new FakeScannerSource(), []);
  const sources = useMemo(() => (devSource ? [source, devSource, importCodes] : [source, importCodes]), [source, devSource, importCodes]);
  // What this scan inherits: the library's remembered spot, changed in the sheet, sent when the session is created.
  const [context, setContext] = useState<SessionContext>(() => defaultSessionContext(libraryId));
  const [contextLibraryId, setContextLibraryId] = useState(libraryId);
  if (contextLibraryId !== libraryId) {
    // A switched-to library starts from its own remembered spot. Adjusted during render (not in an effect) so the
    // bar never shows one library's spot while scanning another.
    setContextLibraryId(libraryId);
    setContext(defaultSessionContext(libraryId));
  }
  const [sheet, setSheet] = useState<'session' | 'search' | null>(null);
  const body = useMemo(() => sessionCreateBody(context), [context]);
  const boundCapture = useMemo(
    () =>
      boundScan
        ? {
            submit: async (code: { value: string; symbology: string }) => {
              await submitBoundBarcode(boundScan.sessionId, boundScan.candidateId, code);
              boundScan.onDone();
            },
          }
        : undefined,
    [boundScan],
  );
  const session = useScanSession({ libraryId, sources, tuning, sessionBody: body, boundCapture });
  const { canAdd } = useLibraryRole(libraryId);
  // "Move it here" moves to the session spot; in-store defers the location, so the card hides Move there.
  const cardActions = useCardActions({
    libraryId,
    sessionId: session.sessionId,
    canAdd,
    moveToLocationId: context.channel === 'home' ? context.locationId : null,
  });
  const [fixing, setFixing] = useState<CandidateDto | null>(null);
  const actions: CardActions = useMemo(() => ({ ...cardActions, onFixIt: setFixing }), [cardActions]);
  const [flash, setFlash] = useState(false);
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

  const toggleFlash = () => {
    const next = !flash;
    setFlash(next);
    source.setFlash(next);
  };

  return (
    <View style={{ flex: 1, backgroundColor: fixed.black }}>
      {preview}
      <View pointerEvents="box-none" style={[StyleSheet.absoluteFill, { paddingTop: insets.top + spacing.sm }]}>
        <View style={{ flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', paddingHorizontal: spacing.lg }}>
          {showClose ? (
            <Button label="Close" variant="secondary" onPress={onClose ?? (() => router.back())} style={{ backgroundColor: fixed.scrim }} />
          ) : null}
          {Platform.OS === 'web' ? (
            <Text variant="heading" accessibilityRole="header" style={{ color: fixed.white }}>
              Scanner
            </Text>
          ) : (
            <View style={{ flex: 1 }} />
          )}
          <Button
            label={flash ? 'Flash on' : 'Flash'}
            variant="secondary"
            accessibilityState={{ selected: flash }}
            onPress={toggleFlash}
            style={{ backgroundColor: flash ? colors.accent : fixed.scrim }}
          />
        </View>
        <SessionBar
          libraryId={libraryId}
          context={context}
          onOpenSession={() => setSheet('session')}
          onOpenSearch={() => setSheet('search')}
          onAddManually={() => router.push({ pathname: '/library', params: { add: '1' } })}
          onOpenReview={() => router.push('/review')}
        />
        {boundScan ? (
          <View style={boundStyles.wrap}>
            <View style={boundStyles.banner}>
              <Text style={boundStyles.title}>{`Scan the barcode for ${boundScan.title}`}</Text>
              <Button label="Cancel" variant="secondary" onPress={boundScan.onCancel} />
            </View>
          </View>
        ) : null}

        <View pointerEvents="none" style={{ flex: 1, alignItems: 'center', justifyContent: 'center' }}>
          <NiceScanFlash visible={session.showNiceScan || autoSnap.flash} reduceMotion={reduceMotion} />
        </View>

        {devSource ? (
          <View style={{ alignItems: 'center', gap: spacing.sm, paddingBottom: spacing.sm }}>
            <Button
              label="Scan these 6 codes"
              variant="secondary"
              onPress={() => devSource.inject(DEV_SIX_CODES)}
              style={{ backgroundColor: fixed.scrim }}
            />
            {pictures.enabled ? (
              <Button label="Snap a fixture shelf" variant="secondary" onPress={pictures.replayFixture} style={{ backgroundColor: fixed.scrim }} />
            ) : null}
          </View>
        ) : null}

        <View pointerEvents="box-none" style={{ alignItems: 'center', gap: spacing.md, paddingBottom: spacing.md }}>
          {pictures.enabled && pictures.unreadableCount > 0 ? (
            <Chip label={`Get closer for these ${pictures.unreadableCount}`} onPress={pictures.dismissUnreadable} />
          ) : null}
          {photos.running && photos.progress ? (
            <View style={{ flexDirection: 'row', alignItems: 'center', gap: spacing.md }}>
              <Text style={{ color: fixed.white }}>{photos.progress}</Text>
              <Chip label="Cancel the rest" onPress={photos.cancel} />
            </View>
          ) : null}
          {!photos.running && photos.message ? <Text style={{ color: fixed.white }}>{photos.message}</Text> : null}
          {autoSnap.hint ? (
            <Chip
              label="Try the flash or move closer"
              onPress={() => {
                if (!flash) toggleFlash();
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
              style={{ backgroundColor: fixed.scrim }}
            />
          ) : null}
          <Pressable
            accessibilityRole="button"
            accessibilityLabel="Take picture"
            onPress={autoSnap.takePicture}
            style={{ width: 72, height: 72, borderRadius: 36, borderWidth: 4, borderColor: fixed.white, alignItems: 'center', justifyContent: 'center' }}
          >
            <View style={{ width: 56, height: 56, borderRadius: 28, backgroundColor: fixed.white }} />
          </Pressable>
        </View>

        {fixing ? (
          <View style={{ paddingHorizontal: spacing.lg, paddingBottom: spacing.sm }}>
            <FixItSheet candidate={fixing} sessionId={session.sessionId} onClose={() => setFixing(null)} />
          </View>
        ) : null}
        {sheet === 'session' ? (
          <View style={sheetStyles.wrap}>
            <SessionSheet
              libraryId={libraryId}
              context={context}
              onSave={(_, next) => {
                setContext(next);
                setSheet(null);
              }}
              onClose={() => setSheet(null)}
            />
          </View>
        ) : null}
        {sheet === 'search' ? (
          <View style={sheetStyles.wrap}>
            <ScannerSearch libraryId={libraryId} onClose={() => setSheet(null)} />
          </View>
        ) : null}
        {actions.moveToast ? (
          <View style={sheetStyles.snack}>
            <Snackbar
              message={actions.moveToast.message}
              actionLabel={actions.moveToast.canUndo ? 'Undo' : undefined}
              onAction={actions.moveToast.canUndo ? actions.onUndoMove : undefined}
            />
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

const sheetStyles = Unistyles.create((theme) => ({
  wrap: { paddingHorizontal: theme.spacing.lg, paddingBottom: theme.spacing.sm },
  snack: { paddingHorizontal: theme.spacing.lg, paddingBottom: theme.spacing.sm },
}));

const boundStyles = Unistyles.create((theme) => ({
  wrap: { paddingHorizontal: theme.spacing.lg, paddingTop: theme.spacing.sm },
  banner: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: theme.spacing.md,
    backgroundColor: fixed.scrim,
    borderRadius: theme.radius.md,
    padding: theme.spacing.md,
  },
  title: { flex: 1, color: fixed.white },
}));
