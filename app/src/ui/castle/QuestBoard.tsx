// The quest board (#362): an opt-in pin in the Great Hall. Down, it is one
// invitation card; pinned, it is parchment quest cards that wait as long as the
// person likes, plus a one-shot celebration when a quest newly finishes. Dragon
// Off removes the whole board, invitation and all.
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import { useEffect, useState } from 'react';
import { Pressable, View } from 'react-native';
import Animated, { FadeInDown } from 'react-native-reanimated';
import { StyleSheet } from 'react-native-unistyles';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { Button } from '../Button';
import { Card } from '../Card';
import { Text } from '../Text';
import { PlacedMascot } from '../mascot/PlacedMascot';
import { useDragonAppearance } from '../mascot/appearances';
import { motionMs } from '../motion';
import { BannerMarker } from './BannerMarker';
import { ParchmentCard } from './ParchmentCard';
import { freshQuests, useQuestBoard, type QuestState } from './quests';

/** How long a finished quest stays up before it rests by itself. */
export const QUEST_CELEBRATION_MS = 2200;

type QuestBoardProps = {
  quests: QuestState[];
  testID?: string;
};

export function QuestBoard({ quests, testID = 'quest-board' }: QuestBoardProps) {
  const appearance = useDragonAppearance((s) => s.appearance);
  const pinned = useQuestBoard((s) => s.pinned);
  const setPinned = useQuestBoard((s) => s.set);
  const reload = useQuestBoard((s) => s.reload);
  useEffect(() => {
    reload();
  }, [reload]);
  if (appearance === 'off') return null;
  if (!pinned) {
    return (
      <View testID={testID}>
        <Card>
          <BannerMarker label="Quest board" />
          <Text>Three quiet quests, no deadlines.</Text>
          <Button label="Pin the quest board" variant="secondary" onPress={() => setPinned(true)} />
        </Card>
      </View>
    );
  }
  return <PinnedBoard quests={quests} onUnpin={() => setPinned(false)} testID={testID} />;
}

function PinnedBoard({ quests, onUnpin, testID }: { quests: QuestState[]; onUnpin(): void; testID: string }) {
  // The first sight seeds quietly; only later finishes join the celebration.
  // New finishes merge during render (the documented previous-value pattern),
  // guarded by a signature so unchanged shelves never update.
  const [celebrating, setCelebrating] = useState<QuestState[]>(() => freshQuests(quests));
  const signature = quests.map((quest) => `${quest.id}:${quest.done ? 1 : 0}:${quest.progress}`).join(',');
  const [watched, setWatched] = useState(signature);
  if (signature !== watched) {
    setWatched(signature);
    const known = new Set(celebrating.map((quest) => quest.id));
    const next = freshQuests(quests).filter((quest) => !known.has(quest.id));
    if (next.length > 0) setCelebrating([...celebrating, ...next]);
  }
  const dismiss = (id: string) => setCelebrating((current) => current.filter((quest) => quest.id !== id));
  return (
    <View style={styles.board} testID={testID}>
      <Card>
        <BannerMarker label="Quest board" />
        <Text tone="muted">Quests wait as long as you like.</Text>
      </Card>
      {celebrating.map((quest) => (
        <QuestCelebration key={quest.id} quest={quest} onDone={() => dismiss(quest.id)} />
      ))}
      {quests.map((quest) => (
        <ParchmentCard key={quest.id} title={quest.title} body={`${quest.goal} ${quest.line}`} testID={`quest-${quest.id}`} />
      ))}
      <Button label="Take the board down" variant="secondary" onPress={onUnpin} />
    </View>
  );
}

function QuestCelebration({ quest, onDone }: { quest: QuestState; onDone(): void }) {
  const reduceMotion = useReduceMotion();
  useEffect(() => {
    const timer = setTimeout(onDone, QUEST_CELEBRATION_MS);
    return () => clearTimeout(timer);
  }, [onDone]);
  // Under Reduce Motion the quest card below tells the story on its own.
  if (reduceMotion) return null;
  return (
    <Animated.View entering={FadeInDown.duration(motionMs.base)}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={`Done. ${quest.title}. Dismiss.`}
        onPress={onDone}
        testID="quest-celebration"
        style={styles.celebration}
      >
        <PlacedMascot pose="celebrating" placement="state" size={96} testID="quest-celebration-dragon" />
        <Text variant="heading">Done. {quest.title}.</Text>
      </Pressable>
    </Animated.View>
  );
}

const styles = StyleSheet.create((theme) => ({
  board: { gap: theme.spacing.md },
  celebration: {
    alignItems: 'center',
    gap: theme.spacing.sm,
    padding: theme.spacing.lg,
    borderRadius: theme.radius.lg,
    borderWidth: 1,
    borderColor: theme.colors.line,
    backgroundColor: theme.colors.surface,
  },
}));
