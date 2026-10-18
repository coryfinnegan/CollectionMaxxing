import { useLocalSearchParams } from 'expo-router';
import { LibraryView } from '@/features/library/LibraryView';

export default function Route() {
  const { add } = useLocalSearchParams<{ add?: string }>();
  return <LibraryView title="Library" openAdd={add === '1'} />;
}
