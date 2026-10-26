// Buttons keep their 44pt target (#269, #356): every labelled close and back
// control in the app is a Button, so the target is asserted once, here.
import { fireEvent, screen } from '@testing-library/react-native';
import { StyleSheet } from 'react-native';
import { renderWithProviders } from '@/test/render';
import { Button } from './Button';

describe('Button', () => {
  it('is spoken with its label', async () => {
    await renderWithProviders(<Button label="Close Format" variant="secondary" onPress={() => undefined} />);

    expect(screen.getByRole('button', { name: 'Close Format' })).toBeTruthy();
  });

  it('keeps a 44pt target', async () => {
    await renderWithProviders(<Button label="Close Format" variant="secondary" onPress={() => undefined} />);

    const style = screen.getByRole('button', { name: 'Close Format' }).props.style;
    const flat = StyleSheet.flatten(typeof style === 'function' ? style({ pressed: false }) : style);
    expect(flat.minHeight).toBeGreaterThanOrEqual(44);
  });

  it('presses when tapped', async () => {
    const onPress = jest.fn();
    await renderWithProviders(<Button label="Close Format" variant="secondary" onPress={onPress} />);

    await fireEvent.press(screen.getByRole('button', { name: 'Close Format' }));

    expect(onPress).toHaveBeenCalled();
  });
});
