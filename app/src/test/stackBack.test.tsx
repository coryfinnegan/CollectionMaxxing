// The way-back sweep (#356): every pushed screen inherits a visible back
// control from its stack layout — the phone header on native, an in-flow row
// on the web where pages have no header — with an accessible name and a 44pt
// target, and nowhere to go back to lands on the parent hub instead of
// stranding the page.
import { fireEvent } from '@testing-library/react-native';
import type { ReactElement } from 'react';
import { Platform, StyleSheet } from 'react-native';
import { renderWithProviders } from '@/test/render';

const mockRouter = { back: jest.fn(), replace: jest.fn(), push: jest.fn(), navigate: jest.fn(), canGoBack: jest.fn(() => true) };
const mockPathname = { current: '/settings' };
const mockCaptured: { screenOptions?: Record<string, unknown>; screens: { name: string; options?: Record<string, unknown> }[] } = {
  screenOptions: undefined,
  screens: [],
};

jest.mock('expo-router', () => {
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const React = require('react') as typeof import('react');
  function Stack({ children, screenOptions }: { children?: React.ReactNode; screenOptions?: unknown }) {
    mockCaptured.screenOptions = screenOptions as Record<string, unknown>;
    return React.createElement(React.Fragment, null, children);
  }
  Stack.Screen = function Screen({ name, options }: { name: string; options?: unknown }) {
    mockCaptured.screens.push({ name, options: options as Record<string, unknown> | undefined });
    return null;
  };
  return { Stack, useRouter: () => mockRouter, usePathname: () => mockPathname.current };
});
jest.mock('@/components/TabsFrame', () => {
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const React = require('react') as typeof import('react');
  return { TabsFrame: ({ children }: { children?: React.ReactNode }) => React.createElement(React.Fragment, null, children) };
});
jest.mock('@/components/RootFrame', () => {
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const React = require('react') as typeof import('react');
  return { RootFrame: ({ children }: { children?: React.ReactNode }) => React.createElement(React.Fragment, null, children) };
});

// eslint-disable-next-line @typescript-eslint/no-require-imports
const PagesLayout = require('@/app/(pages)/_layout').default as () => ReactElement;
// eslint-disable-next-line @typescript-eslint/no-require-imports
const RootLayout = require('@/app/_layout').default as () => ReactElement;

const originalOs = Platform.OS;
beforeEach(() => {
  mockCaptured.screenOptions = undefined;
  mockCaptured.screens = [];
  mockRouter.back.mockClear();
  mockRouter.replace.mockClear();
  mockRouter.canGoBack.mockReset();
  mockRouter.canGoBack.mockReturnValue(true);
  mockPathname.current = '/settings';
  Platform.OS = 'ios';
});
afterEach(() => {
  Platform.OS = originalOs;
});

type HeaderLeft = (props: object) => ReactElement;
type Captured = { screens: { name: string; options?: Record<string, unknown> }[]; screenOptions?: Record<string, unknown> };

// This file mounts several trees per test. Every render queries through its
// own render result (never the global screen), and unmounting is left to the
// automatic cleanup: manual unmounts mid-file break later renders.

/** Renders a layout once to capture its registrations. */
async function captureLayout(Layout: () => ReactElement): Promise<Captured> {
  await renderWithProviders(<Layout />);
  return { screens: [...mockCaptured.screens], screenOptions: mockCaptured.screenOptions };
}

function headerLeftFor(captured: Captured, screenName: string): HeaderLeft {
  const found = captured.screens.find((s) => s.name === screenName);
  const resolved = (found?.options?.headerLeft ?? captured.screenOptions?.headerLeft) as HeaderLeft | undefined;
  expect(resolved).toBeDefined();
  return resolved as HeaderLeft;
}

async function expectBackButton(headerLeft: HeaderLeft) {
  const view = await renderWithProviders(headerLeft({}));
  const back = view.getByRole('button', { name: 'Back' });
  const flat = StyleSheet.flatten(back.props.style);
  expect(flat.minHeight).toBeGreaterThanOrEqual(44);
  expect(flat.minWidth).toBeGreaterThanOrEqual(44);
}

async function pressBack(headerLeft: HeaderLeft) {
  const view = await renderWithProviders(headerLeft({}));
  await fireEvent.press(view.getByRole('button', { name: 'Back' }));
}

