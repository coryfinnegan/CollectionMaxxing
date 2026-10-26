// A parchment notice card: framed texture in Full, the same paper solid in Flat,
// a heavier frame in Accessible. Body text stays in the sans; only display
// titles use Cinzel.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { StyleSheet } from 'react-native-unistyles';
import { Text } from '../Text';
import { type CastleMode, useCastleMode } from './mode';
import { NineSlice } from './NineSlice';

type ParchmentCardProps = {
  title: string;
  body: string;
  /** Defaults to the resolved castle mode. */
  mode?: CastleMode;
  testID?: string;
};

export function ParchmentCard({ title, body, mode, testID }: ParchmentCardProps) {
  const hooked = useCastleMode();
  return (
    <NineSlice surface="parchment" mode={mode ?? hooked} testID={testID}>
      <Text variant="heading" style={styles.title}>
        {title}
      </Text>
      <Text style={styles.body}>{body}</Text>
    </NineSlice>
  );
}

const styles = StyleSheet.create((theme) => ({
  title: { color: theme.castle.parchmentInk },
  body: { color: theme.castle.parchmentInk },
}));
