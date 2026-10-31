// An answer that lands after the person has moved on belongs to the scan it was sent for: after the pipeline is stopped
// (a library switch, leaving the scanner) or the scan is discarded, nothing it brings back is drawn into the cards.
import { http } from 'msw';
import { waitFor } from '@testing-library/react-native';
import { CLOSE_CALL, identifyMock } from '@/mocks/identify';
import { scanMock } from '@/mocks/scan';
import { server } from '@/mocks/server';
import { useCardsStore } from '../cards/cardsStore';
import { picItemOf } from './payload';
import { SnapPipeline } from './SnapPipeline';
import { makeKit, readItem, readingOf, resetKit } from './testKit';

const dune = readItem('Dune', { box: { x: 0.05, y: 0.1, width: 0.2, height: 0.7 } });
const yellow = readItem(CLOSE_CALL.title, { kind: 'game', platform: 'Game Boy', box: { x: 0.55, y: 0.1, width: 0.2, height: 0.3 } });

const gate = () => {
  let release: () => void = () => undefined;
  const hold = new Promise<void>((resolve) => (release = resolve));
  return { hold, release };
};
const cards = () => Object.values(useCardsStore.getState().byId);

/** Every pipeline a test starts is stopped afterwards, so none keeps watching the cards of the next test. */
const started: SnapPipeline[] = [];
const startPipeline = (deps: ConstructorParameters<typeof SnapPipeline>[0]) => {
  const pipeline = new SnapPipeline(deps);
  started.push(pipeline);
  pipeline.start();
  return pipeline;
};

beforeEach(() => {
  scanMock.reset();
  identifyMock.reset();
  useCardsStore.getState().reset();
});
afterEach(() => {
  for (const pipeline of started.splice(0)) pipeline.stop();
  resetKit();
  scanMock.reset();
});

describe('an identify answer that lands late', () => {
  it('draws nothing when the pipeline was stopped meanwhile (a library switch)', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune]));
    const answer = gate();
    identifyMock.hold = answer.hold;
    const pipeline = startPipeline(kit.deps);

    const sending = pipeline.onSnap(await kit.snapEvent());
    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
    pipeline.stop(); // the person switched libraries
    answer.release();
    await sending;

    expect(cards()).toEqual([]);
  });

  it('still draws it for a pipeline that was stopped and started again (the same library)', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune]));
    const answer = gate();
    identifyMock.hold = answer.hold;
    const pipeline = startPipeline(kit.deps);

    const sending = pipeline.onSnap(await kit.snapEvent());
    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
    pipeline.start(); // an effect ran again; nothing changed for the person
    answer.release();
    await sending;

    await waitFor(() => expect(cards()).toHaveLength(1));
  });

  it('draws nothing when the scan was discarded meanwhile', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune]));
    const answer = gate();
    identifyMock.hold = answer.hold;
    const pipeline = new SnapPipeline(kit.deps);

    const sending = pipeline.onSnap(await kit.snapEvent());
    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
    kit.tray.getState().clear(); // Discard
    answer.release();
    await sending;

    expect(cards()).toEqual([]);
  });

  it('sends nothing more once it is stopped', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune]), readingOf([dune]));
    const answer = gate();
    identifyMock.hold = answer.hold;
    const pipeline = startPipeline(kit.deps);
    kit.store.getState().addSnap({
      snapId: 'later',
      libraryId: kit.deps.libraryId,
      trigger: 'shutter',
      createdAt: 5_000,
      thumbUri: 'file:///snaps/later.jpg',
      phase: 'read',
      sent: false,
      items: [picItemOf(dune)],
      unreadableCount: 0,
      engine: 'apple_model',
    });

    const sending = pipeline.onSnap(await kit.snapEvent());
    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
    pipeline.stop();
    answer.release();
    await sending;

    expect(identifyMock.requests).toHaveLength(1); // the waiting snap stays for the next time the scanner opens
  });
});

describe('a close-call answer that lands late', () => {
  const settle = async (kit: ReturnType<typeof makeKit>) => {
    kit.reader.script(readingOf([yellow]));
    kit.art.distances.set('0001', 0.1);
    kit.art.distances.set('0002', 0.8);
    const pipeline = startPipeline(kit.deps);
    await pipeline.onSnap(await kit.snapEvent());
    return pipeline;
  };
  const yellowCard = () => cards().find((c) => c.visibleText === CLOSE_CALL.title);

  it('does not send the distances, or update the card, once the pipeline was stopped during the comparison', async () => {
    const kit = makeKit();
    const answer = gate();
    const download = kit.art.deps.download;
    kit.art.deps.download = async (request) => {
      await answer.hold;
      return download(request);
    };
    const pipeline = await settle(kit);
    await waitFor(() => expect(yellowCard()?.resolution).toBe('ambiguous'));

    pipeline.stop();
    answer.release();
    await new Promise((resolve) => setTimeout(resolve, 100));

    expect(identifyMock.artRequests).toEqual([]);
    expect(yellowCard()?.resolution).toBe('ambiguous');
  });

  it('does not update the card when the pipeline was stopped while the distances were being scored', async () => {
    const kit = makeKit();
    const answer = gate();
    let scoring = false;
    server.use(
      http.post(/\/api\/v1\/scan-sessions\/([^/]+)\/candidates\/([^/]+):art$/, async () => {
        scoring = true;
        await answer.hold;
        return undefined; // the default handler answers, with the settled card
      }),
    );
    const pipeline = await settle(kit);
    await waitFor(() => expect(yellowCard()?.resolution).toBe('ambiguous'));
    await waitFor(() => expect(scoring).toBe(true)); // the comparison ran; the scoring call is on its way and held

    pipeline.stop();
    answer.release();
    await waitFor(() => expect(identifyMock.artRequests).toHaveLength(1));
    await new Promise((resolve) => setTimeout(resolve, 100));

    expect(yellowCard()?.resolution).toBe('ambiguous');
  });
});
