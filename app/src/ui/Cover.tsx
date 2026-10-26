// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useEffect, useState } from 'react';
import Animated from 'react-native-reanimated';
import { StyleSheet } from 'react-native-unistyles';
import { authHeaders, isWeb } from '@/lib/auth/session';
import { ownMediaUrl } from '@/lib/media';
import { Text } from './Text';

type CoverProps = {
  title: string;
  uri?: string | null;
  /** Width in points; the height follows a 2:3 cover ratio. */
  width?: number;
  /** Set on both ends of the library-to-detail flight for the shared transition; unset where it is unsupported. */
  sharedTag?: string;
};

type ImageSource = { uri: string; headers?: Record<string, string> };

/**
 * What the image loads. The web keeps the address as it is (its cookie flows with the request). The phone asks this
 * server's own artwork route at its absolute address with the sign-in, which arrives a beat later; a local photo or a
 * provider address loads as it is, and never carries the sign-in.
 */
function useCoverSource(uri: string | null | undefined): ImageSource | null {
  const absolute = !uri || isWeb() ? undefined : ownMediaUrl(uri);
  // The signed source, keyed by its address so a superseded answer never shows for a new cover.
  const [signed, setSigned] = useState<{ absolute: string; source: ImageSource } | null>(null);
  useEffect(() => {
    if (!absolute) return;
    let live = true;
    authHeaders('GET').then(
      (headers) => {
        if (live) setSigned({ absolute, source: { uri: absolute, headers } });
      },
      () => {
        if (live) setSigned({ absolute, source: { uri: absolute } });
      },
    );
    return () => {
      live = false;
    };
  }, [absolute]);
  if (!uri) return null;
  if (isWeb() || !absolute) return { uri };
  return signed?.absolute === absolute ? signed.source : null;
}

/** A cover image, or a plain tile with the title when there is no art or it fails to load. */
export function Cover({ title, uri, width = 96, sharedTag }: CoverProps) {
  const height = Math.round(width * 1.5);
  const source = useCoverSource(uri);
  // Which address failed, so a new address gets a fresh attempt even after a failure.
  const [failedUri, setFailedUri] = useState<string | null>(null);
  if (source && failedUri !== source.uri) {
    return (
      <Animated.Image
        source={source}
        accessibilityIgnoresInvertColors
        accessibilityLabel={`Cover of ${title}`}
        onError={() => setFailedUri(source.uri)}
        sharedTransitionTag={sharedTag}
        style={styles.image({ width, height })}
      />
    );
  }
  return (
    <Animated.View
      // The title is shown next to the cover, so the stand-in tile stays out of the accessibility tree.
      accessibilityElementsHidden
      importantForAccessibility="no-hide-descendants"
      sharedTransitionTag={sharedTag}
      style={styles.tile({ width, height })}
    >
      {/* A narrow tile cannot fit a word per line, so wrapped titles break
          mid-word ("Cod e 9…"); one truncated line reads as a tile should (#373). */}
      <Text variant="caption" numberOfLines={width < 64 ? 1 : 3}>
        {title}
      </Text>
    </Animated.View>
  );
}

const styles = StyleSheet.create((theme) => ({
  image: ({ width, height }: { width: number; height: number }) => ({
    width,
    height,
    borderRadius: theme.radius.sm,
    backgroundColor: theme.colors.line,
  }),
  tile: ({ width, height }: { width: number; height: number }) => ({
    width,
    height,
    borderRadius: theme.radius.sm,
    backgroundColor: theme.colors.line,
    padding: theme.spacing.sm,
    justifyContent: 'flex-end',
  }),
}));
