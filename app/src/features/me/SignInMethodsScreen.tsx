// Me > Sign-in methods: the Google and Apple accounts that open this library. Add another, or remove one (never the last).
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useLocalSearchParams } from 'expo-router';
import { useState } from 'react';
import { listSignInMethods, removeSignInMethod } from '@/features/auth/api';
import type { SignInMethodDto } from '@/features/auth/contract';
import { loginUrl, navigateTo, signInErrorMessage } from '@/features/auth/navigation';
import { friendlyError } from '@/lib/problem';
import { Button, Card, ListRow, Screen, Text } from '@/ui';
import { BackButton } from './BackButton';

const methodsKey = ['me', 'sign-in-methods'] as const;
const PAGE = '/me/sign-in-methods';

const providerLabel: Record<SignInMethodDto['provider'], string> = { google: 'Google', apple: 'Apple', other: 'Other account' };

function describe(method: SignInMethodDto): string {
  const used = method.lastUsedAt ? `Last used ${new Date(method.lastUsedAt).toLocaleDateString()}` : 'Not used yet';
  return [method.email, used].filter(Boolean).join(' · ');
}

export function SignInMethodsScreen() {
  const queryClient = useQueryClient();
  const params = useLocalSearchParams<{ error?: string }>();
  const methods = useQuery({ queryKey: methodsKey, queryFn: listSignInMethods });
  const [confirming, setConfirming] = useState<SignInMethodDto | null>(null);
  const [message, setMessage] = useState<string | null>(signInErrorMessage(params.error));

  const remove = useMutation({
    mutationFn: (method: SignInMethodDto) => removeSignInMethod(method.id),
    onSuccess: () => {
      setConfirming(null);
      queryClient.invalidateQueries({ queryKey: methodsKey });
    },
    onError: (e) => {
      setConfirming(null);
      setMessage(friendlyError(e));
    },
  });

  const items = methods.data ?? [];
  const canRemove = items.length > 1;

  return (
    <Screen title="Sign-in methods" subtitle="The accounts you can use to sign in.">
      <BackButton />
      {message ? (
        <Text tone="danger" accessibilityRole="alert">
          {message}
        </Text>
      ) : null}

      {confirming ? (
        <Card>
          <Text variant="heading">{`Remove ${providerLabel[confirming.provider]}?`}</Text>
          <Text tone="muted">
            {confirming.isCurrent
              ? "You're signed in with this account right now. You won't be able to use it to sign in again."
              : "You won't be able to sign in with it anymore."}
          </Text>
          <Button label="Remove" variant="danger" busy={remove.isPending} onPress={() => remove.mutate(confirming)} />
          <Button label="Cancel" variant="secondary" onPress={() => setConfirming(null)} />
        </Card>
      ) : null}

      <Card>
        {methods.isLoading ? <Text tone="muted">Loading...</Text> : null}
        {methods.isError ? <Text tone="danger">{friendlyError(methods.error)}</Text> : null}
        {items.map((method) => (
          <ListRow
            key={method.id}
            title={method.isCurrent ? `${providerLabel[method.provider]} (this sign-in)` : providerLabel[method.provider]}
            subtitle={describe(method)}
            trailing={
              canRemove ? (
                <Button
                  label="Remove"
                  accessibilityLabel={`Remove ${providerLabel[method.provider]}${method.email ? ` ${method.email}` : ''}`}
                  variant="secondary"
                  onPress={() => {
                    setMessage(null);
                    setConfirming(method);
                  }}
                />
              ) : undefined
            }
          />
        ))}
        {items.length === 1 ? <Text tone="muted">This is your only way to sign in, so it can&apos;t be removed. Add another first.</Text> : null}
      </Card>

      <Button label="Add a sign-in method" onPress={() => navigateTo(loginUrl({ link: true, returnUrl: PAGE }))} />
    </Screen>
  );
}
