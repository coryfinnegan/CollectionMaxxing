// About: app version, the privacy note and the provider credits. Read-only; no network needed.
import Constants from 'expo-constants';
import { Linking, Pressable } from 'react-native';
import { Card, Screen, Text } from '@/ui';
import { ATTRIBUTIONS } from './attributions';
import { PRIVACY_NOTE } from './privacyNote';

function versionLine(): string {
  const version = Constants.expoConfig?.version ?? 'unknown';
  const build = Constants.expoConfig?.ios?.buildNumber ?? Constants.nativeBuildVersion;
  return build ? `Version ${version} (build ${build})` : `Version ${version}`;
}

export function AboutScreen() {
  return (
    <Screen title="About" subtitle="Version, privacy and credits.">
      <Card>
        <Text variant="heading">CollectionMaxxing</Text>
        <Text>{versionLine()}</Text>
      </Card>
      <Card>
        <Text variant="heading">Privacy</Text>
        {PRIVACY_NOTE.map((paragraph) => (
          <Text key={paragraph}>{paragraph}</Text>
        ))}
      </Card>
      <Card>
        <Text variant="heading">Provider data</Text>
        {ATTRIBUTIONS.map((a) => (
          <Pressable
            key={a.name}
            accessibilityRole="link"
            accessibilityLabel={`${a.name}: ${a.url}`}
            onPress={() => void Linking.openURL(a.url)}
            style={{ minHeight: 44, justifyContent: 'center' }}
          >
            <Text>{a.notice}</Text>
            <Text variant="caption" tone="muted">{a.url}</Text>
          </Pressable>
        ))}
      </Card>
    </Screen>
  );
}
