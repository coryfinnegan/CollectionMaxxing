import { render, screen } from '@testing-library/react-native';
import { StyleSheet } from 'react-native';
import { lightColors } from '@/theme/tokens';
import { LoadingState } from './LoadingState';

describe('LoadingState', () => {
  it('shows the spinner with its label', async () => {
    await render(<LoadingState label="Loading your library…" />);
    expect(screen.getByTestId('loading-state')).toBeTruthy();
    expect(screen.getByText('Loading your library…')).toBeTruthy();
  });

  it('shows just the spinner without a label', async () => {
    await render(<LoadingState />);
    expect(screen.getByTestId('loading-state')).toBeTruthy();
    expect(screen.getByLabelText('Loading')).toBeTruthy();
  });

  it('announces status-like loading when asked', async () => {
    await render(<LoadingState label="Your export is waiting to start." alert />);
    expect(screen.getByRole('alert')).toBeTruthy();
  });

  it('sets the label in full ink for textured grounds when asked', async () => {
    await render(<LoadingState label="Loading your library…" labelTone="ink" />);
    expect(StyleSheet.flatten(screen.getByText('Loading your library…').props.style).color).toBe(lightColors.ink);
  });
});
