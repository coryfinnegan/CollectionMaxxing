// Live announcements: what VoiceOver says, unprompted, during a scan. The announce helper is mocked, so these check
// what is said and when, not how iOS speaks it.
import { QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react-native';
import type { ReactNode } from 'react';
import type { CandidateDto } from '@/api/generated/models';
import { announceForScreenReader } from '@/lib/a11y/announce';
import { makeQueryClient } from '@/test/render';
import { Snackbar } from '@/ui';
import { NiceScanFlash } from './autosnap/NiceScanFlash';
import { ResultCard, type CardActions } from './cards/ResultCards';
import { ReceiptBar } from './tray/ReceiptBar';

jest.mock('@/lib/a11y/announce', () => ({ announceForScreenReader: jest.fn() }));
const spoken = announceForScreenReader as jest.Mock;

beforeEach(() => spoken.mockClear());

const client = makeQueryClient();
const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;

const actions: CardActions = { canAdd: true, onOpen: () => undefined, onAdd: () => undefined, onWishlist: () => undefined };
const candidate = (patch: object): CandidateDto =>
  ({
    id: 'c1',
    seq: 1,
    rawCode: '9780441172719',
    resolution: 'pending',
    alreadyOwnedCount: 0,
    ownedAt: [],
    decision: 'undecided',
    release: null,
    ...patch,
  }) as unknown as CandidateDto;
const dune = { release: { id: 'r1', title: 'Dune', format: 'paperback', year: 1990, platform: null, creators: [], coverUrl: null } };

describe('scan announcements', () => {
  it('says "Nice scan!" when the flash appears, and only once while it stays', async () => {
    const view = await render(<NiceScanFlash visible={false} reduceMotion={false} />);
    expect(spoken).not.toHaveBeenCalled();
    await view.rerender(<NiceScanFlash visible reduceMotion={false} />);
    await view.rerender(<NiceScanFlash visible reduceMotion />);
    expect(spoken.mock.calls).toEqual([['Nice scan!']]);
  });

  it('announces a new result card by what it is', async () => {
    await render(<ResultCard candidate={candidate({ resolution: 'matched', ...dune })} actions={actions} reduceMotion={false} />, { wrapper });
    expect(spoken).toHaveBeenCalledWith('New result: Dune');
  });

  it('announces a card again when it settles, with the answer', async () => {
    const view = await render(<ResultCard candidate={candidate({ resolution: 'matched', alreadyOwnedCount: 0, ...dune })} actions={actions} reduceMotion={false} />, { wrapper });
    spoken.mockClear();
    await view.rerender(<ResultCard candidate={candidate({ resolution: 'matched', alreadyOwnedCount: 2, ...dune })} actions={actions} reduceMotion={false} />);
    expect(spoken).toHaveBeenCalledWith('Dune. Already own — Paperback · 2 copies');
  });

  it('announces Queued when a scan waits on the phone while offline', async () => {
    const props = { items: 1, processing: 1, problem: null, notice: null, onReview: () => undefined };
    const view = await render(<ReceiptBar {...props} queued={0} online={false} />);
    expect(spoken).not.toHaveBeenCalled();
    await view.rerender(<ReceiptBar {...props} queued={1} online={false} />);
    expect(spoken).toHaveBeenCalledWith('Queued. 1 item waiting for your server.');
    spoken.mockClear();
    await view.rerender(<ReceiptBar {...props} queued={2} online={false} />);
    expect(spoken).toHaveBeenCalledWith('Queued. 2 items waiting for your server.');
    spoken.mockClear();
    await view.rerender(<ReceiptBar {...props} queued={2} online />);
    expect(spoken).not.toHaveBeenCalled();
  });

  it('announces a toast such as "Added 3 items" when it appears or changes', async () => {
    const view = await render(<Snackbar message="Added 3 items" actionLabel="Undo" onAction={() => undefined} />);
    expect(spoken).toHaveBeenCalledWith('Added 3 items');
    await view.rerender(<Snackbar message="Undone. What you scanned is back in Review." />);
    expect(spoken).toHaveBeenLastCalledWith('Undone. What you scanned is back in Review.');
  });
});
