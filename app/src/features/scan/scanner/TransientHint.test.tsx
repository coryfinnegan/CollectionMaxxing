// A scanner hint that shows once per condition and fades: never a permanent pill (#408).
import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { useState } from 'react';
import { renderWithProviders } from '@/test/render';
import { Button } from '@/ui';
import { TransientHint } from './TransientHint';

const hint = (visible: boolean, onPress = jest.fn(), visibleMs = 60_000) => (
  <TransientHint visible={visible} label="Try the flash or move closer" onPress={onPress} visibleMs={visibleMs} />
);

describe('transient hint', () => {
  it('stays hidden until the condition holds', async () => {
    await renderWithProviders(hint(false));

    expect(screen.queryByText('Try the flash or move closer')).toBeNull();
  });

  it('shows when the condition first holds', async () => {
    await renderWithProviders(hint(true));

    expect(await screen.findByText('Try the flash or move closer')).toBeTruthy();
  });

  it('fades by itself after its moment', async () => {
    await renderWithProviders(hint(true, jest.fn(), 20));

    await waitFor(() => expect(screen.queryByText('Try the flash or move closer')).toBeNull());
  });

  it('dismisses on tap and runs its action', async () => {
    const onPress = jest.fn();
    await renderWithProviders(hint(true, onPress, 60_000));

    await fireEvent.press(await screen.findByText('Try the flash or move closer'));

    expect(onPress).toHaveBeenCalledTimes(1);
    expect(screen.queryByText('Try the flash or move closer')).toBeNull();
  });

  it('clears when the condition clears, and never shows again', async () => {
    const Harness = () => {
      const [visible, setVisible] = useState(true);
      return (
        <>
          <TransientHint visible={visible} label="Try the flash or move closer" onPress={() => undefined} visibleMs={60_000} />
          <Button label="Flip the condition" onPress={() => setVisible((v) => !v)} />
        </>
      );
    };
    await renderWithProviders(<Harness />);
    expect(await screen.findByText('Try the flash or move closer')).toBeTruthy();

    await fireEvent.press(screen.getByLabelText('Flip the condition'));
    expect(screen.queryByText('Try the flash or move closer')).toBeNull();

    await fireEvent.press(screen.getByLabelText('Flip the condition'));
    expect(screen.queryByText('Try the flash or move closer')).toBeNull();
  });
});
