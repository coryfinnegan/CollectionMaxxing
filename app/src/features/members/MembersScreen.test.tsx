import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { Text } from 'react-native';
import { PasskeyCancelledError } from '@/lib/auth/errors';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { mockAuth, resetMockAuth } from '@/mocks/auth';
import { mockMe } from '@/mocks/data';
import { mockMembers, resetMembers } from '@/mocks/members';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { setQrRenderer } from '@/features/auth/services';
import { MembersScreen } from './MembersScreen';

beforeEach(() => {
  resetMembers();
  resetMockAuth();
  configureAuth({ platform: 'web' });
  setQrRenderer(({ value }) => <Text>{`qr:${value}`}</Text>);
});
afterEach(() => resetAuth());

const asRole = (role: 'owner' | 'editor' | 'viewer') =>
  server.use(
    http.get('*/api/v1/me', () =>
      HttpResponse.json({ ...mockMe(), memberships: [{ libraryId: mockMe().memberships[0]!.libraryId, role }] }),
    ),
  );

async function openInvite(name: string, role: 'Editor' | 'Viewer') {
  await fireEvent.press(await screen.findByLabelText('Invite someone'));
  await fireEvent.changeText(screen.getByLabelText('Name'), name);
  await fireEvent.press(screen.getByLabelText(`Invite as ${role}`));
  await fireEvent.press(screen.getByLabelText('Create invite link'));
}

