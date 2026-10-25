// The web tab layout (#303): headless tabs with Library and Me on narrow
// screens, no Scan tab, and the theme's surface bar. The trigger list always
// renders: triggers define which routes exist, so the scan stand-in keeps a
// hidden trigger even when the bar hides on wide screens.
import { screen } from '@testing-library/react-native';
import { Dimensions } from 'react-native';
import TabsLayout from '@/app/(tabs)/_layout.web';
import { renderWithProviders } from '@/test/render';
import { lightColors } from '@/theme/tokens';

const mockPathname: { current: string } = { current: '/library' };
jest.mock('expo-router', () => ({ usePathname: () => mockPathname.current }));
jest.mock('@/components/TabsFrame', () => ({ TabsFrame: ({ children }: { children?: unknown }) => children }));

type CapturedTrigger = { name: string; href?: string; style?: unknown; children?: unknown };
const mockTriggers: CapturedTrigger[] = [];
const mockListProps: { current: Record<string, unknown> | null } = { current: null };
jest.mock('expo-router/ui', () => {
  // Mock factories cannot see imports, so react-native comes in through require.
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const { View: MockView, Pressable: MockPressable } = require('react-native');
  const TabList = (props: Record<string, unknown>) => {
    mockListProps.current = props;
    return (
      <MockView testID="tab-list" style={props.style as never}>
        {props.children as never}
      </MockView>
    );
  };
  // The real trigger is a Pressable (an accessibility element); a plain View would hide it from role queries.
  const TabTrigger = (props: CapturedTrigger) => {
    mockTriggers.push(props);
    const { name: _name, href: _href, ...rest } = props;
    return <MockPressable {...rest}>{props.children as never}</MockPressable>;
  };
  return { Tabs: ({ children }: { children?: unknown }) => children, TabList, TabTrigger, TabSlot: () => null };
});

const windowOf = (width: number) => ({ width, height: 800, scale: 2, fontScale: 2 });
let dimensionsSpy: jest.SpyInstance;

beforeEach(() => {
  mockTriggers.length = 0;
  mockListProps.current = null;
  mockPathname.current = '/library';
  dimensionsSpy = jest.spyOn(Dimensions, 'get').mockReturnValue(windowOf(390));
});

afterEach(() => dimensionsSpy.mockRestore());

describe('web TabsLayout', () => {
  it('shows Library and Me tabs, and no Scan tab', async () => {
    await renderWithProviders(<TabsLayout />);

    expect(screen.getByRole('tab', { name: 'Library' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Me' })).toBeTruthy();
    expect(screen.queryByRole('tab', { name: 'Scan' })).toBeNull();
    expect(mockTriggers.filter((t) => t.name === 'scan')).toHaveLength(1);
    expect(mockTriggers.find((t) => t.name === 'scan')?.href).toBe('/scan');
  });

  it('links each tab at its route', async () => {
    await renderWithProviders(<TabsLayout />);

    expect(mockTriggers.find((t) => t.name === 'library')?.href).toBe('/library');
    expect(mockTriggers.find((t) => t.name === 'me')?.href).toBe('/me');
  });

  it('marks only the open tab selected', async () => {
    await renderWithProviders(<TabsLayout />);
    expect(screen.getByRole('tab', { name: 'Library' }).props.accessibilityState).toEqual({ selected: true });
    expect(screen.getByRole('tab', { name: 'Me' }).props.accessibilityState).toEqual({ selected: false });

    mockPathname.current = '/me';
    mockTriggers.length = 0;
    await renderWithProviders(<TabsLayout />);
    expect(screen.getByRole('tab', { name: 'Library' }).props.accessibilityState).toEqual({ selected: false });
    expect(screen.getByRole('tab', { name: 'Me' }).props.accessibilityState).toEqual({ selected: true });
  });

  it('selects nothing on a page outside the tabs', async () => {
    mockPathname.current = '/scan';
    await renderWithProviders(<TabsLayout />);

    expect(screen.getByRole('tab', { name: 'Library' }).props.accessibilityState).toEqual({ selected: false });
    expect(screen.getByRole('tab', { name: 'Me' }).props.accessibilityState).toEqual({ selected: false });
  });

  it('paints the bar with the theme and keeps every tab a finger wide', async () => {
    await renderWithProviders(<TabsLayout />);

    expect(mockListProps.current?.style).toMatchObject({ backgroundColor: lightColors.surface });
    for (const trigger of mockTriggers.filter((t) => t.name !== 'scan')) {
      expect(trigger.style).toMatchObject({ minHeight: 44 });
    }
  });

  it('declares every tab on wide screens but shows no bar', async () => {
    dimensionsSpy.mockReturnValue(windowOf(1280));
    await renderWithProviders(<TabsLayout />);

    expect(mockTriggers.map((t) => t.name).sort()).toEqual(['library', 'me', 'scan']);
    expect(mockListProps.current?.style).toMatchObject({ display: 'none' });
    expect(screen.queryByRole('tab', { name: 'Library' })).toBeNull();
  });
});
