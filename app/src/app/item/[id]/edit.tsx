import { useLocalSearchParams } from 'expo-router';
import { ItemEditScreen } from '@/features/item/ItemEditScreen';

export default function Route() {
  const { id } = useLocalSearchParams<{ id: string }>();
  return <ItemEditScreen id={id} />;
}
