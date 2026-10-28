// The shared toast (#465): the bar draws on the theme surface with ink text, so
// the message and the labeled action stay visible in both schemes.
import { fireEvent, screen } from '@testing-library/react-native';
import { StyleSheet } from 'react-native';
import { renderWithProviders } from '@/test/render';
import { lightColors } from '@/theme/tokens';
import { Snackbar } from './Snackbar';

describe('Snackbar', () => {
  it('shows the message with a labeled action that runs on press', async () => {
    const onAction = jest.fn();
    await renderWithProviders(<Snackbar message="Deleted Dune" actionLabel="Undo" onAction={onAction} />);

    expect(screen.getByText('Deleted Dune')).toBeTruthy();
    await fireEvent.press(screen.getByRole('button', { name: 'Undo' }));
    expect(onAction).toHaveBeenCalled();
  });

  it('renders no action when none is given', async () => {
    await renderWithProviders(<Snackbar message="That didn't work. Try again." />);

    expect(screen.queryByRole('button')).toBeNull();
  });

  it('draws the bar on the theme surface with ink text, so the action label stays visible', async () => {
    await renderWithProviders(<Snackbar message="Deleted Dune" actionLabel="Undo" onAction={() => undefined} />);

    const bar = StyleSheet.flatten(screen.getByText('Deleted Dune').parent!.props.style);
    const message = StyleSheet.flatten(screen.getByText('Deleted Dune').props.style);
    const label = StyleSheet.flatten(screen.getByText('Undo').props.style);
    expect(bar.backgroundColor).toBe(lightColors.surface);
    expect(message.color).toBe(lightColors.ink);
    expect(label.color).toBe(lightColors.ink);
    expect(label.color).not.toBe(bar.backgroundColor);
  });

  it('gives the action a 44pt target', async () => {
    await renderWithProviders(<Snackbar message="Deleted Dune" actionLabel="Undo" onAction={() => undefined} />);

    const button = screen.getByRole('button', { name: 'Undo' });
    const raw = typeof button.props.style === 'function' ? button.props.style({ pressed: false }) : button.props.style;
    expect(StyleSheet.flatten(raw).minHeight).toBeGreaterThanOrEqual(44);
  });
});
