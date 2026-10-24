import { useLocalSearchParams } from 'expo-router';
import { SafeAreaView } from 'react-native-safe-area-context';
import { ItemDetailScreen } from '@/features/item/ItemDetailScreen';

export default function Route() {
  const { id } = useLocalSearchParams<{ id: string }>();
  // Pushed above the tabs, so there is no tab bar below: keep the content clear of the home indicator.
  // The stack header already accounts for the top inset.
  return (
    <SafeAreaView testID="item-detail-safe-area" edges={['bottom', 'left', 'right']} style={{ flex: 1 }}>
      <ItemDetailScreen id={id} />
    </SafeAreaView>
  );
}
