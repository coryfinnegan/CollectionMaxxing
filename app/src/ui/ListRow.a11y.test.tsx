import { render, screen } from '@testing-library/react-native';
import { ListRow } from './ListRow';

describe('a pressable list row', () => {
  it('is spoken with its subtitle, so nothing it shows is lost to VoiceOver', async () => {
    await render(<ListRow title="Living room" subtitle="3 shelves" onPress={() => undefined} />);
    expect(screen.getByRole('button', { name: 'Living room, 3 shelves' })).toBeTruthy();
  });

  it('is spoken as its title alone when there is no subtitle', async () => {
    await render(<ListRow title="Living room" onPress={() => undefined} />);
    expect(screen.getByRole('button', { name: 'Living room' })).toBeTruthy();
  });
});