describe('MembersScreen', () => {
  it('shows the owner the full table with invite controls', async () => {
    await renderWithProviders(<MembersScreen />);

    expect(await screen.findByText('Sam')).toBeTruthy();
    expect(screen.getByLabelText('Invite someone')).toBeTruthy();
    expect(screen.getByLabelText('Remove Sam')).toBeTruthy();
    expect(screen.getByLabelText('Role for Sam: Viewer')).toBeTruthy();
    expect(await screen.findByLabelText('Revoke invite for Alex')).toBeTruthy();
    expect(screen.getByText(/Safari on iPhone/)).toBeTruthy();
  });

  it.each(['editor', 'viewer'] as const)('shows a %s a read-only list without invite data', async (role) => {
    asRole(role);
    let invitesRequested = false;
    server.use(
      http.get('*/api/v1/libraries/:libraryId/invites', () => {
        invitesRequested = true;
        return HttpResponse.json({ items: [], nextCursor: null });
      }),
    );
    await renderWithProviders(<MembersScreen />);

    expect(await screen.findByText('Sam')).toBeTruthy();
    expect(screen.queryByLabelText('Invite someone')).toBeNull();
    expect(screen.queryByLabelText(/^Remove /)).toBeNull();
    expect(screen.queryByLabelText(/^Role for /)).toBeNull();
    expect(screen.queryByText('Pending invites')).toBeNull();
    expect(invitesRequested).toBe(false);
  });

  it('shows the invite link once and does not show it again after closing', async () => {
    await renderWithProviders(<MembersScreen />);
    await openInvite('Jordan', 'Viewer');

    expect(await screen.findByText(/^https:\/\/pc\.tailnet\.ts\.net\/join\//)).toBeTruthy();
    expect(screen.getByText(/^qr:/)).toBeTruthy();
    expect(screen.getByText(/Add them to Tailscale first/)).toBeTruthy();
    expect(screen.getByLabelText('Copy link')).toBeTruthy();

    await fireEvent.press(screen.getByLabelText('Close invite'));
    await fireEvent.press(screen.getByLabelText('Invite someone'));

    expect(screen.queryByText(/^https:\/\/pc\.tailnet\.ts\.net\/join\//)).toBeNull();
    expect(screen.queryByLabelText('Copy link')).toBeNull();
    expect(screen.getByLabelText('Name').props.value).toBe('');
  });

  it('lists the new invite as pending with Revoke only', async () => {
    await renderWithProviders(<MembersScreen />);
    await openInvite('Jordan', 'Viewer');
    await screen.findByLabelText('Copy link');
    await fireEvent.press(screen.getByLabelText('Close invite'));

    expect(await screen.findByLabelText('Revoke invite for Jordan')).toBeTruthy();
    expect(screen.queryAllByLabelText('Copy link')).toHaveLength(0);
  });

  it('asks for a name before creating an invite', async () => {
    await renderWithProviders(<MembersScreen />);
    await fireEvent.press(await screen.findByLabelText('Invite someone'));
    await fireEvent.press(screen.getByLabelText('Create invite link'));

    expect(await screen.findByText('Give them a name and choose Editor or Viewer.')).toBeTruthy();
    expect(mockMembers.invites).toHaveLength(2);
  });

  it('runs the passkey check once and retries when creating an invite needs it', async () => {
    mockAuth.requireReauth = true;
    const reauth = jest.fn(async () => {
      mockAuth.requireReauth = false;
    });
    configureAuth({ reauth });
    await renderWithProviders(<MembersScreen />);
    await openInvite('Jordan', 'Editor');

    expect(await screen.findByText(/^https:\/\/pc\.tailnet\.ts\.net\/join\//)).toBeTruthy();
    expect(reauth).toHaveBeenCalledTimes(1);
  });

  it('says so when the passkey check is cancelled and creates no invite', async () => {
    mockAuth.requireReauth = true;
    configureAuth({ reauth: () => Promise.reject(new PasskeyCancelledError()) });
    await renderWithProviders(<MembersScreen />);
    await openInvite('Jordan', 'Editor');

    expect(await screen.findByText("Passkey cancelled. Try again when you're ready.")).toBeTruthy();
    expect(screen.queryByText(/^https:\/\/pc\.tailnet\.ts\.net\/join\//)).toBeNull();
  });

  it('changes a role with the new role and shows the server answer', async () => {
    let sent: unknown;
    server.use(
      http.patch('*/api/v1/libraries/:libraryId/members/:userId', async ({ request }) => {
        sent = await request.json();
        mockMembers.members[1]!.role = 'viewer';
        return HttpResponse.json(mockMembers.members[1]);
      }),
    );
    await renderWithProviders(<MembersScreen />);
    await fireEvent.press(await screen.findByLabelText('Role for Sam: Viewer'));

    expect(await screen.findByText('Sam is now a Viewer.')).toBeTruthy();
    expect(sent).toEqual({ role: 'viewer' });
    await waitFor(() => expect(screen.getByLabelText('Role for Sam: Viewer').props.accessibilityState.selected).toBe(true));
  });

  it('gives the owner row no role select and no Remove', async () => {
    await renderWithProviders(<MembersScreen />);
    await screen.findByText('Sam');

    expect(screen.getByText('Cory (you)')).toBeTruthy();
    expect(screen.queryByLabelText('Role for Cory: Viewer')).toBeNull();
    expect(screen.queryByLabelText('Remove Cory')).toBeNull();
    expect(screen.queryByLabelText('Remove Cory (you)')).toBeNull();
  });

  it('removes a member only after confirmation', async () => {
    await renderWithProviders(<MembersScreen />);
    await fireEvent.press(await screen.findByLabelText('Remove Sam'));

    expect(screen.getByText(/signs them out everywhere; items they added stay/)).toBeTruthy();
    expect(mockMembers.members).toHaveLength(3);

    await fireEvent.press(screen.getByLabelText('Remove Sam and sign them out'));
    await waitFor(() => expect(screen.queryByText('Sam')).toBeNull());
    expect(mockMembers.members).toHaveLength(2);
  });

  it('does nothing when the remove confirmation is cancelled', async () => {
    await renderWithProviders(<MembersScreen />);
    await fireEvent.press(await screen.findByLabelText('Remove Sam'));
    await fireEvent.press(screen.getByLabelText('Cancel'));

    expect(screen.queryByText(/signs them out everywhere/)).toBeNull();
    expect(screen.getByText('Sam')).toBeTruthy();
    expect(mockMembers.members).toHaveLength(3);
  });

  it('revokes a pending invite and does not list expired ones', async () => {
    await renderWithProviders(<MembersScreen />);
    await screen.findByLabelText('Revoke invite for Alex');
    expect(screen.queryByText(/Jo ·/)).toBeNull();
    expect(screen.getByText('Alex · Viewer · expires in 5 days')).toBeTruthy();

    await fireEvent.press(screen.getByLabelText('Revoke invite for Alex'));

    await waitFor(() => expect(screen.queryByLabelText('Revoke invite for Alex')).toBeNull());
    expect(mockMembers.invites.map((i) => i.name)).toEqual(['Jo']);
  });

  it('labels every control', async () => {
    await renderWithProviders(<MembersScreen />);
    await screen.findByLabelText('Revoke invite for Alex');
    await fireEvent.press(screen.getByLabelText('Invite someone'));

    for (const label of ['Name', 'Invite as Editor', 'Invite as Viewer', 'Create invite link', 'Close invite']) {
      expect(screen.getByLabelText(label)).toBeTruthy();
    }
  });
});

