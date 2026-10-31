// The way-back sweep (#356, #445): every pushed screen inherits a visible back
// control from its stack layout — the shared custom header on the phone, the web
// header or an in-flow row on the web — with an accessible name and a 44pt
// target, and nowhere to go back to lands on the parent hub instead of
// stranding the page. The control is a chevron glyph alone: the spoken name
// stays Back, but no Back text renders and no pill background wraps it (a custom
// header owns the phone bar, so iOS draws no bar-button glass around it). The
// web layout also pins the native back label to Back (#372), so it never falls
// back to a route-group name like (tabs); on the phone every pushed screen
// declares an explicit header title instead, so the custom header cannot fall
// back to a route name either.
import { fireEvent } from '@testing-library/react-native';
import type { ReactElement } from 'react';
import { Dimensions, Platform, StyleSheet } from 'react-native';
import { StackBackButton } from '@/components/StackBackButton';
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
jest.mock('@/components/Sidebar', () => {
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const React = require('react') as typeof import('react');
  return { Sidebar: () => React.createElement(React.Fragment, null) };
});

// eslint-disable-next-line @typescript-eslint/no-require-imports
const PagesLayout = require('@/app/(pages)/_layout').default as () => ReactElement;
// eslint-disable-next-line @typescript-eslint/no-require-imports
const RootLayout = require('@/app/_layout').default as () => ReactElement;
// eslint-disable-next-line @typescript-eslint/no-require-imports
const WebRootLayout = require('@/app/_layout.web').default as () => ReactElement;

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
let dimensionsSpy: jest.SpyInstance | undefined;
afterEach(() => {
  Platform.OS = originalOs;
  dimensionsSpy?.mockRestore();
  dimensionsSpy = undefined;
});

type Header = (props: { options?: Record<string, unknown> }) => ReactElement;
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

/** The shared phone header a screen resolves: its own option, else the layout's shared one. */
function headerFor(captured: Captured, screenName: string): Header {
  const found = captured.screens.find((s) => s.name === screenName);
  const resolved = (found?.options?.header ?? captured.screenOptions?.header) as Header | undefined;
  expect(resolved).toBeDefined();
  return resolved as Header;
}

function headerLeftFor(captured: Captured, screenName: string): HeaderLeft {
  const found = captured.screens.find((s) => s.name === screenName);
  const resolved = (found?.options?.headerLeft ?? captured.screenOptions?.headerLeft) as HeaderLeft | undefined;
  expect(resolved).toBeDefined();
  return resolved as HeaderLeft;
}

/** The options the navigator hands the header for a screen. */
function optionsFor(captured: Captured, screenName: string): Record<string, unknown> {
  return captured.screens.find((s) => s.name === screenName)?.options ?? {};
}

/** The native back label a web screen resolves: its own option, else the layout's shared one. */
function backTitleFor(captured: Captured, screenName: string): unknown {
  const found = captured.screens.find((s) => s.name === screenName);
  return found?.options?.headerBackTitle ?? captured.screenOptions?.headerBackTitle;
}

type Rendered = Awaited<ReturnType<typeof renderWithProviders>>;

/** The chevron-only assertions every back control meets, wherever it renders. */
async function expectChevronBack(view: Rendered) {
  const back = view.getByRole('button', { name: 'Back' });
  const flat = StyleSheet.flatten(back.props.style);
  expect(flat.minHeight).toBeGreaterThanOrEqual(44);
  expect(flat.minWidth).toBeGreaterThanOrEqual(44);
  // Chevron only (#445): spoken as Back, with no Back text rendered.
  expect(view.queryByText(/Back/)).toBeNull();
}

async function expectHeaderBack(header: Header, screenOptions: Record<string, unknown>) {
  const view = await renderWithProviders(header({ options: screenOptions }));
  await expectChevronBack(view);
  // The header names the screen from its explicit title, never a route name.
  const title = screenOptions.title;
  expect(typeof title).toBe('string');
  expect(view.getByText(title as string)).toBeTruthy();
}

async function expectHeaderLeftBack(headerLeft: HeaderLeft) {
  const view = await renderWithProviders(headerLeft({}));
  await expectChevronBack(view);
}

async function pressHeaderBack(header: Header, screenOptions: Record<string, unknown>) {
  const view = await renderWithProviders(header({ options: screenOptions }));
  await fireEvent.press(view.getByRole('button', { name: 'Back' }));
}

describe('the shared back control', () => {
  it('is spoken as Back with a 44pt target and no Back text', async () => {
    const view = await renderWithProviders(<StackBackButton />);

    await expectChevronBack(view);
  });

  it('renders no button on the scanner: its own Close owns that flow', async () => {
    mockPathname.current = '/scanner';
    const view = await renderWithProviders(<StackBackButton />);

    expect(view.queryByRole('button', { name: 'Back' })).toBeNull();
  });
});

