// The sidebar's library list: name with your role under it, the active one marked. Hidden with one library; New library and
// Join a library are always there (docs/15 "Library switcher").
import { Link } from 'expo-router';
import { Pressable, View } from 'react-native';
import { radius, spacing, useColors } from '@/theme/tokens';
import { Text } from '@/ui';
import { roleLabel } from './JoinLibraryScreen';
import { useActiveLibraryId, useLibraries } from './useLibraries';
import { useLibraryStore } from '@/state/library';

export function LibrarySwitcher() {
  const colors = useColors();
  const { libraries, loaded } = useLibraries();
  const { libraryId } = useActiveLibraryId();
  const setActive = useLibraryStore((s) => s.setActiveLibraryId);
  if (!loaded || libraries.length === 0) return null;
  return (
    <View role="group" aria-label="Libraries" style={{ gap: spacing.xs, marginBottom: spacing.lg }}>
      {libraries.length > 1
        ? libraries.map((library) => {
            const active = library.id === libraryId;
            return (
              <Pressable
                key={library.id}
                accessibilityRole="button"
                accessibilityLabel={`${library.name}, ${roleLabel[library.role]}`}
                {...(active ? { 'aria-current': 'true' as const } : {})}
                onPress={() => setActive(library.id)}
                style={{
                  paddingVertical: spacing.sm,
                  paddingHorizontal: spacing.md,
                  borderRadius: radius.md,
                  borderWidth: 1,
                  borderColor: active ? colors.scan : colors.line,
                  backgroundColor: active ? colors.ground : 'transparent',
                }}
              >
                <Text variant="label" tone={active ? 'scan' : 'ink'}>
                  {library.name}
                </Text>
                <Text variant="caption" tone="muted">
                  {roleLabel[library.role]}
                </Text>
              </Pressable>
            );
          })
        : null}
      <Link href="/welcome/create" asChild>
        <Pressable accessibilityRole="link" style={{ paddingVertical: spacing.xs, paddingHorizontal: spacing.md }}>
          <Text variant="caption" tone="muted">
            New library
          </Text>
        </Pressable>
      </Link>
      <Link href="/welcome/join" asChild>
        <Pressable accessibilityRole="link" style={{ paddingVertical: spacing.xs, paddingHorizontal: spacing.md }}>
          <Text variant="caption" tone="muted">
            Join a library
          </Text>
        </Pressable>
      </Link>
    </View>
  );
}
