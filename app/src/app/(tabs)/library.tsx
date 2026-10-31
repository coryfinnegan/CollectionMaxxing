import { TabBottomPad } from '@/components/TabBottomPad';
import { LibraryView } from '@/features/library/LibraryView';

export default function Route() {
  return (
    <TabBottomPad>
      <LibraryView title="Library" />
    </TabBottomPad>
  );
}