describe('every pushed page', () => {
  it('registers its screens with the layout, so the sweep below sees them all', async () => {
    const captured = await captureLayout(PagesLayout);

    const names = captured.screens.map((s) => s.name);
    expect(names).toContain('settings');
    expect(names).toContain('me/devices');
    expect(names).toContain('wishlist');
    expect(names.length).toBeGreaterThan(0);
  });

  it('inherits the shared phone header with a chevron back and its own title', async () => {
    const captured = await captureLayout(PagesLayout);
    expect(captured.screens.length).toBeGreaterThan(0);

    for (const { name } of captured.screens) {
      mockPathname.current = `/${name}`;
      await expectHeaderBack(headerFor(captured, name), optionsFor(captured, name));
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

  it('inherits the shared phone header with a chevron back and its own title', async () => {
    const captured = await captureLayout(RootLayout);

    for (const name of pushed) {
      mockPathname.current = pathnameFor[name];
      await expectHeaderBack(headerFor(captured, name), optionsFor(captured, name));
    }
  });

  it('leaves the groups and entry screens headerless, so the shared back never shows there', async () => {
    const captured = await captureLayout(RootLayout);

    for (const name of ['(tabs)', '(pages)', 'login', 'join/[token]', 'welcome/index', 'welcome/create', 'welcome/join']) {
      expect(captured.screens.find((s) => s.name === name)?.options?.headerShown).toBe(false);
    }
  });

  it('leaves the scanner header titled with no back button: its own Close owns that flow', async () => {
    const captured = await captureLayout(RootLayout);

    mockPathname.current = '/scanner';
    const view = await renderWithProviders(<>{headerFor(captured, 'scanner')({ options: optionsFor(captured, 'scanner') })}</>);
    expect(view.queryByRole('button', { name: 'Back' })).toBeNull();
    expect(view.getByText('Scanner')).toBeTruthy();
  });

  it('declares an explicit title on every headed screen, so the header never falls back to a route name', async () => {
    const captured = await captureLayout(RootLayout);

    for (const { options } of captured.screens) {
      if (options?.headerShown === false) continue;
      expect(typeof options?.title).toBe('string');
    }
  });
});

describe('every pushed page title', () => {
  it('declares an explicit title on every page, so the header never falls back to a route name', async () => {
    const captured = await captureLayout(PagesLayout);
    expect(captured.screens.length).toBeGreaterThan(0);

    for (const { options } of captured.screens) {
      expect(typeof options?.title).toBe('string');
    }
  });
});

describe('every pushed web root screen', () => {
  it('keeps a chevron back in the web header, with no Back text', async () => {
    const captured = await captureLayout(WebRootLayout);

    for (const name of ['item/[id]/index', 'item/[id]/edit', 'review', 'evals', 'result/[id]']) {
      await expectHeaderLeftBack(headerLeftFor(captured, name));
    }
  });
});

describe('the back button', () => {
  it('goes back when there is somewhere to go back to', async () => {
    mockRouter.canGoBack.mockReturnValue(true);
    const captured = await captureLayout(PagesLayout);

    await pressHeaderBack(headerFor(captured, 'settings'), optionsFor(captured, 'settings'));

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

    await pressHeaderBack(headerFor(captured, pathname.slice(1)), optionsFor(captured, pathname.slice(1)));

    expect(mockRouter.replace).toHaveBeenCalledWith(hub);
  });

  it('from a fresh load of /sold on the phone lands on Me, the hub that links to it', async () => {
    mockRouter.canGoBack.mockReturnValue(false);
    mockPathname.current = '/sold';
    const captured = await captureLayout(PagesLayout);

    await pressHeaderBack(headerFor(captured, 'sold'), optionsFor(captured, 'sold'));

    expect(mockRouter.replace).toHaveBeenCalledWith('/me');
  });

  it('from a fresh load of /sold on the narrow web lands on Me too: the bottom bar has it', async () => {
    Platform.OS = 'web';
    dimensionsSpy = jest.spyOn(Dimensions, 'get').mockReturnValue({ width: 600, height: 900, scale: 1, fontScale: 1 });
    mockRouter.canGoBack.mockReturnValue(false);
    mockPathname.current = '/sold';
    const captured = await captureLayout(PagesLayout);

    await pressHeaderBack(headerFor(captured, 'sold'), optionsFor(captured, 'sold'));

    expect(mockRouter.replace).toHaveBeenCalledWith('/me');
  });

  it('from a fresh load of /sold on the wide web lands on the library: there is no Me there', async () => {
    Platform.OS = 'web';
    dimensionsSpy = jest.spyOn(Dimensions, 'get').mockReturnValue({ width: 1280, height: 800, scale: 1, fontScale: 1 });
    mockRouter.canGoBack.mockReturnValue(false);
    mockPathname.current = '/sold';
    const captured = await captureLayout(PagesLayout);

    await pressHeaderBack(headerFor(captured, 'sold'), optionsFor(captured, 'sold'));

    expect(mockRouter.replace).toHaveBeenCalledWith('/library');
  });

  it.each(['/wishlist', '/lent-out'])('from a fresh load of %s lands on the library', async (pathname) => {
    mockRouter.canGoBack.mockReturnValue(false);
    mockPathname.current = pathname;
    const captured = await captureLayout(PagesLayout);

    await pressHeaderBack(headerFor(captured, pathname.slice(1)), optionsFor(captured, pathname.slice(1)));

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

    await pressHeaderBack(headerFor(captured, screenName), optionsFor(captured, screenName));

    expect(mockRouter.replace).toHaveBeenCalledWith(hub);
  });
});

describe('the native back label on the web (#372)', () => {
  it('pins the label in the shared web options, so it never falls back to a route name', async () => {
    const captured = await captureLayout(WebRootLayout);

    expect(captured.screenOptions?.headerBackTitle).toBe('Back');
  });

  it('resolves every web root screen to Back, never a route-group name', async () => {
    const captured = await captureLayout(WebRootLayout);
    expect(captured.screens.length).toBeGreaterThan(0);

    for (const { name } of captured.screens) {
      expect(backTitleFor(captured, name)).toBe('Back');
    }
  });
});
