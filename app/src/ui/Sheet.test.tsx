// Every sheet carries the same way out (#356): the shared Sheet owns a Close
// button labelled with the sheet's title, so no sheet is gesture-only.
import { fireEvent, screen } from '@testing-library/react-native';
import { Text } from 'react-native';
import { renderWithProviders } from '@/test/render';
import { Sheet } from './Sheet';

describe('Sheet', () => {
  it('labels its close button with the sheet title', async () => {
    await renderWithProviders(
      <Sheet title="Format" onClose={() => undefined}>
        <Text>Options</Text>
      </Sheet>,
    );

    expect(screen.getByRole('button', { name: 'Close Format' })).toBeTruthy();
  });

  it('closes when the close button is pressed', async () => {
    const onClose = jest.fn();
    await renderWithProviders(
      <Sheet title="Format" onClose={onClose}>
        <Text>Options</Text>
      </Sheet>,
    );

    await fireEvent.press(screen.getByRole('button', { name: 'Close Format' }));

    expect(onClose).toHaveBeenCalled();
  });
});
