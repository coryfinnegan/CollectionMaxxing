import { fireEvent, screen, waitFor, within } from '@testing-library/react-native';
import { photoMock } from '@/mocks/scan-photo';
import { scanMock } from '@/mocks/scan';
import { renderWithProviders } from '@/test/render';
import { useCardsStore } from '../cards/cardsStore';
import { ResultDetail } from '../cards/ResultDetail';
import { DEV_CODES } from '../scanner/devCodes';
import { CROP_BYTES, BOX_A, BOX_B, resetDetail, seedSnap, TWO } from './testKit';
import { PHOTO_GONE } from './PhotoViewer';
import { server } from '@/mocks/server';
import { setCropReader, TOO_BIG } from './coverPhoto';

jest.mock('expo-router', () => ({ useRouter: () => ({ push: jest.fn(), back: jest.fn() }) }));

beforeEach(() => resetDetail());
afterEach(() => resetDetail());

const boxStyle = () => {
  const style = screen.getByTestId('item-box').props.style as { left: number; top: number; width: number; height: number };
  return { left: style.left, top: style.top, width: style.width, height: style.height };
};

describe('the highlighted photo', () => {
  it('shows your photo with the item boxed, and the box moves with the pager', async () => {
    const { cards } = await seedSnap(TWO);
    await renderWithProviders(<ResultDetail id={cards[0]!.id} />);

    expect(await screen.findByLabelText('Item 1 of 2 highlighted')).toBeTruthy();
    expect(screen.getByText('1 of 2 in this photo')).toBeTruthy();
    expect(screen.getByText('Super Mario 64')).toBeTruthy();
    const first = boxStyle();

    await fireEvent.press(screen.getByLabelText('Next item'));
    expect(await screen.findByLabelText('Item 2 of 2 highlighted')).toBeTruthy();
    expect(screen.getByText('2 of 2 in this photo')).toBeTruthy();
    expect(screen.getByText('GoldenEye 007')).toBeTruthy();
    expect(boxStyle()).not.toEqual(first);
    expect(BOX_A).not.toEqual(BOX_B);
  });

  it('hides the counter and the pager for a picture with one item', async () => {
    const { cards } = await seedSnap([TWO[0]!]);
    await renderWithProviders(<ResultDetail id={cards[0]!.id} />);

    expect(await screen.findByLabelText('Item highlighted')).toBeTruthy();
    expect(screen.queryByText(/in this photo/)).toBeNull();
    expect(screen.queryByLabelText('Next item')).toBeNull();
    expect(screen.queryByLabelText('Previous item')).toBeNull();
  });

  it('keeps a card from another picture out of the pager', async () => {
    const { cards, sessionId } = await seedSnap(TWO);
    const res = await fetch(`http://api.test/api/v1/scan-sessions/${sessionId}/barcodes`, {
      method: 'POST',
      body: JSON.stringify({ codes: [{ value: DEV_CODES.ghostbustersNew, symbology: 'ean-13', copies: 1 }] }),
    });
    const { candidates } = (await res.json()) as { candidates: { id: string }[] };
    useCardsStore.getState().merge([{ ...candidates[0]!, localId: 'local-other' } as never], { announce: false });

    await renderWithProviders(<ResultDetail id={cards[1]!.id} />);
    expect(await screen.findByText('2 of 2 in this photo')).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Next item'));
    expect(await screen.findByText('1 of 2 in this photo')).toBeTruthy();
    expect(screen.queryByText('Ghostbusters')).toBeNull();
  });

  it('draws no box for an item without one', async () => {
    const { cards } = await seedSnap([{ ...TWO[0]!, box: undefined }]);
    await renderWithProviders(<ResultDetail id={cards[0]!.id} />);

    expect(await screen.findByLabelText('Your photo')).toBeTruthy();
    expect(screen.queryByTestId('item-box')).toBeNull();
  });

  it('falls back to the card with friendly words when the photo was cleared from the phone', async () => {
    const { cards } = await seedSnap(TWO, { still: false });
    await renderWithProviders(<ResultDetail id={cards[0]!.id} />);

    expect(await screen.findByText(PHOTO_GONE)).toBeTruthy();
    expect(screen.getByText('Super Mario 64')).toBeTruthy();
    expect(screen.queryByLabelText('Your photo')).toBeNull();
  });

  it('falls back the same way when the picture file cannot be opened', async () => {
    const { cards } = await seedSnap(TWO);
    await renderWithProviders(<ResultDetail id={cards[0]!.id} />);

    await fireEvent(await screen.findByLabelText('Your photo'), 'error');
    expect(await screen.findByText(PHOTO_GONE)).toBeTruthy();
    expect(screen.getByText('Super Mario 64')).toBeTruthy();
  });

  it('shows no photo area for a card that was not read from a picture', async () => {
    const { sessionId } = await seedSnap(TWO);
    const res = await fetch(`http://api.test/api/v1/scan-sessions/${sessionId}/barcodes`, {
      method: 'POST',
      body: JSON.stringify({ codes: [{ value: DEV_CODES.ghostbustersNew, symbology: 'ean-13', copies: 1 }] }),
    });
    const { candidates } = (await res.json()) as { candidates: { id: string }[] };
    useCardsStore.getState().merge(candidates as never, { announce: false });
    await renderWithProviders(<ResultDetail id={candidates[0]!.id} />);

    expect(await screen.findByText('Ghostbusters')).toBeTruthy();
    expect(screen.queryByLabelText('Your photo')).toBeNull();
    expect(screen.queryByText(PHOTO_GONE)).toBeNull();
    expect(screen.queryByLabelText('Use my photo')).toBeNull();
  });
});

