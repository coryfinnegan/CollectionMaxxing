import { closeStep, initialClose } from './closeMachine';

describe('close scanner machine', () => {
  it('closes straight away when nothing was scanned', () => {
    expect(closeStep(initialClose, { type: 'close', unreviewed: 0 })).toEqual({ state: initialClose, effect: 'leave' });
  });

  it('asks when there are unreviewed items', () => {
    expect(closeStep(initialClose, { type: 'close', unreviewed: 3 })).toEqual({ state: { phase: 'asking' }, effect: undefined });
  });

  it('routes the three choices', () => {
    const asking = { phase: 'asking' as const };
    expect(closeStep(asking, { type: 'choose', choice: 'review' }).effect).toBe('review');
    expect(closeStep(asking, { type: 'choose', choice: 'keep' }).effect).toBe('leave');
    expect(closeStep(asking, { type: 'choose', choice: 'discard' })).toEqual({ state: { phase: 'discarding' }, effect: 'discard' });
  });

  it('leaves once a discard finishes, and offers another try when it fails', () => {
    expect(closeStep({ phase: 'discarding' }, { type: 'discarded' }).effect).toBe('leave');
    const failed = closeStep({ phase: 'discarding' }, { type: 'discardFailed', message: 'Try again' });
    expect(failed.state).toEqual({ phase: 'failed', error: 'Try again' });
    expect(closeStep(failed.state, { type: 'choose', choice: 'discard' }).effect).toBe('discard');
  });

  it('cannot be dismissed mid-discard, but can be before', () => {
    expect(closeStep({ phase: 'discarding' }, { type: 'dismiss' }).state.phase).toBe('discarding');
    expect(closeStep({ phase: 'asking' }, { type: 'dismiss' }).state).toEqual(initialClose);
  });
});
