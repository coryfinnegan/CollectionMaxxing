// The strip shows every snap in shoot order with the right state, from shutter to Review.
import type { CandidateDto } from '@/api/generated/models';
import { candidate } from '@/features/review/testData';
import type { PicItem, PicSnap } from '../pipeline/pipelineStore';
import { cardStateLabel, inShootOrder, segmentDot, segmentLabel, segmentStateOf } from './segmentStates';

const item = (localId: string, extra: Partial<PicItem> = {}): PicItem => ({
  localId,
  text: localId,
  title: localId,
  payload: { localId } as PicItem['payload'],
  ...extra,
});

const snap = (snapId: string, extra: Partial<PicSnap> = {}): PicSnap => ({
  snapId,
  libraryId: 'library-1',
  trigger: 'shutter',
  createdAt: 1,
  thumbUri: `file:///snaps/${snapId}-still.jpg`,
  phase: 'reading',
  sent: false,
  items: [],
  unreadableCount: 0,
  ...extra,
});

describe('segment states', () => {
  it('is captured from the shutter until the first item is read', () => {
    expect(segmentStateOf(snap('s1'), [], true)).toBe('captured');
  });

  it('is reading while items stream in', () => {
    expect(segmentStateOf(snap('s1', { items: [item('a')] }), [], true)).toBe('reading');
  });

  it('is identifying once read and waiting to send', () => {
    expect(segmentStateOf(snap('s1', { phase: 'read', items: [item('a')] }), [], true)).toBe('identifying');
  });

  it('is queued offline while read and unsent', () => {
    expect(segmentStateOf(snap('s1', { phase: 'read', items: [item('a')] }), [], false)).toBe('offline_queued');
  });

  it('stays identifying until every item has the server answer', () => {
    const sent = snap('s1', { phase: 'read', sent: true, sessionId: 'session', items: [item('a', { candidateId: 'c1' }), item('b')] });
    const answered = candidate({ id: 'c1', localId: 'a', resolution: 'matched' });

    expect(segmentStateOf(sent, [answered], true)).toBe('identifying');
  });

  it('treats the phone’s own local card as still waiting', () => {
    const sent = snap('s1', { phase: 'read', sent: true, sessionId: 'session', items: [item('a')] });
    const local = { ...candidate({ localId: 'a', resolution: 'matched' }), id: 'local:s1:a' } as CandidateDto;

    expect(segmentStateOf(sent, [local], true)).toBe('identifying');
  });

  it('is queued offline while sent but unanswered', () => {
    const sent = snap('s1', { phase: 'read', sent: true, sessionId: 'session', items: [item('a', { candidateId: 'c1' })] });
    const pending = candidate({ id: 'c1', localId: 'a', resolution: 'pending' });

    expect(segmentStateOf(sent, [pending], false)).toBe('offline_queued');
  });

  it('is resolved when everything matched and nothing needs a decision', () => {
    const sent = snap('s1', { phase: 'read', sent: true, sessionId: 'session', items: [item('a', { candidateId: 'c1' })] });
    const matched = candidate({ id: 'c1', localId: 'a', resolution: 'matched', decision: 'undecided' });

    expect(segmentStateOf(sent, [matched], true)).toBe('resolved');
  });

  it.each(['ambiguous', 'not_found', 'error'] as const)('needs eyes when an answer is %s', (resolution) => {
    const sent = snap('s1', { phase: 'read', sent: true, sessionId: 'session', items: [item('a', { candidateId: 'c1' })] });
    const unsure = candidate({ id: 'c1', localId: 'a', resolution, decision: 'undecided' });

    expect(segmentStateOf(sent, [unsure], true)).toBe('needs_eye');
  });

  it('is reviewed once every item is decided or added', () => {
    const sent = snap('s1', {
      phase: 'read',
      sent: true,
      sessionId: 'session',
      items: [item('a', { candidateId: 'c1' }), item('b', { candidateId: 'c2' })],
    });
    const cards = [
      candidate({ id: 'c1', localId: 'a', resolution: 'matched', decision: 'accepted', createdItemId: 'item-1' }),
      candidate({ id: 'c2', localId: 'b', resolution: 'not_found', decision: 'rejected' }),
    ];

    expect(segmentStateOf(sent, cards, true)).toBe('reviewed');
  });

  it('lists snaps oldest first', () => {
    const snaps = [snap('new', { createdAt: 3 }), snap('old', { createdAt: 1 }), snap('mid', { createdAt: 2 })];

    expect(inShootOrder(snaps).map((s) => s.snapId)).toEqual(['old', 'mid', 'new']);
  });

  it('names one card’s state in sentence case', () => {
    expect(cardStateLabel(candidate({ resolution: 'matched', alreadyOwnedCount: 0 }))).toBe('Resolved');
    expect(cardStateLabel(candidate({ resolution: 'matched', alreadyOwnedCount: 1 }))).toBe('Owned');
    expect(cardStateLabel(candidate({ resolution: 'pending' }))).toBe('Identifying');
    expect(cardStateLabel(candidate({ resolution: 'ambiguous' }))).toBe('Needs a look');
    expect(cardStateLabel(candidate({ resolution: 'matched', decision: 'rejected' }))).toBe('Skipped');
    expect(cardStateLabel(candidate({ resolution: 'matched', decision: 'accepted', createdItemId: 'item-1' }))).toBe('Added');
  });

  it('names every state in sentence case with a theme dot', () => {
    expect(segmentLabel('needs_eye')).toBe('Needs a look');
    expect(segmentLabel('offline_queued')).toBe('Queued');
    expect(segmentDot('resolved')).toEqual({ fill: 'accent', hollow: false });
    expect(segmentDot('reviewed')).toEqual({ fill: 'accent', hollow: true });
    expect(segmentDot('reading')).toEqual(segmentDot('identifying'));
  });
});
