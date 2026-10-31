// What the done screen says about a run the PC has scored, in plain words: how many photos were scored, and what was left out and why.
import type { EvalState } from './runner';

const photos = (count: number) => `${count} ${count === 1 ? 'photo' : 'photos'}`;

/** The reasons the PC lists a photo as skipped when the photo itself was not scored (the others, such as a trimmed list, say what was cut from a photo that was). */
const NOT_SCORED = new Set(['no_truth', 'invalid_truth', 'unknown_photo', 'grounding_failed']);

/** What the PC says when it could not score a run at all: the plain reason, and that it keeps trying. */
export function scoringProblem(code: string | undefined): string {
  return code === 'evals.results_not_writable'
    ? "Your PC couldn't score the run: its results folder can't be written. It keeps trying."
    : "Your PC couldn't score the run. It keeps trying.";
}

/**
 * The lines under a finished run, the first being the headline. It says "Stopped early" only when the owner tapped Stop; a run that ended partial for
 * any other reason says how many photos were scored out of how many, and what was left out.
 */
export function outcomeLines(state: EvalState): string[] {
  const { summary } = state;
  if (!summary) return ["Your PC scored the run. Open the report on your PC; the summary isn't on this phone."];

  const notScored = new Set(summary.skipped.filter((s) => NOT_SCORED.has(s.reason)).map((s) => s.photoId));
  const scored = Math.max(0, state.photosDone - notScored.size);
  const lines = [
    state.stopped
      ? `Stopped early. Your PC scored ${photos(scored)}.`
      : state.partial
        ? `Your PC scored ${scored} of ${photos(state.total)}.`
        : `Your PC scored ${photos(scored)}.`,
  ];

  if (state.refused.length > 0) {
    lines.push(`Your PC wouldn't take ${photos(state.refused.length)}, so ${state.refused.length === 1 ? 'it is' : 'they are'} left out of the score.`);
  }
  if (state.skipped.length > 0) {
    lines.push(`Skipped ${photos(state.skipped.length)} that wouldn't download.`);
  }
  const unreadable = summary.skipped.filter((s) => s.reason === 'grounding_failed').length;
  if (unreadable > 0) {
    lines.push(`Your PC couldn't read ${photos(unreadable)} it was sent, so ${unreadable === 1 ? 'it is' : 'they are'} left out of the score.`);
  }
  const neverRead = state.total - state.photosDone - state.refused.length - state.skipped.length;
  if (state.partial && !state.stopped && neverRead > 0) {
    lines.push(`${photos(neverRead)} ${neverRead === 1 ? 'was' : 'were'} never read.`);
  }
  return lines;
}
