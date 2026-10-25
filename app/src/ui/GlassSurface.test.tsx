// Floating surfaces (#306): native liquid glass where iOS 26+ offers it, a plain view everywhere else.
import { StyleSheet } from 'react-native';
import { render, screen } from '@testing-library/react-native';
import { Text } from './Text';
import { GlassSurface } from './GlassSurface';

const mockGlassState = { available: false, tintSeen: null as unknown };

jest.mock('expo-glass-effect', () => {
  const React = jest.requireActual<typeof import('react')>('react');
  const { View } = jest.requireActual<typeof import('react-native')>('react-native');
  return {
    GlassView: ({ tintColor, ...rest }: { tintColor?: unknown }) => {
      mockGlassState.tintSeen = tintColor;
      return React.createElement(View, { ...rest, accessibilityLabel: 'liquid glass' });
    },
    isLiquidGlassAvailable: () => mockGlassState.available,
  };
});

beforeEach(() => {
  mockGlassState.available = false;
  mockGlassState.tintSeen = null;
});

describe('GlassSurface', () => {
  it('renders its children in a plain view with the fallback background when glass is unavailable', async () => {
    await render(
      <GlassSurface style={{ padding: 8 }} fallbackStyle={{ backgroundColor: 'rgb(1, 2, 3)' }} tint="rgb(4, 5, 6)" testID="surface">
        <Text>Inside</Text>
      </GlassSurface>,
    );

    expect(screen.getByText('Inside')).toBeTruthy();
    expect(screen.queryByLabelText('liquid glass')).toBeNull();
    expect(StyleSheet.flatten(screen.getByTestId('surface').props.style)).toMatchObject({ padding: 8, backgroundColor: 'rgb(1, 2, 3)' });
  });

  it('renders native glass with the tint, and no fallback background, when iOS 26+ offers it', async () => {
    mockGlassState.available = true;

    await render(
      <GlassSurface style={{ padding: 8 }} fallbackStyle={{ backgroundColor: 'rgb(1, 2, 3)' }} tint="rgb(4, 5, 6)" testID="surface">
        <Text>Inside</Text>
      </GlassSurface>,
    );

    expect(screen.getByLabelText('liquid glass')).toBeTruthy();
    expect(screen.getByText('Inside')).toBeTruthy();
    expect(mockGlassState.tintSeen).toBe('rgb(4, 5, 6)');
    expect(StyleSheet.flatten(screen.getByTestId('surface').props.style)).toMatchObject({ padding: 8 });
    expect(StyleSheet.flatten(screen.getByTestId('surface').props.style).backgroundColor).toBeUndefined();
  });
});
