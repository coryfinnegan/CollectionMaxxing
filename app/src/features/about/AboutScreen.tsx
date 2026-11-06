// About: app version, the privacy note and the provider credits. Read-only; no network needed.
import Constants from 'expo-constants';
import { useEffect, useState } from 'react';
import { Linking, Pressable } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { useColors } from '@/theme/tokens';
import { Card, Screen, Text } from '@/ui';
import { ATTRIBUTIONS } from './attributions';
import { buildLine } from './buildInfo';
import { PRIVACY_NOTE } from './privacyNote';

/** The real CFBundleVersion; the Expo config's build number is "1" on TestFlight. Absent on web or when the read fails. */
function useBuildNumber(): string | undefined {
  const [build, setBuild] = useState<string>();
  useEffect(() => {
    let live = true;
    // Loaded on use, as the eval runner does: the native module is not there on web or under test.
    void (async () => {
      try {
        // eslint-disable-next-line @typescript-eslint/no-require-imports
        const { visionEngine } = require('@/native/cm-vision') as typeof import('@/native/cm-vision');
        const status = await visionEngine.deviceStatus();
        if (live) setBuild(status.buildNumber || undefined);
      } catch {
        // no build part: the version alone is still true
      }
    })();
    return () => {
      live = false;
    };
  }, []);
  return build;
}

function versionLine(build: string | undefined): string {
  const version = Constants.expoConfig?.version ?? 'unknown';
  return build ? `Version ${version} (build ${build})` : `Version ${version}`;
}

export function AboutScreen() {
  const colors = useColors();
  const build = buildLine();
  const buildNumber = useBuildNumber();
  return (
    <SafeAreaView edges={['left', 'right']} style={{ flex: 1, backgroundColor: colors.ground }}>
    <Screen title="About" subtitle="Version, privacy and credits.">
      <Card>
        <Text variant="heading">CollectionMaxxing</Text>
        <Text>{versionLine(buildNumber)}</Text>
        {build ? <Text tone="muted">{build}</Text> : null}
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
            accessibilityLabel={`${a.notice} ${a.url}`}
            onPress={() => void Linking.openURL(a.url)}
            style={{ minHeight: 44, justifyContent: 'center' }}
          >
            <Text>{a.notice}</Text>
            <Text variant="caption" tone="muted">{a.url}</Text>
          </Pressable>
        ))}
      </Card>
    </Screen>
    </SafeAreaView>
  );
}
