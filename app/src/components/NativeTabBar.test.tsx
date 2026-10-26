// The phone tab bar (#303): native tabs with SF Symbol icons in default/selected
// variants, equal labels, and no custom backgrounds: iOS 26 Liquid Glass owns the
// bar, and tintColor names the selected tab. The Scan pill floats above every tab
// but Scan itself.
import { screen } from '@testing-library/react-native';
import type { ReactElement } from 'react';
import { renderWithProviders } from '@/test/render';
import { lightColors } from '@/theme/tokens';
import { NativeTabBar } from './NativeTabBar';
import { ScanPill } from './ScanPill';

const mockRouter = { navigate: jest.fn(), push: jest.fn(), back: jest.fn(), replace: jest.fn() };
const mockPathname: { current: string } = { current: '/library' };
jest.mock('expo-router', () => ({ usePathname: () => mockPathname.current, useRouter: () => mockRouter }));

type CapturedTrigger = { name: string; children: ReactElement | ReactElement[] };
const mockTriggers: CapturedTrigger[] = [];
const mockTabsProps: { current: Record<string, unknown> | null } = { current: null };
const mockAccessoryChildren: { current: unknown } = { current: undefined };
jest.mock('expo-router/unstable-native-tabs', () => {
  // Mock factories cannot see imports, so react-native comes in through require.
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const { View: MockView } = require('react-native');
  const Label = () => null;
  const Icon = () => null;
  const Trigger = (props: CapturedTrigger) => {
    mockTriggers.push(props);
    return null;
  };
  const BottomAccessory = ({ children }: { children?: unknown }) => {
    mockAccessoryChildren.current = children;
    return <MockView testID="bottom-accessory">{children as never}</MockView>;
  };
  (BottomAccessory as unknown as { usePlacement: () => 'regular' }).usePlacement = () => 'regular';
  const NativeTabs = (props: Record<string, unknown>) => {
    mockTabsProps.current = props;
    return <MockView testID="native-tabs">{props.children as never}</MockView>;
  };
  (NativeTabs as unknown as Record<string, unknown>).Trigger = Object.assign(Trigger, { Label, Icon });
  (NativeTabs as unknown as Record<string, unknown>).BottomAccessory = BottomAccessory;
  return { NativeTabs };
});

const { NativeTabs } = jest.requireMock('expo-router/unstable-native-tabs') as typeof import('expo-router/unstable-native-tabs');

beforeEach(() => {
  mockTriggers.length = 0;
  mockTabsProps.current = null;
  mockAccessoryChildren.current = undefined;
  mockPathname.current = '/library';
});

function iconOf(trigger: CapturedTrigger): Record<string, unknown> {
  const children = Array.isArray(trigger.children) ? trigger.children : [trigger.children];
  const icon = children.find((child) => child.type === NativeTabs.Trigger.Icon);
  if (!icon) throw new Error(`tab ${trigger.name} has no icon`);
  return icon.props as Record<string, unknown>;
}

function labelOf(trigger: CapturedTrigger): { text: unknown; props: Record<string, unknown> } {
  const children = Array.isArray(trigger.children) ? trigger.children : [trigger.children];
  const label = children.find((child) => child.type === NativeTabs.Trigger.Label);
  if (!label) throw new Error(`tab ${trigger.name} has no label`);
  const props = label.props as Record<string, unknown>;
  return { text: props.children, props };
}

describe('NativeTabBar', () => {
  it('tabs Library, Scan, and Me with icons and equal labels', async () => {
    await renderWithProviders(<NativeTabBar />);

    expect(mockTriggers.map((t) => t.name)).toEqual(['library', 'scan', 'me/index']);
    expect(mockTriggers.map((t) => labelOf(t).text)).toEqual(['Library', 'Scan', 'Me']);
    expect(iconOf(mockTriggers[0]).sf).toEqual({ default: 'books.vertical', selected: 'books.vertical.fill' });
    expect(iconOf(mockTriggers[1]).sf).toEqual('barcode.viewfinder');
    expect(iconOf(mockTriggers[2]).sf).toEqual({ default: 'person.crop.circle', selected: 'person.crop.circle.fill' });
    // No tab restyles its own label: the oversized Scan label is gone.
    for (const trigger of mockTriggers) {
      expect(labelOf(trigger).props.selectedStyle).toBeUndefined();
      expect(labelOf(trigger).props.hidden).toBeUndefined();
    }
  });

  it('tints the selected tab and leaves the glass alone', async () => {
    await renderWithProviders(<NativeTabBar />);

    expect(mockTabsProps.current?.tintColor).toBe(lightColors.accent);
    expect(mockTabsProps.current?.backgroundColor).toBeUndefined();
    expect(mockTabsProps.current?.blurEffect).toBeUndefined();
    expect(mockTabsProps.current?.labelStyle).toBeUndefined();
  });

  it('floats the Scan pill above every tab but Scan itself', async () => {
    await renderWithProviders(<NativeTabBar />);
    expect(screen.getByText('Scan')).toBeTruthy();
    const pill = mockAccessoryChildren.current as ReactElement;
    expect(pill.type).toBe(ScanPill);

    mockPathname.current = '/scan';
    mockTriggers.length = 0;
    await renderWithProviders(<NativeTabBar />);
    expect(mockAccessoryChildren.current).toBeNull();
    expect(screen.queryByText('Scan')).toBeNull();
  });
});
