// Members (owner only to change anything): the people in this library, their roles, and pending invites.
// Everyone else sees a read-only list. Web first; phone widths use the same layout, wrapped.
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { View } from 'react-native';
import { useGetMe } from '@/api/generated/account/account';
import { getListInvitesQueryKey, useListInvites, useRevokeInvite } from '@/api/generated/invites/invites';
import { getListMembersQueryKey, useListMembers, useRemoveMember, useUpdateMember } from '@/api/generated/libraries/libraries';
import type { InviteDto, LibraryRole, MemberDto } from '@/api/generated/models';
import { formatLastSeen } from '@/features/auth/DevicesScreen';
import { useActiveLibraryId } from '@/features/locations/useLocations';
import { LibraryLoading } from '@/features/onboarding/LibraryLoading';
import { friendlyError } from '@/lib/problem';
import { useNow } from '@/lib/useNow';
import { spacing } from '@/theme/tokens';
import { Button, Card, Chip, ListRow, LoadingState, Screen, Text } from '@/ui';
import { InviteDialog } from './InviteDialog';

const roleLabel: Record<LibraryRole, string> = { owner: 'Owner', editor: 'Editor', viewer: 'Viewer' };
const DAY = 86_400_000;

export function expiresIn(expiresAt: string, now = Date.now()): string {
  const days = Math.ceil((new Date(expiresAt).getTime() - now) / DAY);
  return days <= 1 ? 'expires today' : `expires in ${days} days`;
}

export function MembersScreen() {
  const { libraryId, isLoading } = useActiveLibraryId();
  const me = useGetMe();
  const myId = me.data?.status === 200 ? me.data.data.id : undefined;
  const myRole = me.data?.status === 200 ? me.data.data.memberships.find((m) => m.libraryId === libraryId)?.role : undefined;
  if (isLoading || me.isLoading || !libraryId) {
    return (
      <Screen title="Members">
        <LibraryLoading label="Loading members…" />
      </Screen>
    );
  }
  return <Members libraryId={libraryId} myId={myId} isOwner={myRole === 'owner'} />;
}

