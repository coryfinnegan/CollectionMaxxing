// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
// "Add test photos" on the Evals screen: pick photos in the library, choose Cover or Shelf for each, and send the originals to the PC's eval folder.
import { useSyncExternalStore } from 'react';
import { Image, View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { Button, Card, Chip, Text, TextField } from '@/ui';
import { goldenPhotoApi, type GoldenSet } from './api';
import { AddPhotosController, SET_LABEL, isRetryable, photos, type DraftPhoto } from './addPhotos';
import { deviceOriginalPicker, readOriginal } from './originalPicker';

const SETS: GoldenSet[] = ['single', 'shelf'];

/** The real picker, reader and API. The picker itself loads only when "Add photos" is tapped, so the Evals screen opens without the photo library. */
export const deviceAddPhotos = () => new AddPhotosController({ picker: deviceOriginalPicker, read: readOriginal, api: goldenPhotoApi });

function PhotoRow({
  row,
  index,
  problem,
  locked,
  controller,
}: {
  row: DraftPhoto;
  index: number;
  problem: string | null;
  locked: boolean;
  controller: AddPhotosController;
}) {
  const n = index + 1;
  const editable = isRetryable(row) && !locked;
  const calm = row.status === 'sending' || row.status === 'added' || row.status === 'ready';
  const status = row.status === 'sending' ? 'Sending…' : row.status === 'added' ? 'Added' : row.message;
  return (
    <View style={styles.row}>
      <Image source={{ uri: row.uri }} style={styles.thumb} accessible={false} accessibilityIgnoresInvertColors />
      <View style={styles.body}>
        {editable ? (
          <>
            <View style={styles.chips}>
              {SETS.map((set) => (
                <Chip
                  key={set}
                  label={SET_LABEL[set]}
                  selected={row.set === set}
                  accessibilityLabel={`${SET_LABEL[set]} for photo ${n}`}
                  onPress={() => controller.setSet(row.key, set)}
                />
              ))}
            </View>
            <TextField
              label={`Name for photo ${n}`}
              value={row.name}
              onChangeText={(name) => controller.rename(row.key, name)}
              autoCapitalize="none"
              autoCorrect={false}
              maxLength={64}
              error={problem}
            />
          </>
        ) : (
          <Text>{`${SET_LABEL[row.set]}: ${row.name}`}</Text>
        )}
        {status ? (
          <Text variant="caption" tone={calm ? 'muted' : 'danger'} accessibilityRole={calm ? undefined : 'alert'}>
            {status}
          </Text>
        ) : null}
        {row.status !== 'sending' && row.status !== 'added' && !locked ? (
          <Button label="Remove" variant="secondary" accessibilityLabel={`Remove photo ${n}`} onPress={() => controller.remove(row.key)} />
        ) : null}
      </View>
    </View>
  );
}

export function AddEvalPhotos({ controller }: { controller: AddPhotosController }) {
  const state = useSyncExternalStore(controller.subscribe, controller.getState);
  const sending = state.phase === 'sending';
  const problems = controller.nameProblems();
  const waiting = state.photos.filter(isRetryable).length;
  const added = state.photos.filter((row) => row.status === 'added').length;
  const stuck = state.photos.filter((row) => row.status === 'too_large' || row.status === 'unsupported').length;

  return (
    <Card>
      <Text variant="heading">Add test photos</Text>
      <Text tone="muted">
        Pick photos from your library and choose Cover for a photo of one item or Shelf for a whole shelf. They go to your PC&apos;s eval folder, at full size, and
        nowhere else.
      </Text>
      {state.notice ? (
        <Text tone="danger" accessibilityRole="alert">
          {state.notice}
        </Text>
      ) : null}
      <Button label="Add photos" variant="secondary" disabled={sending || state.phase === 'picking'} onPress={() => void controller.pick()} />

      {state.photos.map((row, index) => (
        <PhotoRow key={row.key} row={row} index={index} problem={problems[row.key] ?? null} locked={sending} controller={controller} />
      ))}

      {sending ? (
        <Button label={`Sending photo ${state.position} of ${state.batch}`} busy onPress={() => {}} />
      ) : waiting > 0 ? (
        <Button
          label={`Send ${photos(waiting)} to your PC`}
          disabled={!controller.canSend()}
          haptic="commit"
          onPress={() => void controller.send()}
        />
      ) : null}

      {!sending && added > 0 ? <Text accessibilityRole="alert">{`Added ${photos(added)} to your PC's eval folder.`}</Text> : null}
      {!sending && stuck > 0 ? <Text tone="muted">{`${photos(stuck)} can't be sent. Remove ${stuck === 1 ? 'it' : 'them'} to clear the list.`}</Text> : null}
    </Card>
  );
}

const styles = StyleSheet.create((theme) => ({
  row: { flexDirection: 'row', alignItems: 'flex-start', gap: theme.spacing.md },
  thumb: { width: 56, height: 56, borderRadius: theme.radius.sm, backgroundColor: theme.colors.line },
  body: { flex: 1, gap: theme.spacing.sm },
  chips: { flexDirection: 'row', gap: theme.spacing.sm },
}));
