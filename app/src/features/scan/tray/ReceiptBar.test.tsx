// The scanner bottom bar (#451): session counts plus the Review button. No expansion, no receipt lines, no undo.
import { fireEvent, render, screen } from '@testing-library/react-native';
import type { SessionCounters } from '../session/sessionCounters';
import { ReceiptBar } from './ReceiptBar';

const mockGlassState = { available: false };

jest.mock('expo-glass-effect', () => {
  const React = jest.requireActual<typeof import('react')>('react');
  const { View } = jest.requireActual<typeof import('react-native')>('react-native');
  return {
    GlassView: (props: object) => React.createElement(View, { ...props, accessibilityLabel: 'liquid glass' }),
    isLiquidGlassAvailable: () => mockGlassState.available,
  };
});

const noop = () => undefined;
const ZERO: SessionCounters = { captured: 0, added: 0, toReview: 0, owned: 0, skipped: 0 };

const bar = (props: Partial<React.ComponentProps<typeof ReceiptBar>> = {}) => {
  mockGlassState.available = false;
  return render(<ReceiptBar online items={3} processing={1} problem={null} notice={null} onReview={noop} {...props} />);
};

describe('ReceiptBar dock', () => {
  it('keeps the count bar a plain surface bar without liquid glass', async () => {
    await bar({ counters: ZERO });

    expect(screen.getByText('3 items · 1 processing')).toBeTruthy();
    expect(screen.queryByLabelText('liquid glass')).toBeNull();
  });

  it('floats the count bar in liquid glass on iOS 26+', async () => {
    mockGlassState.available = true;
    await render(<ReceiptBar online items={3} processing={1} problem={null} notice={null} onReview={noop} />);

    expect(screen.getByLabelText('liquid glass')).toBeTruthy();
    expect(screen.getByText('3 items · 1 processing')).toBeTruthy();
  });
});

describe('ReceiptBar status line', () => {
  it('reads the tray summary without counters', async () => {
    await bar({ items: 1, processing: 0 });

    expect(screen.getByText('1 item')).toBeTruthy();
  });

  it('merges the tray and the session counters into one status line', async () => {
    await bar({ items: 5, processing: 1, counters: { ...ZERO, added: 2, toReview: 4, owned: 1 } });

    expect(screen.getByText('5 items · 1 processing · 4 to review · 1 owned · 2 added')).toBeTruthy();
  });

  it('announces count changes to screen readers', async () => {
    const view = await bar({ items: 1, processing: 0 });
    expect(view.getByText('1 item').props.accessibilityLiveRegion).toBe('polite');
  });

  it('badges offline and keeps counting', async () => {
    await bar({ online: false, items: 1, processing: 1, counters: { ...ZERO, toReview: 1 } });

    expect(screen.getByText('1 item · 1 processing · 1 to review')).toBeTruthy();
    expect(screen.getByLabelText('Offline')).toBeTruthy();
    expect(screen.getByText(/Your scans are saved and will finish when you're back online/)).toBeTruthy();
  });

  it('shows the problem and the notice when there is one', async () => {
    await bar({ problem: 'The server said no.', notice: 'Heads up.' });

    expect(screen.getByText('The server said no.')).toBeTruthy();
    expect(screen.getByText('Heads up.')).toBeTruthy();
  });
});

describe('ReceiptBar review button', () => {
  it('labels Review with the pending count', async () => {
    await bar({ items: 3, processing: 0 });

    expect(screen.getByRole('button', { name: 'Review 3 items' })).toBeTruthy();
  });

  it('uses the singular for one pending item', async () => {
    await bar({ items: 1, processing: 0 });

    expect(screen.getByRole('button', { name: 'Review 1 item' })).toBeTruthy();
  });

  it('opens Review on tap', async () => {
    const onReview = jest.fn();
    await bar({ items: 2, processing: 0, onReview });

    fireEvent.press(screen.getByRole('button', { name: 'Review 2 items' }));

    expect(onReview).toHaveBeenCalledTimes(1);
  });

  it('stays a bare, disabled Review with nothing scanned', async () => {
    await bar({ items: 0, processing: 0 });

    const button = screen.getByRole('button', { name: 'Review' });
    expect(button.props.accessibilityState.disabled).toBe(true);
  });
});

describe('ReceiptBar removals (#451)', () => {
  it('has no expansion affordance and no undo', async () => {
    await bar({ items: 3, processing: 1, counters: { ...ZERO, toReview: 3 } });

    expect(screen.queryByLabelText(/the receipt/)).toBeNull();
    expect(screen.queryByText('Undo last add')).toBeNull();
    expect(screen.queryByLabelText('Undo last add')).toBeNull();
    // The status is text, not a button: the only button is Review.
    expect(screen.getAllByRole('button')).toHaveLength(1);
  });
});
