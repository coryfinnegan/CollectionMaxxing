// The scanner count bar (#306): a frosted dock over the camera on iOS 26+, the surface bar elsewhere.
import { fireEvent, render, screen } from '@testing-library/react-native';
import type { CandidateDto } from '@/api/generated/models';
import { ReceiptBar } from './ReceiptBar';
import type { TrayLine } from './tray';

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

describe('ReceiptBar auto-add', () => {
  const line: TrayLine = { code: '9780441172719', symbology: 'ean-13', copies: 1, sentCopies: 1, addedAt: 1, lastSeenAt: 1 };
  const card = (createdItemId: string | null) =>
    ({ id: 'c1', rawCode: '9780441172719', resolution: 'matched', createdItemId, release: { title: 'Dune' } }) as unknown as CandidateDto;

  const openReceipt = async (cards: CandidateDto[]) => {
    mockGlassState.available = false;
    await render(
      <ReceiptBar lines={[line]} cards={cards} online items={1} processing={0} problem={null} notice={null} onPlusOne={noop} onRemove={noop} onReview={noop} />,
    );
    fireEvent.press(screen.getByLabelText('1 item. Show the receipt'));
  };

  it('marks an auto-added line ADDED', async () => {
    await openReceipt([card('item-1')]);

    expect(await screen.findByText('ADDED')).toBeTruthy();
    expect(screen.getByLabelText('Dune, 1 copy, added')).toBeTruthy();
  });

  it('leaves a resolved-but-not-added line unmarked', async () => {
    await openReceipt([card(null)]);

    expect(await screen.findByText('Dune')).toBeTruthy();
    expect(screen.queryByText('ADDED')).toBeNull();
    expect(screen.getByLabelText('Dune, 1 copy')).toBeTruthy();
  });
});
