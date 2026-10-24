// Owner-only dev screen: run the golden photos through both engines on this iPhone and see the PC's gate table when it is done.
import { useEffect, useState, useSyncExternalStore } from 'react';
import { SafeAreaView } from 'react-native-safe-area-context';
import { useColors } from '@/theme/tokens';
import { Button, Card, Screen, Text } from '@/ui';
import { getEvalRunner } from './device';
import type { EvalRunner, EvalState } from './runner';

const THERMAL_LABEL = { nominal: 'Normal', fair: 'Warm', serious: 'Hot', critical: 'Very hot' } as const;

function clock(ms: number): string {
  const seconds = Math.max(0, Math.floor(ms / 1000));
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}

function useElapsed(state: EvalState): number {
  const [now, setNow] = useState(() => Date.now());
  const ticking = state.phase === 'running' || state.phase === 'starting' || state.phase === 'paused';
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

export function EvalsScreen({ runner = getEvalRunner() }: { runner?: EvalRunner }) {
  const colors = useColors();
  const state = useSyncExternalStore(runner.subscribe, runner.getState);
  const elapsed = useElapsed(state);
  const working = state.phase === 'starting' || state.phase === 'running' || state.phase === 'paused';
  const apple = state.step === 'apple' ? 'Apple model' : 'Vision text';

  return (
    <SafeAreaView edges={['bottom', 'left', 'right']} style={{ flex: 1, backgroundColor: colors.ground }}>
    <Screen title="Run evals" subtitle="Reads your PC's test photos on this iPhone and sends the readings back to be scored.">
      {state.phase === 'idle' ? (
        <>
          <Text tone="muted">Keep the phone awake, plugged in and out of direct sun. The photos are not kept on the phone and are not sent back.</Text>
          <Button label="Start the run" onPress={() => void runner.start()} />
        </>
      ) : null}

      {working ? (
        <Card>
          <Text variant="heading">
            {state.phase === 'starting' ? 'Getting the photo list' : `Photo ${state.current} of ${state.total} · ${apple}`}
          </Text>
          <Text>{`Time: ${clock(elapsed)}`}</Text>
          <Text>{`Phone temperature: ${state.thermal ? THERMAL_LABEL[state.thermal] : 'not known yet'}`}</Text>
          {state.keepCool ? (
            <Text tone="danger" accessibilityRole="alert">
              The phone is hot. Move it out of the sun and take it off the charger if you can. The run carries on and records the heat.
            </Text>
          ) : null}
          {state.phase === 'paused' ? <Text tone="muted">Paused.</Text> : null}
          <Button
            label={state.phase === 'paused' ? 'Resume' : 'Pause'}
            variant="secondary"
            disabled={state.phase === 'starting'}
            onPress={() => (state.phase === 'paused' ? void runner.resume() : runner.pause())}
          />
          <Button label="Cancel and send what's read so far" variant="secondary" onPress={() => runner.cancel()} />
        </Card>
      ) : null}

      {state.phase === 'uploading' ? <Text>Sending the results to your PC.</Text> : null}

      {state.phase === 'upload_failed' ? (
        <>
          <Text tone="danger" accessibilityRole="alert">{state.message}</Text>
          <Button label={`Send ${state.resultCount} results again`} onPress={() => void runner.retryUpload()} />
        </>
      ) : null}

      {state.phase === 'error' ? (
        <>
          <Text tone="danger" accessibilityRole="alert">{state.message}</Text>
          <Button label="Try again" onPress={() => void runner.start()} />
        </>
      ) : null}

      {state.phase === 'done' ? (
        <>
          <Text>
            {state.message ??
              (state.partial
                ? `Cancelled. Sent ${state.photosDone} of ${state.total} photos, marked partial.`
                : `Sent ${state.photosDone} photos to your PC.`)}
          </Text>
          {state.skipped.length > 0 ? <Text tone="muted">{`${state.skipped.length} photos couldn't be downloaded and were skipped.`}</Text> : null}
          <Summary state={state} />
          {state.partial ? <Button label="Carry on with the rest" onPress={() => void runner.resume()} /> : null}
          <Button label="Run again" variant="secondary" onPress={() => void runner.start()} />
        </>
      ) : null}
    </Screen>
    </SafeAreaView>
  );
}
