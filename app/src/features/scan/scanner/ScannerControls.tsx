// The scanner's one control row (#408): the session chip plus Flash, Search, Photos and manual add as compact
// icon controls in a single fixed row. Nothing else floats over the viewfinder. The glyphs are hand-drawn SVG,
// the repo's icon idiom; every control keeps its spoken label.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { Platform, Pressable, View } from 'react-native';
import type { ReactNode } from 'react';
import Animated from 'react-native-reanimated';
import Svg, { Circle, Line, Path, Rect } from 'react-native-svg';
import { StyleSheet } from 'react-native-unistyles';
import { breadcrumb } from '@/features/item/breadcrumb';
import { useLocationsList } from '@/features/locations/useLocations';
import { fixed, useColors } from '@/theme/tokens';
import { Text, usePressFeedback } from '@/ui';
import { SESSION_CHANNEL_LABEL, SESSION_MEDIA_LABEL, SESSION_STATUS_LABEL, type SessionContext } from '../session/sessionContext';

function FlashGlyph({ color }: { color: string }) {
  return (
    <Svg width={22} height={22} viewBox="0 0 24 24">
      <Path d="M13 2 3 14h9l-1 8 10-12h-9l1-8z" fill={color} />
    </Svg>
  );
}

function SearchGlyph({ color }: { color: string }) {
  return (
    <Svg width={22} height={22} viewBox="0 0 24 24">
      <Circle cx={11} cy={11} r={6.5} fill="none" stroke={color} strokeWidth={2} />
      <Line x1={15.8} y1={15.8} x2={20} y2={20} stroke={color} strokeWidth={2} strokeLinecap="round" />
    </Svg>
  );
}

function PhotosGlyph({ color }: { color: string }) {
  return (
    <Svg width={22} height={22} viewBox="0 0 24 24">
      <Rect x={3} y={5} width={18} height={14} rx={2} fill="none" stroke={color} strokeWidth={2} />
      <Circle cx={9} cy={10} r={1.5} fill={color} />
      <Path d="M4.5 17.5 10 12l3.5 3.5L16 13l3.5 3.5" fill="none" stroke={color} strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" />
    </Svg>
  );
}

function PlusGlyph({ color }: { color: string }) {
  return (
    <Svg width={22} height={22} viewBox="0 0 24 24">
      <Path d="M12 5v14M5 12h14" stroke={color} strokeWidth={2} strokeLinecap="round" />
    </Svg>
  );
}

function CloseGlyph({ color }: { color: string }) {
  return (
    <Svg width={22} height={22} viewBox="0 0 24 24">
      <Path d="M6 6l12 12M18 6 6 18" stroke={color} strokeWidth={2} strokeLinecap="round" />
    </Svg>
  );
}

function ControlButton({
  label,
  hint,
  selected = false,
  disabled = false,
  onPress,
  children,
}: {
  label: string;
  hint?: string;
  selected?: boolean;
  disabled?: boolean;
  onPress: () => void;
  children: ReactNode;
}) {
  const press = usePressFeedback('tap');
  return (
    <Animated.View style={press.style}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={label}
        accessibilityHint={hint}
        accessibilityState={{ selected, disabled }}
        disabled={disabled}
        onPress={() => {
          press.buzz();
          onPress();
        }}
        onPressIn={press.onPressIn}
        onPressOut={press.onPressOut}
        style={styles.icon({ selected })}
      >
        {children}
      </Pressable>
    </Animated.View>
  );
}

export function ScannerControls({
  libraryId,
  context,
  showClose,
  onClose,
  onOpenSession,
  flash,
  onToggleFlash,
  onOpenSearch,
  photosEnabled,
  photosRunning,
  onPhotos,
  onAddManually,
}: {
  libraryId: string;
  context: SessionContext;
  showClose: boolean;
  onClose: () => void;
  onOpenSession: () => void;
  flash: boolean;
  onToggleFlash: () => void;
  onOpenSearch: () => void;
  photosEnabled: boolean;
  photosRunning: boolean;
  onPhotos: () => void;
  onAddManually: () => void;
}) {
  const colors = useColors();
  const chipPress = usePressFeedback('select');
  const { locations } = useLocationsList(libraryId);
  const place = context.channel === 'in_store' ? 'Decide later' : breadcrumb(locations, context.locationId);
  const summary = `${place} · ${SESSION_STATUS_LABEL[context.status]} · ${SESSION_MEDIA_LABEL[context.mediaHint]} · ${SESSION_CHANNEL_LABEL[context.channel]}`;
  const glyph = fixed.white;
  return (
    <View testID="scanner-controls" style={styles.row}>
      {Platform.OS === 'web' ? (
        <Text variant="heading" accessibilityRole="header" style={styles.title}>
          Scanner
        </Text>
      ) : null}
      {showClose ? (
        <ControlButton label="Close" onPress={onClose}>
          <CloseGlyph color={glyph} />
        </ControlButton>
      ) : null}
      <Animated.View style={[chipPress.style, styles.chipFlex]}>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={`Session: ${place}, ${SESSION_STATUS_LABEL[context.status]}, ${SESSION_MEDIA_LABEL[context.mediaHint]}, ${SESSION_CHANNEL_LABEL[context.channel]}. Change settings`}
          onPress={() => {
            chipPress.buzz();
            onOpenSession();
          }}
          onPressIn={chipPress.onPressIn}
          onPressOut={chipPress.onPressOut}
          style={styles.chip}
        >
          <Text style={styles.chipText} numberOfLines={1}>
            {summary}
          </Text>
        </Pressable>
      </Animated.View>
      <ControlButton label={flash ? 'Flash on' : 'Flash'} selected={flash} onPress={onToggleFlash}>
        <FlashGlyph color={flash ? colors.onAccent : glyph} />
      </ControlButton>
      <ControlButton label="Search the catalog" onPress={onOpenSearch}>
        <SearchGlyph color={glyph} />
      </ControlButton>
      {photosEnabled ? (
        <ControlButton label="Photos" hint="Choose pictures from your library to read, with no camera needed" disabled={photosRunning} onPress={onPhotos}>
          <PhotosGlyph color={glyph} />
        </ControlButton>
      ) : null}
      <ControlButton label="Add it by hand" onPress={onAddManually}>
        <PlusGlyph color={glyph} />
      </ControlButton>
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  row: { flexDirection: 'row', alignItems: 'center', gap: theme.spacing.sm, paddingHorizontal: theme.spacing.lg },
  title: { color: theme.fixed.white },
  chipFlex: { flex: 1 },
  chip: {
    minHeight: 44,
    justifyContent: 'center',
    paddingHorizontal: theme.spacing.md,
    borderRadius: theme.radius.pill,
    backgroundColor: theme.fixed.scrim,
  },
  chipText: { color: theme.fixed.white },
  icon: ({ selected }: { selected: boolean }) => ({
    width: 44,
    height: 44,
    borderRadius: theme.radius.pill,
    backgroundColor: selected ? theme.colors.accent : theme.fixed.scrim,
    alignItems: 'center',
    justifyContent: 'center',
  }),
}));
