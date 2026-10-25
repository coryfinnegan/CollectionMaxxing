// The web twin draws the same moments as still art (lottie-react-native's web
// player is not shipped), and stays out of the way when Reduce Motion is on.
import { render, screen } from '@testing-library/react-native';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { LottieSpot } from './LottieSpot.web';

jest.mock('@/lib/a11y/useReduceMotion', () => ({ useReduceMotion: jest.fn() }));

const reduceMotion = (on: boolean) => jest.mocked(useReduceMotion).mockReturnValue(on);
// The spot hides from screen readers, so the queries look at hidden elements too.
const hidden = { includeHiddenElements: true };

describe('the web Lottie twin', () => {
  it('draws the same moments as still art', async () => {
    reduceMotion(false);
    for (const name of ['success', 'empty-library', 'no-results'] as const) {
      const { unmount } = await render(<LottieSpot name={name} testID={`spot-${name}`} />);
      expect(screen.getByTestId(`spot-${name}`, hidden)).toBeTruthy();
      await unmount();
    }
  });

  it('renders nothing when Reduce Motion is on', async () => {
    reduceMotion(true);
    await render(<LottieSpot name="success" testID="moment" />);
    expect(screen.queryByTestId('moment')).toBeNull();
  });
});
