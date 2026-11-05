import { render, screen } from '@testing-library/react-native';
import { ListRow } from './ListRow';

// WCAG 2.5.3 (Label in Name): a row sets no accessibilityLabel, so its spoken name is built from the words it shows and can't drift from them.
describe('a pressable list row', () => {
  it('is spoken with its title and subtitle, so nothing it shows is lost to VoiceOver', async () => {
    await render(<ListRow title="Living room" subtitle="3 shelves" onPress={() => undefined} />);
    const row = screen.getByRole('button', { name: /^Living room.*3 shelves$/ });
    expect(row.props.accessibilityLabel).toBeUndefined();
  });

  it('is spoken as its title alone when there is no subtitle', async () => {
    await render(<ListRow title="Living room" onPress={() => undefined} />);
    expect(screen.getByRole('button', { name: 'Living room' })).toBeTruthy();
  });

  it('keeps its chevron out of the spoken name', async () => {
    await render(<ListRow title="Living room" chevron onPress={() => undefined} />);
    expect(screen.getByRole('button', { name: 'Living room' })).toBeTruthy();
  });
});
