import { Card } from './Card';
import { Screen } from './Screen';
import { Text } from './Text';

/** Stands in for a screen that a later task builds. Shows its name and what it will do (from docs/06). */
export function Placeholder({ name, description }: { name: string; description: string }) {
  return (
    <Screen title={name}>
      <Card>
        <Text>{description}</Text>
        <Text variant="caption" tone="muted">
          Coming soon.
        </Text>
      </Card>
    </Screen>
  );
}
