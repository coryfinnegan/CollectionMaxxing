import { useLocalSearchParams } from 'expo-router';
import { JoinScreen } from '@/features/auth/WebAuthScreens';

export default function Join() {
  const { token } = useLocalSearchParams<{ token: string }>();
  return <JoinScreen token={token} />;
}
