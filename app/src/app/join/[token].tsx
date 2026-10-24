import { useLocalSearchParams } from 'expo-router';
import { JoinInviteScreen } from '@/features/onboarding/JoinInviteScreen';

export default function Join() {
  const { token } = useLocalSearchParams<{ token: string }>();
  return <JoinInviteScreen token={token} />;
}
