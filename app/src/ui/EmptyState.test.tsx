// Empty states get a small looping spot above the words; errors stay bare.
import { render, screen } from '@testing-library/react-native';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { EmptyState } from './EmptyState';

jest.mock('@/lib/a11y/useReduceMotion', () => ({ useReduceMotion: jest.fn() }));

const reduceMotion = (on: boolean) => jest.mocked(useReduceMotion).mockReturnValue(on);

describe('EmptyState', () => {
  it('shows the spot above the words when asked', async () => {
    reduceMotion(false);
    await render(<EmptyState title="Nothing here yet." animation="empty-library" />);
    expect(screen.getByTestId('empty-spot', { includeHiddenElements: true })).toBeTruthy();
    expect(screen.getByText('Nothing here yet.')).toBeTruthy();
  });

  it('stays bare without an animation, and under Reduce Motion', async () => {
    reduceMotion(false);
    const bare = await render(<EmptyState title="Couldn't load your library" />);
    expect(bare.queryByTestId('empty-spot')).toBeNull();

    reduceMotion(true);
    const still = await render(<EmptyState title="Nothing here yet." animation="empty-library" />);
    expect(still.queryByTestId('empty-spot')).toBeNull();
  });
});
