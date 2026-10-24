import { Redirect } from 'expo-router';
import { useLibraries } from '@/features/onboarding/useLibraries';
import { WelcomeScreen } from '@/features/onboarding/WelcomeScreen';

// Only an account with no libraries stays here (web and iPhone).
export default function Welcome() {
  const { libraries, loaded } = useLibraries();
  if (loaded && libraries.length > 0) return <Redirect href="/library" />;
  return <WelcomeScreen />;
}
