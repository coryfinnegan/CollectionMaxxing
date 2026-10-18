// The invite dialog: a name and a role, then the link and QR code, shown once (the server never returns them again).
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { View } from 'react-native';
import type { CreatedInviteDto } from '@/api/generated/models';
import { getListInvitesQueryKey, useCreateInvite } from '@/api/generated/invites/invites';
import { getQrRenderer } from '@/features/auth/services';
import { friendlyError } from '@/lib/problem';
import { useDialogBehavior } from '@/lib/a11y/useDialogBehavior';
import { spacing } from '@/theme/tokens';
import { Button, Card, Chip, Text, TextField } from '@/ui';

type InviteRole = 'editor' | 'viewer';
const roles: { role: InviteRole; label: string; hint: string }[] = [
  { role: 'editor', label: 'Editor', hint: 'Can scan, add and edit.' },
  { role: 'viewer', label: 'Viewer', hint: 'Can browse and check "do I own this?".' },
];

async function copyToClipboard(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}

export function InviteDialog({ libraryId, onClose }: { libraryId: string; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [name, setName] = useState('');
  const [role, setRole] = useState<InviteRole | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [created, setCreated] = useState<CreatedInviteDto | null>(null);
  const [copied, setCopied] = useState<'yes' | 'no' | null>(null);
  const create = useCreateInvite();
  const dialog = useDialogBehavior(onClose);

  const submit = () => {
    if (!name.trim() || !role) {
      setError('Give them a name and choose Editor or Viewer.');
      return;
    }
    setError(null);
    create.mutate(
      { libraryId, data: { name: name.trim(), role } },
      {
        onSuccess: (response) => {
          if (response.status === 201) setCreated(response.data);
          queryClient.invalidateQueries({ queryKey: getListInvitesQueryKey(libraryId) });
        },
        onError: (e) => setError(friendlyError(e)),
      },
    );
  };

  const header = (
    <View style={{ flexDirection: 'row', alignItems: 'center', gap: spacing.md }}>
      <Text variant="heading" accessibilityRole="header" style={{ flex: 1 }}>
        {created ? `Invite for ${created.invite.name}` : 'Invite someone'}
      </Text>
      <Button label="Close invite" variant="secondary" onPress={onClose} />
    </View>
  );

  if (created) {
    const renderQr = getQrRenderer();
    return (
      <View {...dialog}>
      <Card>
        {header}
        <Text>{"This link works once, for 7 days. It won't be shown again, so copy it now."}</Text>
        <Text selectable accessibilityLabel="Invite link">
          {created.link}
        </Text>
        <View style={{ alignItems: 'center' }}>{renderQr({ value: created.qrPayload, size: 200 })}</View>
        <Button
          label="Copy link"
          onPress={async () => setCopied((await copyToClipboard(created.link)) ? 'yes' : 'no')}
        />
        {copied === 'yes' ? <Text accessibilityRole="alert">Link copied.</Text> : null}
        {copied === 'no' ? <Text accessibilityRole="alert">{"Couldn't copy. Select the link above and copy it by hand."}</Text> : null}
        <Text tone="muted">
          {"Add them to Tailscale first, or the link won't open for them. Then send them the link yourself."}
        </Text>
      </Card>
      </View>
    );
  }

  return (
    <View {...dialog}>
    <Card>
      {header}
      <TextField label="Name" value={name} onChangeText={setName} autoFocus autoComplete="off" onSubmitEditing={submit} />
      <View style={{ gap: spacing.sm }}>
        <Text variant="label">Role</Text>
        <View style={{ flexDirection: 'row', gap: spacing.sm, flexWrap: 'wrap' }}>
          {roles.map((r) => (
            <Chip key={r.role} label={r.label} accessibilityLabel={`Invite as ${r.label}`} selected={role === r.role} onPress={() => setRole(r.role)} />
          ))}
        </View>
        <Text variant="caption" tone="muted">
          {roles.map((r) => `${r.label}: ${r.hint}`).join(' ')}
        </Text>
      </View>
      {error ? (
        <Text tone="danger" accessibilityRole="alert">
          {error}
        </Text>
      ) : null}
      <Button label="Create invite link" busy={create.isPending} onPress={submit} />
    </Card>
    </View>
  );
}
