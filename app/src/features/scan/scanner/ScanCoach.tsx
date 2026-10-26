// The Scan tab's first-run coach: a one-time overlay over the viewfinder. One tap dismisses it for good; returning
// runs open the scanner with nothing in the way.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { Button, Card, Text } from '@/ui';

export function ScanCoach({ onDone }: { onDone: () => void }) {
  return (
    <View style={styles.overlay}>
      <Card>
        <View testID="scan-coach" style={styles.coach}>
          <Text variant="heading" accessibilityRole="header">
            Scan to add
          </Text>
          <Text>Point the camera at a barcode and it reads straight away. Point it at a shelf of spines and hold steady — check the matches before anything is added. Add anything else by hand from the bar at the top.</Text>
          <Button label="Start scanning" onPress={onDone} />
        </View>
      </Card>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  overlay: {
    position: 'absolute',
    left: 0,
    right: 0,
    top: 0,
    bottom: 0,
    alignItems: 'center',
    justifyContent: 'center',
    padding: theme.spacing.xl,
    backgroundColor: theme.fixed.scrim,
  },
  coach: { gap: theme.spacing.md },
}));
