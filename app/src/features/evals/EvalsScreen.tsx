// Owner-only dev screen: run the golden photos through all three engines on this iPhone and see the PC's gate table when it is done.
// The run lives on the PC. Each photo goes up as soon as it is read, so the owner can leave the screen or the app and pick up where it stopped.
import { useEffect, useState, useSyncExternalStore } from 'react';
import { SafeAreaView } from 'react-native-safe-area-context';
import { useColors } from '@/theme/tokens';
import { Button, Card, LoadingState, Screen, Text, TextField } from '@/ui';
import { AddEvalPhotos, deviceAddPhotos } from './AddEvalPhotos';
import type { AddPhotosController } from './addPhotos';
import { getEvalRunner } from './device';
import type { EvalRunner, EvalState } from './runner';

const THERMAL_LABEL = { nominal: 'Normal', fair: 'Warm', serious: 'Hot', critical: 'Very hot' } as const;

const photos = (count: number) => `${count} ${count === 1 ? 'photo' : 'photos'}`;

function clock(ms: number): string {
  const seconds = Math.max(0, Math.floor(ms / 1000));
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}

function useElapsed(state: EvalState): number {
  const [now, setNow] = useState(() => Date.now());
  const ticking = state.phase === 'running' || state.phase === 'starting' || state.phase === 'paused' || state.phase === 'waiting';
  useEffect(() => {
    if (!ticking) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [ticking]);
  return state.startedAt === null ? 0 : (ticking ? now : (state.endedAt ?? now)) - state.startedAt;
}

function Summary({ state }: { state: EvalState }) {
  const { summary } = state;
  if (!summary) return null;
  return (
    <Card>
      <Text variant="heading">{`M3 gate: ${summary.gate.verdict}`}</Text>
      {summary.gate.measures.map((m) => (
        <Text key={m.name}>{`${m.name}: ${m.actual ?? 'no data'} (needs ${m.bar}) · ${m.status}`}</Text>
      ))}
      <Text tone="muted">{`Compared with the last good run: ${summary.regression.status}.`}</Text>
      {summary.skipped.length > 0 ? <Text tone="muted">{`${summary.skipped.length} photos weren't scored (no truth file yet or not found).`}</Text> : null}
    </Card>
  );
}

/** A run that is picked up where it left off, or thrown away. Throwing it away asks once. */
function Resumable({ runner, state, adding }: { runner: EvalRunner; state: EvalState; adding: boolean }) {
  const [confirming, setConfirming] = useState(false);
  return (
    <Card>
      <Text variant="heading">{`${state.photosDone} of ${state.total} photos read`}</Text>
      <Text>Your PC is holding this run. Pick it up where it left off, or start over.</Text>
      {state.pending > 0 ? <Text tone="muted">{`${photos(state.pending)} read on this phone go up first.`}</Text> : null}
      {state.message ? <Text tone="muted">{state.message}</Text> : null}
      {confirming ? (
        <>
          <Text accessibilityRole="alert">{`Start over? Your PC throws away this run and the ${photos(state.photosDone)} it has read.`}</Text>
          <Button
            label={`Throw away ${photos(state.photosDone)} and start over`}
            variant="secondary"
            onPress={() => {
              setConfirming(false);
              void runner.startOver();
            }}
          />
          <Button label="Keep the run" variant="secondary" onPress={() => setConfirming(false)} />
        </>
      ) : (
        <>
          <Button label="Continue" disabled={adding} onPress={() => void runner.continueRun()} />
          <Button label="Start over" variant="secondary" onPress={() => setConfirming(true)} />
        </>
      )}
    </Card>
  );
}

export function EvalsScreen({ runner = getEvalRunner(), photos: addPhotosController }: { runner?: EvalRunner; photos?: AddPhotosController }) {
  const colors = useColors();
  const state = useSyncExternalStore(runner.subscribe, runner.getState);
  const [addPhotos] = useState(() => addPhotosController ?? deviceAddPhotos());
  const adding = useSyncExternalStore(addPhotos.subscribe, addPhotos.getState).phase === 'sending';
  const elapsed = useElapsed(state);
  const [note, setNote] = useState('');
  const working = state.phase === 'starting' || state.phase === 'running' || state.phase === 'paused' || state.phase === 'waiting';
  const busy = working || state.phase === 'finishing';
  const step = state.step === 'apple' ? 'Apple model' : state.step === 'text' ? 'Apple text model' : 'Vision text';

  // Asks the PC what it holds when the screen opens, and keeps asking whether it has finished scoring for as long as the screen is open.
  useEffect(() => {
    void runner.refresh();
    return runner.watch();
  }, [runner]);

  return (
    <SafeAreaView edges={['bottom', 'left', 'right']} style={{ flex: 1, backgroundColor: colors.ground }}>
    <Screen subtitle="Reads your PC's test photos on this iPhone. Each photo goes to your PC as soon as it's read, so you can leave and pick up where you left off." keyboardAvoiding>
      {state.phase === 'checking' ? <LoadingState label="Checking your PC for a run in progress." /> : null}

      {state.phase === 'idle' ? (
        <>
          <Text tone="muted">{"Keep the phone plugged in and out of direct sun. The photos aren't kept on the phone and aren't sent back."}</Text>
          {state.message ? <Text tone="muted">{state.message}</Text> : null}
          <TextField
            label="Baseline note"
            value={note}
            onChangeText={setNote}
            placeholder="Leave empty unless the thresholds moved"
            maxLength={500}
          />
          <Button label="Start the run" disabled={adding} onPress={() => void runner.start({ baselineNote: note })} />
        </>
      ) : null}

      {state.phase === 'resumable' ? <Resumable runner={runner} state={state} adding={adding} /> : null}

      {working ? (
        <Card>
          <Text variant="heading">
            {state.phase === 'starting'
              ? 'Opening a run on your PC'
              : state.current > 0
                ? `Photo ${state.current} of ${state.total} · ${step}`
                : `${state.photosDone} of ${state.total} photos read`}
          </Text>
          <Text>{`Time: ${clock(elapsed)}`}</Text>
          <Text>{`Phone temperature: ${state.thermal ? THERMAL_LABEL[state.thermal] : 'checking…'}`}</Text>
          {state.keepCool ? (
            <Text tone="danger" accessibilityRole="alert">
              The phone is hot. Move it out of the sun and take it off the charger if you can. The run keeps going and records the heat.
            </Text>
          ) : null}
          {state.phase === 'paused' ? (
            <Text tone="muted">
              {state.pause === 'background' ? 'Paused while the app is in the background. It picks up on its own when you come back.' : 'Paused.'}
            </Text>
          ) : null}
          {state.phase === 'waiting' ? (
            <Text accessibilityRole="alert">
              {state.pending > 0
                ? `Waiting for your PC. The ${photos(state.pending)} read so far ${state.pending === 1 ? 'is' : 'are'} kept on this phone and go up as soon as it answers.`
                : 'Waiting for your PC. Everything read so far is kept and goes up as soon as it answers.'}
            </Text>
          ) : null}
          {state.phase === 'running' || state.phase === 'paused' ? (
            <Button
              label={state.phase === 'paused' && state.pause === 'user' ? 'Resume' : 'Pause'}
              variant="secondary"
              disabled={state.phase === 'paused' && state.pause === 'background'}
              onPress={() => (state.phase === 'paused' ? runner.resume() : runner.pause())}
            />
          ) : null}
          <Button
            label={state.photosDone > 0 ? `Stop and score the ${state.photosDone} read so far` : 'Stop'}
            variant="secondary"
            disabled={state.phase === 'starting'}
            onPress={() => runner.stop()}
          />
        </Card>
      ) : null}

      {state.phase === 'finishing' ? <LoadingState label="Asking your PC to score the run." /> : null}

      {state.phase === 'scoring' ? (
        <Card>
          <LoadingState label="Your PC is scoring the run." />
          <Text tone="muted">{"You can leave this screen. The gate summary shows here when it's ready."}</Text>
        </Card>
      ) : null}

      {state.phase === 'error' ? (
        <>
          <Text tone="danger" accessibilityRole="alert">{state.message}</Text>
          <Button label="Check again" disabled={adding} onPress={() => void runner.recheck()} />
        </>
      ) : null}

      {state.phase === 'done' ? (
        <>
          <Text>
            {state.summary
              ? state.partial
                ? `Stopped early. Your PC scored the ${photos(state.photosDone)} it had read.`
                : `Your PC scored all ${photos(state.total)}.`
              : "Your PC scored the run. Open the report on your PC; the summary isn't on this phone."}
          </Text>
          {state.skipped.length > 0 ? <Text tone="muted">{`Skipped ${photos(state.skipped.length)} that wouldn't download.`}</Text> : null}
          {state.refused.length > 0 ? <Text tone="muted">{`Your PC wouldn't take ${photos(state.refused.length)}, so ${state.refused.length === 1 ? 'it is' : 'they are'} left out of the score.`}</Text> : null}
          <Summary state={state} />
          <Button label="Run again" variant="secondary" disabled={adding} onPress={() => void runner.start()} />
        </>
      ) : null}

      {busy ? null : <AddEvalPhotos controller={addPhotos} />}
    </Screen>
    </SafeAreaView>
  );
}