function Members({ libraryId, myId, isOwner }: { libraryId: string; myId: string | undefined; isOwner: boolean }) {
  const queryClient = useQueryClient();
  const [inviting, setInviting] = useState(false);
  const [removing, setRemoving] = useState<MemberDto | null>(null);
  const [message, setMessage] = useState<{ text: string; tone: 'info' | 'error' } | null>(null);

  const members = useListMembers(libraryId);
  const invites = useListInvites(libraryId, { query: { enabled: isOwner } });
  const list: MemberDto[] = members.data?.status === 200 ? members.data.data.items : [];
  const now = useNow();
  const pending: InviteDto[] =
    isOwner && invites.data?.status === 200
      ? invites.data.data.items.filter((i) => !i.acceptedAt && !i.revokedAt && new Date(i.expiresAt).getTime() > now)
      : [];

  const refreshMembers = () => queryClient.invalidateQueries({ queryKey: getListMembersQueryKey(libraryId) });
  const fail = (e: unknown) => setMessage({ text: friendlyError(e), tone: 'error' });

  const updateMember = useUpdateMember();
  const removeMember = useRemoveMember();
  const revokeInvite = useRevokeInvite();

  const changeRole = (member: MemberDto, role: LibraryRole) => {
    if (member.role === role) return;
    setMessage(null);
    updateMember.mutate(
      { libraryId, userId: member.userId, data: { role } },
      {
        onSuccess: (response) => {
          if (response.status === 200) setMessage({ text: `${member.displayName} is now ${/^[aeiou]/i.test(roleLabel[response.data.role]) ? 'an' : 'a'} ${roleLabel[response.data.role]}.`, tone: 'info' });
          refreshMembers();
        },
        onError: fail,
      },
    );
  };

  const confirmRemove = (member: MemberDto) => {
    removeMember.mutate(
      { libraryId, userId: member.userId },
      {
        onSuccess: () => {
          setRemoving(null);
          setMessage({ text: `${member.displayName} was removed and signed out everywhere.`, tone: 'info' });
          refreshMembers();
        },
        onError: (e) => {
          setRemoving(null);
          fail(e);
        },
      },
    );
  };

  const revoke = (invite: InviteDto) => {
    setMessage(null);
    revokeInvite.mutate(
      { libraryId, id: invite.id },
      {
        onSuccess: () => queryClient.invalidateQueries({ queryKey: getListInvitesQueryKey(libraryId) }),
        onError: fail,
      },
    );
  };

  return (
    <Screen title="Members" subtitle={isOwner ? 'Invite people and manage their roles.' : 'Only the owner can change who is here.'}>
      {message ? (
        <Text accessibilityRole="alert" tone={message.tone === 'error' ? 'danger' : 'muted'}>
          {message.text}
        </Text>
      ) : null}

      {isOwner && inviting ? <InviteDialog libraryId={libraryId} onClose={() => setInviting(false)} /> : null}
      {isOwner && !inviting ? (
        <View style={{ flexDirection: 'row' }}>
          <Button label="Invite someone" onPress={() => setInviting(true)} />
        </View>
      ) : null}

      {removing ? (
        <Card>
          <Text variant="heading">Remove {removing.displayName}?</Text>
          <Text tone="muted">This signs them out everywhere; items they added stay.</Text>
          <Button
            label={`Remove ${removing.displayName} and sign them out`}
            variant="danger"
            busy={removeMember.isPending}
            onPress={() => confirmRemove(removing)}
          />
          <Button label="Cancel" variant="secondary" onPress={() => setRemoving(null)} />
        </Card>
      ) : null}

      <Card>
        <View accessibilityLabel="Members table" style={{ gap: spacing.xs }}>
          {members.isLoading ? <LoadingState label="Loading members…" /> : null}
          {members.isError ? <Text tone="danger" accessibilityRole="alert">{friendlyError(members.error)}</Text> : null}
          {list.map((member) => {
            const isYou = member.userId === myId;
            const name = isYou ? `${member.displayName} (you)` : member.displayName;
            // Only the owner is sent each member's devices and last seen; for everyone else the fields are
            // missing, which is not the same as "no devices", so the row shows nothing there.
            const details = member.devices
              ? `${member.devices.length ? member.devices.join(', ') : 'No devices yet'} · ${formatLastSeen(member.lastSeenAt)}`
              : undefined;
            const canChange = isOwner && member.role !== 'owner';
            return (
              <ListRow
                key={member.userId}
                title={name}
                subtitle={details}
                trailing={
                  canChange ? (
                    <View style={{ flexDirection: 'row', gap: spacing.sm, flexWrap: 'wrap', justifyContent: 'flex-end' }}>
                      {(['editor', 'viewer'] as const).map((role) => (
                        <Chip
                          key={role}
                          label={roleLabel[role]}
                          accessibilityLabel={`Role for ${member.displayName}: ${roleLabel[role]}`}
                          selected={member.role === role}
                          onPress={() => changeRole(member, role)}
                        />
                      ))}
                      <Button
                        label="Remove"
                        accessibilityLabel={`Remove ${member.displayName}`}
                        variant="secondary"
                        onPress={() => {
                          setMessage(null);
                          setRemoving(member);
                        }}
                      />
                    </View>
                  ) : (
                    <Text tone="muted">{roleLabel[member.role]}</Text>
                  )
                }
              />
            );
          })}
        </View>
      </Card>

      {isOwner && pending.length > 0 ? (
        <Card>
          <Text variant="heading" accessibilityRole="header">
            Pending invites
          </Text>
          {pending.map((invite) => (
            <ListRow
              key={invite.id}
              title={`${invite.name} · ${roleLabel[invite.role]} · ${expiresIn(invite.expiresAt, now)}`}
              trailing={
                <Button
                  label="Revoke"
                  accessibilityLabel={`Revoke invite for ${invite.name}`}
                  variant="secondary"
                  onPress={() => revoke(invite)}
                />
              }
            />
          ))}
        </Card>
      ) : null}
    </Screen>
  );
}
