// The pipeline tells the reader what the framing said, so a cover shot reads as a cover (#504).
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { identifyMock } from '@/mocks/identify';
import { scanMock } from '@/mocks/scan';
import { useCardsStore } from '../cards/cardsStore';
import { SnapPipeline } from './SnapPipeline';
import { makeKit, readItem, readingOf, resetKit } from './testKit';

beforeEach(() => {
  scanMock.reset();
  identifyMock.reset();
  useCardsStore.getState().reset();
});
afterEach(() => {
  resetKit();
  scanMock.reset();
});

describe('routing a snap to its reader', () => {
  it('hands a cover shot to the reader as a cover', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([readItem('NIGELLA BITES')]));
    await new SnapPipeline(kit.deps).onSnap(await kit.snapEvent('shutter', 'single_cover'));
    expect(kit.reader.options).toEqual([{ shotKind: 'single_cover' }]);
  });

  it('hands a shelf shot to the reader as a shelf', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([readItem('Dune'), readItem('Emma')]));
    await new SnapPipeline(kit.deps).onSnap(await kit.snapEvent('shutter', 'shelf_segment'));
    expect(kit.reader.options).toEqual([{ shotKind: 'shelf_segment' }]);
    expect(kit.store.getState().snaps[0]).toMatchObject({ libraryId: MOCK_LIBRARY_ID });
  });
});
