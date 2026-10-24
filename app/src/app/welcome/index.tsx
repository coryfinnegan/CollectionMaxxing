import { Redirect } from 'expo-router';
import { useLibraries } from '@/features/onboarding/useLibraries';
import { WelcomeScreen } from '@/features/onboarding/WelcomeScreen';
import { isWeb } from '@/lib/auth/session';

// Only an account with no libraries stays here; the phone's own Welcome is ONB-3.
export default function Welcome() {
  const { libraries, loaded } = useLibraries();
  if (!isWeb() || (loaded && libraries.length > 0)) return <Redirect href="/library" />;
  return <WelcomeScreen />;
}
