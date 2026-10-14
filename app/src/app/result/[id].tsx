import { useLocalSearchParams } from 'expo-router';
import { ResultDetail } from '@/features/scan/cards/ResultDetail';

export default function Route() {
  const { id } = useLocalSearchParams<{ id: string }>();
  return <ResultDetail id={id} />;
}
