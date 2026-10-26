// The first-run coach: what it says, and one tap dismisses it for good.
import { fireEvent, screen } from '@testing-library/react-native';
import { renderWithProviders } from '@/test/render';
import { ScanCoach } from './ScanCoach';

describe('scan coach', () => {
  it('dismisses with one tap', async () => {
    const onDone = jest.fn();
    await renderWithProviders(<ScanCoach onDone={onDone} />);

    expect(await screen.findByText('Scan to add')).toBeTruthy();
    fireEvent.press(screen.getByLabelText('Start scanning'));
    expect(onDone).toHaveBeenCalledTimes(1);
  });
});
