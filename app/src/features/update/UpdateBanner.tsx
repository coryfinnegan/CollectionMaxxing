// One calm notice at the top of the Library on the iPhone when the server has a newer app version. The web never shows it.
import { View } from 'react-native';
import { spacing } from '@/theme/tokens';
import { Button, Card, Text } from '@/ui';
import { useUpdateNotice } from './useUpdateNotice';

export function UpdateBanner() {
  const notice = useUpdateNotice();
  if (!notice) return null;
  return (
    <Card>
      <Text>{`Version ${notice.version} is ready. Install it at home on your Wi-Fi.`}</Text>
      <View style={{ flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm }}>
        <Button label={`Download version ${notice.version}`} onPress={notice.download} />
        <Button label="Dismiss" variant="secondary" onPress={notice.dismiss} />
      </View>
    </Card>
  );
}
