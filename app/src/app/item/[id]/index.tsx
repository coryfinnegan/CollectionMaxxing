import { useLocalSearchParams } from 'expo-router';
import { ItemDetailScreen } from '@/features/item/ItemDetailScreen';

export default function Route() {
  const { id } = useLocalSearchParams<{ id: string }>();
  return <ItemDetailScreen id={id} />;
}
