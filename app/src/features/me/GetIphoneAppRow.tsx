// Settings > Get the iPhone app (web only). The app comes through TestFlight (ADR-024): the owner adds each person as a
// tester in App Store Connect, the invite arrives by email, and TestFlight installs and updates the app.
import { Linking } from 'react-native';
import { Button, Text } from '@/ui';

const TESTFLIGHT_URL = 'https://apps.apple.com/app/testflight/id899247664';

export function GetIphoneAppRow() {
  return (
    <>
      <Text variant="heading">Get the iPhone app</Text>
      <Text>The iPhone app comes through TestFlight, Apple&apos;s app for private apps. Once the owner adds you as a tester, an invite arrives by email.</Text>
      <Text tone="muted">On your iPhone, install TestFlight, open the invite and tap Install. Turn on automatic updates in TestFlight so each new version arrives on its own.</Text>
      <Button label="Get TestFlight" variant="secondary" onPress={() => void Linking.openURL(TESTFLIGHT_URL).catch(() => {})} />
    </>
  );
}