describe('the bottom section', () => {
  it('leads an owned result with where it lives, and offers no add buttons', async () => {
    const { cards } = await seedSnap(TWO);
    await renderWithProviders(<ResultDetail id={cards[1]!.id} />);

    expect(await screen.findByText('You own this: Game room › Shelf 1')).toBeTruthy();
    expect(screen.queryByLabelText('Add to library')).toBeNull();
  });

  it('offers the add buttons for a result that is not owned', async () => {
    const { cards } = await seedSnap(TWO);
    await renderWithProviders(<ResultDetail id={cards[0]!.id} />);

    expect(await screen.findByLabelText('Add to library')).toBeTruthy();
    expect(screen.getByLabelText('Wishlist')).toBeTruthy();
  });
});

describe('Wrong item? Fix it', () => {
  it('shows the alternatives and a search filled with what was read, and choosing one sends the existing PATCH', async () => {
    const { cards } = await seedSnap(TWO);
    const alternative = scanMock.catalogue[DEV_CODES.ghostbustersNew]!.release;
    const session = [...scanMock.sessions.values()][0]!;
    session.candidates[0]!.alternatives = [alternative] as never;
    useCardsStore.getState().merge([{ ...cards[0]!, visibleText: 'SUPER MARIO', alternatives: [alternative] as never }], { announce: false });
    const patches: unknown[] = [];
    server.events.on('request:start', async ({ request }) => {
      if (request.method === 'PATCH') patches.push(await request.clone().json());
    });

    await renderWithProviders(<ResultDetail id={cards[0]!.id} />);
    await fireEvent.press(await screen.findByLabelText('Wrong item? Fix it'));

    expect(await screen.findByText('Pick one')).toBeTruthy();
    expect(screen.getByDisplayValue('SUPER MARIO')).toBeTruthy();
    await fireEvent.press(screen.getByLabelText(/^Ghostbusters/));

    await waitFor(() => expect(patches).toEqual([{ releaseId: alternative.id }]));
    expect(await screen.findByText('Ghostbusters')).toBeTruthy();
    expect(photoMock.uploads).toEqual([]);
    server.events.removeAllListeners();
  });

  it('searches again from the filled-in text', async () => {
    const { cards } = await seedSnap(TWO);
    useCardsStore.getState().merge([{ ...cards[0]!, visibleText: 'ghostbusters' }], { announce: false });
    await renderWithProviders(<ResultDetail id={cards[0]!.id} />);
    await fireEvent.press(await screen.findByLabelText('Wrong item? Fix it'));
    await fireEvent.press(await screen.findByLabelText('Search'));

    expect(await screen.findByText('Ghostbusters')).toBeTruthy();
  });
});

describe('Use my photo', () => {
  it('sends exactly one request with the crop of that item and not the full photo, then shows it as the cover choice', async () => {
    const { cards } = await seedSnap(TWO);
    await renderWithProviders(<ResultDetail id={cards[0]!.id} />);

    await fireEvent.press(await screen.findByLabelText('Use my photo'));

    await waitFor(() => expect(photoMock.uploads).toHaveLength(1));
    expect(photoMock.uploads[0]).toMatchObject({ candidateId: cards[0]!.id, contentType: 'image/jpeg', size: CROP_BYTES, parts: 1 });
    expect(await screen.findByText('Your photo is the cover')).toBeTruthy();
    expect(screen.queryByLabelText('Use my photo')).toBeNull();
    expect(within(screen.getByTestId('cover-choice')).getByLabelText('Your photo as the cover')).toBeTruthy();
  });

  it('sends the crop of the item on screen after swiping to another one', async () => {
    const { cards } = await seedSnap(TWO);
    await renderWithProviders(<ResultDetail id={cards[0]!.id} />);

    await fireEvent.press(await screen.findByLabelText('Next item'));
    await fireEvent.press(await screen.findByLabelText('Use my photo'));
    await waitFor(() => expect(photoMock.uploads).toHaveLength(1));
    expect(photoMock.uploads[0]!.candidateId).toBe(cards[1]!.id);
  });

  it('says so in friendly words when the upload fails, keeps the button, and sends nothing twice', async () => {
    const { cards } = await seedSnap(TWO);
    photoMock.failNext = 500;
    await renderWithProviders(<ResultDetail id={cards[0]!.id} />);

    await fireEvent.press(await screen.findByLabelText('Use my photo'));
    expect(await screen.findByText(/Your photo is still on your phone/)).toBeTruthy();
    expect(screen.getByLabelText('Use my photo')).toBeTruthy();
    expect(photoMock.uploads).toHaveLength(1);
  });

  it('does not send a crop that is over the server limit', async () => {
    const { cards } = await seedSnap(TWO);
    setCropReader(async () => new Blob([new Uint8Array(4 * 1024 * 1024 + 1)], { type: 'image/jpeg' }));
    await renderWithProviders(<ResultDetail id={cards[0]!.id} />);

    await fireEvent.press(await screen.findByLabelText('Use my photo'));
    expect(await screen.findByText(TOO_BIG)).toBeTruthy();
    expect(photoMock.uploads).toEqual([]);
  });

  it('is not offered when the phone has no crop of the item', async () => {
    const { cards } = await seedSnap([{ ...TWO[0]!, withCrop: false }, TWO[1]!]);
    await renderWithProviders(<ResultDetail id={cards[0]!.id} />);

    expect(await screen.findByLabelText('Item 1 of 2 highlighted')).toBeTruthy();
    expect(screen.queryByLabelText('Use my photo')).toBeNull();
  });
});
