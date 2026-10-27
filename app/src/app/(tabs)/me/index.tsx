import { TabBottomPad } from '@/components/TabBottomPad';
import { MeScreen } from '@/features/me/MeScreen';

export default function Route() {
  return (
    <TabBottomPad withPill>
      <MeScreen />
    </TabBottomPad>
  );
}
