import { useLocalSearchParams } from 'expo-router';
import { SafeAreaView } from 'react-native-safe-area-context';
import { ItemEditScreen } from '@/features/item/ItemEditScreen';

export default function Route() {
  const { id, status } = useLocalSearchParams<{ id: string; status?: string }>();
  // Pushed above the tabs, so there is no tab bar below: keep the form clear of the home indicator.
  // The stack header already accounts for the top inset.
  return (
    <SafeAreaView testID="item-edit-safe-area" edges={['bottom', 'left', 'right']} style={{ flex: 1 }}>
      <ItemEditScreen id={id} startAs={status === 'lent' ? 'lent' : undefined} />
    </SafeAreaView>
  );
}
