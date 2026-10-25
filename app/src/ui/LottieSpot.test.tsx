// The Lottie spot plays its moment, and stays out of the way when Reduce Motion is on.
import { render, screen } from '@testing-library/react-native';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { LottieSpot } from './LottieSpot';

jest.mock('@/lib/a11y/useReduceMotion', () => ({ useReduceMotion: jest.fn() }));

const reduceMotion = (on: boolean) => jest.mocked(useReduceMotion).mockReturnValue(on);

// The spot hides from screen readers, so the queries look at hidden elements too.
const hidden = { includeHiddenElements: true };

describe('LottieSpot', () => {
  it('plays the named moment at the asked size', async () => {
    reduceMotion(false);
    await render(<LottieSpot name="success" size={120} testID="moment" />);
    const spot = screen.getByTestId('moment', hidden);
    expect(spot.props.source.nm).toBe('success-check');
    expect(spot.props.style).toEqual({ width: 120, height: 120 });
    expect(spot.props.loop).toBe(false);
  });

  it('loops the empty-state spots', async () => {
    reduceMotion(false);
    await render(<LottieSpot name="empty-library" loop testID="moment" />);
    expect(screen.getByTestId('moment', hidden).props.loop).toBe(true);
  });

  it('renders nothing when Reduce Motion is on', async () => {
    reduceMotion(true);
    await render(<LottieSpot name="success" testID="moment" />);
    expect(screen.queryByTestId('moment')).toBeNull();
  });

  it('hides from screen readers; the words next to it say what matters', async () => {
    reduceMotion(false);
    await render(<LottieSpot name="success" testID="moment" />);
    const wrapper = screen.getByTestId('moment', hidden).parent!;
    expect(wrapper.props.accessibilityElementsHidden).toBe(true);
    expect(wrapper.props.importantForAccessibility).toBe('no-hide-descendants');
  });
});