describe('every pushed page', () => {
  it('registers its screens with the layout, so the sweep below sees them all', async () => {
    const captured = await captureLayout(PagesLayout);

    const names = captured.screens.map((s) => s.name);
    expect(names).toContain('settings');
    expect(names).toContain('me/devices');
    expect(names).toContain('wishlist');
    expect(names.length).toBeGreaterThan(0);
  });

  it('inherits a header back button with an accessible name and a 44pt target', async () => {
    const captured = await captureLayout(PagesLayout);
    expect(captured.screens.length).toBeGreaterThan(0);

    for (const { name } of captured.screens) {
      mockPathname.current = `/${name}`;
      await expectBackButton(headerLeftFor(captured, name));
    }
  });

  it('shows no in-flow back button on the phone, where the header owns it', async () => {
    const view = await renderWithProviders(<PagesLayout />);

    expect(view.queryByRole('button', { name: 'Back' })).toBeNull();
  });

  it('shows one in-flow back button on the web, where pages have no header', async () => {
    Platform.OS = 'web';
    const view = await renderWithProviders(<PagesLayout />);

    expect(view.getAllByRole('button', { name: 'Back' })).toHaveLength(1);
  });
});

describe('every pushed root screen', () => {
  const pushed = ['item/[id]/index', 'item/[id]/edit', 'review', 'evals', 'result/[id]'];
  const pathnameFor: Record<string, string> = {
    'item/[id]/index': '/item/42',
    'item/[id]/edit': '/item/42/edit',
    review: '/review',
    evals: '/evals',
    'result/[id]': '/result/7',
  };

  it('registers its pushed screens with the layout, so the sweep below sees them all', async () => {
    const captured = await captureLayout(RootLayout);

    const names = captured.screens.map((s) => s.name);
    for (const name of pushed) expect(names).toContain(name);
  });

  it('inherits a header back button with an accessible name and a 44pt target', async () => {
    const captured = await captureLayout(RootLayout);

    for (const name of pushed) {
      mockPathname.current = pathnameFor[name];
      await expectBackButton(headerLeftFor(captured, name));
    }
  });

  it('leaves the groups and entry screens headerless, so the shared back never shows there', async () => {
    const captured = await captureLayout(RootLayout);

    for (const name of ['(tabs)', '(pages)', 'login', 'join/[token]', 'welcome/index', 'welcome/create', 'welcome/join']) {
      expect(captured.screens.find((s) => s.name === name)?.options?.headerShown).toBe(false);
    }
  });

  it('leaves the scanner header without a back button: its own Close owns that flow', async () => {
    const captured = await captureLayout(RootLayout);

    mockPathname.current = '/scanner';
    const view = await renderWithProviders(<>{headerLeftFor(captured, 'scanner')({})}</>);
    expect(view.queryByRole('button', { name: 'Back' })).toBeNull();
  });
});

describe('the back button', () => {
  it('goes back when there is somewhere to go back to', async () => {
    mockRouter.canGoBack.mockReturnValue(true);
    const captured = await captureLayout(PagesLayout);

    await pressBack(headerLeftFor(captured, 'settings'));

    expect(mockRouter.back).toHaveBeenCalled();
    expect(mockRouter.replace).not.toHaveBeenCalled();
  });

  it.each([
    ['/settings', '/me'],
    ['/members', '/me'],
    ['/about', '/me'],
    ['/me/devices', '/me'],
    ['/me/delete', '/me'],
  ])('from a fresh load of %s lands on %s, where there is no history', async (pathname, hub) => {
    mockRouter.canGoBack.mockReturnValue(false);
    mockPathname.current = pathname;
    const captured = await captureLayout(PagesLayout);

    await pressBack(headerLeftFor(captured, pathname.slice(1)));

    expect(mockRouter.replace).toHaveBeenCalledWith(hub);
  });

  it.each(['/wishlist', '/lent-out'])('from a fresh load of %s lands on the library', async (pathname) => {
    mockRouter.canGoBack.mockReturnValue(false);
    mockPathname.current = pathname;
    const captured = await captureLayout(PagesLayout);

    await pressBack(headerLeftFor(captured, pathname.slice(1)));

    expect(mockRouter.replace).toHaveBeenCalledWith('/library');
  });

  it.each([
    ['/item/42', 'item/[id]/index', '/library'],
    ['/review', 'review', '/library'],
    ['/result/7', 'result/[id]', '/library'],
    ['/evals', 'evals', '/settings'],
  ])('from a fresh load of %s lands on %s', async (pathname, screenName, hub) => {
    mockRouter.canGoBack.mockReturnValue(false);
    mockPathname.current = pathname;
    const captured = await captureLayout(RootLayout);

    await pressBack(headerLeftFor(captured, screenName));

    expect(mockRouter.replace).toHaveBeenCalledWith(hub);
  });
});
