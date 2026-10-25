// Web-only sign-in screen. Signing in is a navigation to the server (which hands off to Auth0); nothing here handles a password or a token.
// Every first sign-in creates an account (docs/15), so there is one Sign in button and no setup page. Joining by link lives in
// features/onboarding.
import { useLocalSearchParams } from 'expo-router';
import { SafeAreaView } from 'react-native-safe-area-context';
import { useColors } from '@/theme/tokens';
import { Button, Card, Screen, Text } from '@/ui';
import { loginUrl, navigateTo, safeReturnUrl, signInErrorMessage } from './navigation';

function ErrorText({ children }: { children: string | null }) {
  return children ? (
    <Text tone="danger" accessibilityRole="alert">
      {children}
    </Text>
  ) : null;
}

export function LoginScreen() {
  const params = useLocalSearchParams<{ error?: string; returnUrl?: string }>();
  const colors = useColors();
  const returnUrl = safeReturnUrl(params.returnUrl);
  return (
    <SafeAreaView testID="safe-area" style={{ flex: 1, backgroundColor: colors.ground }}>
      <Screen title="CollectionMaxxing" titleVariant="display" subtitle="Sign in to your library.">
        <Card>
          <ErrorText>{signInErrorMessage(params.error)}</ErrorText>
          <Button label="Sign in" onPress={() => navigateTo(loginUrl({ returnUrl }))} />
        </Card>
      </Screen>
    </SafeAreaView>
  );
}
