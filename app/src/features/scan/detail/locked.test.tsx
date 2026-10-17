// Locked acceptance tests for CAM-3c (issue #119). Never weaken, skip or delete these.
import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { photoMock } from '@/mocks/scan-photo';
import { scanMock } from '@/mocks/scan';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { ResultDetail } from '../cards/ResultDetail';
import { uploadCover } from './coverPhoto';
import { CROP_BYTES, resetDetail, seedSnap, TWO } from './testKit';

jest.mock('expo-router', () => ({ useRouter: () => ({ push: jest.fn(), back: jest.fn() }) }));

beforeEach(() => resetDetail());

const photoRequests: string[] = [];
const watch = ({ request }: { request: Request }) => {
  if (request.method !== 'GET' && /\/photo$/.test(new URL(request.url).pathname)) photoRequests.push(request.url);
};
beforeEach(() => {
  photoRequests.length = 0;
  server.events.on('request:start', watch);
});
afterEach(() => server.events.removeListener('request:start', watch));

describe('Photos stay on the phone unless the owner taps "Use my photo"', () => {
  it('opening, swiping, highlighting and Fix it make no upload; the only upload is the cover action, for one crop', async () => {
    const { cards } = await seedSnap(TWO);
    await renderWithProviders(<ResultDetail id={cards[0]!.id} />);

    expect(await screen.findByLabelText('Item 1 of 2 highlighted')).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Next item'));
    expect(await screen.findByLabelText('Item 2 of 2 highlighted')).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Previous item'));
    expect(await screen.findByLabelText('Item 1 of 2 highlighted')).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Wrong item? Fix it'));
    expect(await screen.findByText('Fix it')).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Close Fix it'));

    expect(photoRequests).toEqual([]);
    expect(photoMock.uploads).toEqual([]);

    await fireEvent.press(screen.getByLabelText('Use my photo'));
    await waitFor(() => expect(photoMock.uploads).toHaveLength(1));
    expect(photoRequests).toHaveLength(1);
    expect(photoMock.uploads[0]).toMatchObject({ candidateId: cards[0]!.id, size: CROP_BYTES, parts: 1 });
    expect(await screen.findByText('Your photo is the cover')).toBeTruthy();
  });
});

describe('The cover upload is offered to editors only', () => {
  it('shows a viewer no button, and the call is blocked', async () => {
    server.use(http.get('*/api/v1/me', () => HttpResponse.json({ id: 'u', memberships: [{ libraryId: MOCK_LIBRARY_ID, role: 'viewer' }] })));
    const { cards, sessionId } = await seedSnap(TWO);
    scanMock.role = 'viewer';
    await renderWithProviders(<ResultDetail id={cards[0]!.id} />);

    expect(await screen.findByLabelText('Item 1 of 2 highlighted')).toBeTruthy();
    expect(screen.queryByLabelText('Use my photo')).toBeNull();

    const result = await uploadCover({ canEdit: false, sessionId, candidateId: cards[0]!.id, cropUri: 'file:///snaps/snap-1-0.jpg' });
    expect(result).toEqual({ ok: false, reason: 'blocked' });
    expect(photoRequests).toEqual([]);
    expect(photoMock.uploads).toEqual([]);
  });
});
