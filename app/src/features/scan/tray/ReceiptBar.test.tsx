// The scanner count bar (#306): a frosted dock over the camera on iOS 26+, the surface bar elsewhere.
import { render, screen } from '@testing-library/react-native';
import { ReceiptBar } from './ReceiptBar';

const mockGlassState = { available: false };

jest.mock('expo-glass-effect', () => {
  const React = jest.requireActual<typeof import('react')>('react');
  const { View } = jest.requireActual<typeof import('react-native')>('react-native');
  return {
    GlassView: (props: object) => React.createElement(View, { ...props, accessibilityLabel: 'liquid glass' }),
    isLiquidGlassAvailable: () => mockGlassState.available,
  };
});

const noop = () => undefined;

const bar = (available: boolean) => {
  mockGlassState.available = available;
  return render(<ReceiptBar lines={[]} cards={[]} online items={3} processing={1} problem={null} notice={null} onPlusOne={noop} onRemove={noop} onReview={noop} />);
};

describe('ReceiptBar glass', () => {
  it('keeps the count bar a plain surface bar without liquid glass', async () => {
    await bar(false);

    expect(screen.getByRole('button', { name: /Show the receipt/ })).toBeTruthy();
    expect(screen.queryByLabelText('liquid glass')).toBeNull();
  });

  it('floats the count bar in liquid glass on iOS 26+', async () => {
    await bar(true);

    expect(screen.getByLabelText('liquid glass')).toBeTruthy();
    expect(screen.getByRole('button', { name: /Show the receipt/ })).toBeTruthy();
  });
});
