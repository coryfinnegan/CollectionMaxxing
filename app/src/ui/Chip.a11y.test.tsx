import { render, screen } from '@testing-library/react-native';
import { Chip } from './Chip';

// WCAG 2.5.3 (Label in Name): the spoken name starts with the visible text; extra context goes in the hint.
describe('a chip', () => {
  it('is spoken as its visible label', async () => {
    await render(<Chip label="Any format" onPress={() => undefined} />);
    expect(screen.getByRole('button', { name: 'Any format' })).toBeTruthy();
  });

  it('speaks what it does as a hint, leaving the name equal to the visible label', async () => {
    await render(<Chip label="Any format" accessibilityHint="Filter by format" onPress={() => undefined} />);
    const chip = screen.getByRole('button', { name: 'Any format' });
    expect(chip.props.accessibilityHint).toBe('Filter by format');
  });
});
