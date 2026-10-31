import { outcomeLines, scoringProblem } from './outcome';
import type { EvalState } from './runner';

const done = (patch: Partial<EvalState> = {}, skipped: { photoId: string; reason: string }[] = []): EvalState => ({
  phase: 'done',
  total: 19,
  photosDone: 19,
  current: 0,
  step: null,
  startedAt: null,
  endedAt: null,
  thermal: undefined,
  keepCool: false,
  partial: false,
  pending: 0,
  skipped: [],
  refused: [],
  stopped: false,
  summary: { skipped } as EvalState['summary'],
  ...patch,
});

describe('what the done screen says about a scored run', () => {
  it('says how many photos were scored, and nothing else, when every photo was', () => {
    expect(outcomeLines(done())).toEqual(['Your PC scored 19 photos.']);
  });

  it('does not say it scored all of them when some were refused: it says how many it scored, then what was left out', () => {
    expect(outcomeLines(done({ photosDone: 17, refused: ['a', 'b'] }))).toEqual([
      'Your PC scored 17 photos.',
      "Your PC wouldn't take 2 photos, so they are left out of the score.",
    ]);
    expect(outcomeLines(done({ photosDone: 18, refused: ['a'] }))[1]).toBe("Your PC wouldn't take 1 photo, so it is left out of the score.");
  });

  it('says "Stopped early" only when the owner stopped it', () => {
    expect(outcomeLines(done({ partial: true, stopped: true, photosDone: 5 }))).toEqual(['Stopped early. Your PC scored 5 photos.']);
    const other = outcomeLines(done({ partial: true, photosDone: 17, skipped: ['x', 'y'] })).join(' ');
    expect(other).not.toMatch(/Stopped early/);
  });

  it('says what was left out when a run ended partial for another reason', () => {
    expect(outcomeLines(done({ partial: true, photosDone: 15, skipped: ['x', 'y'], refused: ['z'] }))).toEqual([
      'Your PC scored 15 of 19 photos.',
      "Your PC wouldn't take 1 photo, so it is left out of the score.",
      "Skipped 2 photos that wouldn't download.",
      '1 photo was never read.',
    ]);
  });

  it('counts a photo the PC could not read or had no truth for as not scored, and says which could not be read', () => {
    const lines = outcomeLines(
      done({ partial: true, photosDone: 19 }, [
        { photoId: 'a', reason: 'grounding_failed' },
        { photoId: 'b', reason: 'no_truth' },
        { photoId: 'c', reason: 'trimmed_items_120_of_130' },
      ]),
    );
    expect(lines[0]).toBe('Your PC scored 17 of 19 photos.');
    expect(lines).toContain("Your PC couldn't read 1 photo it was sent, so it is left out of the score.");
  });

  it('still says something plain when the summary is not on the phone', () => {
    expect(outcomeLines(done({ summary: undefined }))).toEqual(["Your PC scored the run. Open the report on your PC; the summary isn't on this phone."]);
  });
});

describe('why the PC cannot score a run', () => {
  it('names a results folder that cannot be written, and that it keeps trying', () => {
    expect(scoringProblem('evals.results_not_writable')).toBe("Your PC couldn't score the run: its results folder can't be written. It keeps trying.");
  });

  it('says it plainly for any other reason', () => {
    expect(scoringProblem('evals.scoring_failed')).toBe("Your PC couldn't score the run. It keeps trying.");
  });
});
