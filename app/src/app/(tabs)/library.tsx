import { useLocalSearchParams } from 'expo-router';
import { TabBottomPad } from '@/components/TabBottomPad';
import { LibraryView } from '@/features/library/LibraryView';

export default function Route() {
  const { add } = useLocalSearchParams<{ add?: string }>();
  return (
    <TabBottomPad>
      <LibraryView title="Library" openAdd={add === '1'} />
    </TabBottomPad>
  );
}
