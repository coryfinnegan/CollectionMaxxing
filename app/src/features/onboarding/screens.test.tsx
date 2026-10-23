import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { resetAuth } from '@/lib/auth/session';
import { setNavigator } from '@/features/auth/services';
import { MOCK_LOGOUT_URL } from '@/mocks/auth';
import { MOCK_LIBRARY_ID, mockAccount, mockMe } from '@/mocks/data';
import { MOCK_CODE } from '@/mocks/onboarding';
import { server } from '@/mocks/server';
import { useLibraryStore } from '@/state/library';
import { renderWithProviders } from '@/test/render';
import { CreateLibraryScreen } from './CreateLibraryScreen';
import { JoinInviteScreen } from './JoinInviteScreen';
import { JoinLibraryScreen } from './JoinLibraryScreen';
import { LeaveLibraryCard } from './LeaveLibraryCard';
import { LibrarySwitcher } from './LibrarySwitcher';
import { WelcomeScreen } from './WelcomeScreen';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({
  useRouter: () => mockRouter,
  Link: ({ children }: { children: unknown }) => children,
}));

const navigate = jest.fn();
beforeEach(() => {
  navigate.mockClear();
  mockRouter.push.mockClear();
  mockRouter.replace.mockClear();
  useLibraryStore.setState({ activeLibraryId: null });
  setNavigator(navigate);
});
afterEach(() => resetAuth());

const problem = (status: number, code: string) =>
  HttpResponse.json({ title: code, status, code }, { status, headers: { 'Content-Type': 'application/problem+json' } });

describe('WelcomeScreen', () => {
  it('greets you by name and says how you are signed in', async () => {
    mockAccount.libraries = [];
    await renderWithProviders(<WelcomeScreen />);

    expect(await screen.findByText('Welcome, Cory.')).toBeTruthy();
    expect(await screen.findByText("You're signed in as cory@example.com.")).toBeTruthy();
  });

  it('leaves out the signed-in line when the account has no email', async () => {
    server.use(http.get('*/api/v1/me', () => HttpResponse.json({ ...mockMe(), email: null })));
    await renderWithProviders(<WelcomeScreen />);

    expect(await screen.findByText('Welcome, Cory.')).toBeTruthy();
    expect(screen.queryByText(/signed in as/)).toBeNull();
  });

  it('offers create, join and another account', async () => {
    await renderWithProviders(<WelcomeScreen />);
    await fireEvent.press(screen.getByLabelText('Create your library'));
    await fireEvent.press(screen.getByLabelText('Join a library'));

    expect(mockRouter.push).toHaveBeenNthCalledWith(1, '/welcome/create');
    expect(mockRouter.push).toHaveBeenNthCalledWith(2, '/welcome/join');
  });

  it('Use another account ends the Auth0 session so the account picker shows again', async () => {
    await renderWithProviders(<WelcomeScreen />);
    await fireEvent.press(screen.getByLabelText('Use another account'));

    await waitFor(() => expect(navigate).toHaveBeenCalledWith(MOCK_LOGOUT_URL));
  });

  it('says so when signing out fails', async () => {
    server.use(http.post('*/account/logout', () => HttpResponse.json({ title: 'x', status: 500 }, { status: 500 })));
    await renderWithProviders(<WelcomeScreen />);
    await fireEvent.press(screen.getByLabelText('Use another account'));

    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(navigate).not.toHaveBeenCalled();
  });
});

describe('CreateLibraryScreen', () => {
  it("prefills the name with the person's name, creates the library and makes it the active one", async () => {
    mockAccount.libraries = [];
    await renderWithProviders(<CreateLibraryScreen />);
    await waitFor(() => expect(screen.getByLabelText('Library name').props.value).toBe("Cory's library"));
    await fireEvent.press(screen.getByLabelText('Create library'));

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/library'));
    expect(mockAccount.libraries).toHaveLength(1);
    expect(mockAccount.libraries[0]).toMatchObject({ name: "Cory's library", role: 'owner' });
    expect(useLibraryStore.getState().activeLibraryId).toBe(mockAccount.libraries[0]!.id);
  });

  it('sends the name as typed', async () => {
    await renderWithProviders(<CreateLibraryScreen />);
    await fireEvent.changeText(screen.getByLabelText('Library name'), '  Game room ');
    await fireEvent.press(screen.getByLabelText('Create library'));

    await waitFor(() => expect(mockAccount.libraries.map((l) => l.name)).toContain('Game room'));
  });

  it('asks for a name when it is empty and sends nothing', async () => {
    await renderWithProviders(<CreateLibraryScreen />);
    await fireEvent.changeText(screen.getByLabelText('Library name'), '   ');
    await fireEvent.press(screen.getByLabelText('Create library'));

    expect(await screen.findByText('Give your library a name.')).toBeTruthy();
    expect(mockAccount.libraries).toHaveLength(1);
  });

  it('says why at the limit of three owned libraries', async () => {
    mockAccount.libraries = [1, 2, 3].map((n) => ({ id: `f0000000-0000-4000-8000-00000000000${n}`, name: `L${n}`, role: 'owner' as const }));
    await renderWithProviders(<CreateLibraryScreen />);
    await fireEvent.press(screen.getByLabelText('Create library'));

    expect(await screen.findByText('You already own 3 libraries, which is the most you can have.')).toBeTruthy();
    expect(mockRouter.replace).not.toHaveBeenCalled();
  });

  it('goes back to Welcome when there are no libraries', async () => {
    mockAccount.libraries = [];
    await renderWithProviders(<CreateLibraryScreen />);
    await waitFor(() => expect(screen.getByLabelText('Library name').props.value).toBe("Cory's library"));
    await fireEvent.press(screen.getByLabelText('Back'));

    expect(mockRouter.replace).toHaveBeenLastCalledWith('/welcome');
  });

  it('goes back to the library when there is one', async () => {
    await renderWithProviders(<CreateLibraryScreen />);
    await waitFor(() => expect(screen.getByLabelText('Library name').props.value).toBe("Cory's library"));
    // Wait for the library list too: until it arrives, Back cannot know where to go.
    await new Promise((resolve) => setTimeout(resolve, 100));
    await fireEvent.press(screen.getByLabelText('Back'));

    expect(mockRouter.replace).toHaveBeenLastCalledWith('/library');
  });
});

describe('JoinLibraryScreen', () => {
  const type = (text: string) => fireEvent.changeText(screen.getByLabelText('Invite link or code'), text);

  it('previews a pasted link, then joins with the invited role', async () => {
    await renderWithProviders(<JoinLibraryScreen />);
    expect(screen.getByText('Paste the invite link or type the code you were sent.')).toBeTruthy();
    await type('https://pc.tailnet.ts.net/join/valid-invite');
    await fireEvent.press(screen.getByLabelText('Check invite'));

    expect(await screen.findByText("Join Cory's library as Editor")).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Join'));

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/library'));
    expect(mockAccount.libraries).toContainEqual(expect.objectContaining({ name: "Cory's library", role: 'editor' }));
  });

  it('forgives case and dashes in a code', async () => {
    await renderWithProviders(<JoinLibraryScreen />);
    await type(MOCK_CODE.toLowerCase().replace(/-/g, ' '));
    await fireEvent.press(screen.getByLabelText('Check invite'));

    expect(await screen.findByText("Join Sam's library as Viewer")).toBeTruthy();
  });

  it('shows one message for a bad invite', async () => {
    await renderWithProviders(<JoinLibraryScreen />);
    await type('nothing-here');
    await fireEvent.press(screen.getByLabelText('Check invite'));

    expect(await screen.findByText("That invite doesn't work. Ask for a new one.")).toBeTruthy();
    expect(screen.queryByLabelText('Join')).toBeNull();
  });

  it('does not call the server for text that is not an invite', async () => {
    let called = false;
    server.use(http.post('*/api/v1/invites\\:preview', () => ((called = true), problem(404, 'invite.invalid'))));
    await renderWithProviders(<JoinLibraryScreen />);
    await type('??');
    await fireEvent.press(screen.getByLabelText('Check invite'));

    expect(await screen.findByText("That invite doesn't work. Ask for a new one.")).toBeTruthy();
    expect(called).toBe(false);
  });

  it('says so when you are already in that library', async () => {
    await renderWithProviders(<JoinLibraryScreen />);
    await type('taken-invite');
    await fireEvent.press(screen.getByLabelText('Check invite'));
    await screen.findByText("Join Cory's library as Editor");
    await fireEvent.press(screen.getByLabelText('Join'));

    expect(await screen.findByText("You're already in that library.")).toBeTruthy();
  });

  it('asks to check again after the text changes', async () => {
    await renderWithProviders(<JoinLibraryScreen />);
    await type('valid-invite');
    await fireEvent.press(screen.getByLabelText('Check invite'));
    await screen.findByText("Join Cory's library as Editor");
    await type('valid-invit');

    expect(screen.queryByLabelText('Join')).toBeNull();
    expect(screen.getByLabelText('Check invite')).toBeTruthy();
  });
});

describe('JoinInviteScreen', () => {
  it('signed out, offers Sign in to join and comes back to the same page', async () => {
    mockAccount.signedIn = false;
    await renderWithProviders(<JoinInviteScreen token="valid-invite" />);
    expect(await screen.findByText("Join Cory's library as Editor")).toBeTruthy();
    await waitFor(() => expect(screen.getByLabelText('Sign in to join').props.accessibilityState.disabled).toBe(false));
    await fireEvent.press(screen.getByLabelText('Sign in to join'));

    expect(navigate).toHaveBeenCalledWith('/account/login?returnUrl=%2Fjoin%2Fvalid-invite');
  });

  it('signed in, offers Join and joins', async () => {
    await renderWithProviders(<JoinInviteScreen token="valid-invite" />);
    expect(await screen.findByText("Join Cory's library as Editor")).toBeTruthy();
    await waitFor(() => expect(screen.getByLabelText('Join').props.accessibilityState.disabled).toBe(false));
    expect(screen.queryByLabelText('Sign in to join')).toBeNull();
    await fireEvent.press(screen.getByLabelText('Join'));

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/library'));
    expect(mockAccount.libraries).toHaveLength(2);
  });

  it.each(['expired-invite', 'used-invite', 'who-knows'])('shows the same message for the bad, used or expired token %s', async (token) => {
    await renderWithProviders(<JoinInviteScreen token={token} />);

    expect(await screen.findByText("That invite doesn't work. Ask for a new one.")).toBeTruthy();
    expect(screen.queryByLabelText('Sign in to join')).toBeNull();
    expect(screen.queryByLabelText('Join')).toBeNull();
  });
});

describe('LibrarySwitcher', () => {
  const two = () => {
    mockAccount.libraries = [
      { id: MOCK_LIBRARY_ID, name: "Cory's library", role: 'owner' },
      { id: 'f0000000-0000-4000-8000-000000000002', name: "Sam's library", role: 'viewer' },
    ];
  };

  it('is hidden with one library, apart from New library and Join a library', async () => {
    await renderWithProviders(<LibrarySwitcher />);

    expect(await screen.findByText('New library')).toBeTruthy();
    expect(screen.getByText('Join a library')).toBeTruthy();
    expect(screen.queryByLabelText("Cory's library, Owner")).toBeNull();
  });

  it('lists each library with your role and switches the active one', async () => {
    two();
    await renderWithProviders(<LibrarySwitcher />);
    await fireEvent.press(await screen.findByLabelText("Sam's library, Viewer"));

    expect(useLibraryStore.getState().activeLibraryId).toBe('f0000000-0000-4000-8000-000000000002');
    expect(screen.getByLabelText("Cory's library, Owner")).toBeTruthy();
  });

  it('shows nothing for an account with no libraries', async () => {
    mockAccount.libraries = [];
    await renderWithProviders(<LibrarySwitcher />);
    await waitFor(() => expect(screen.queryByText('New library')).toBeNull());
  });
});

describe('LeaveLibraryCard', () => {
  it('is not shown to an owner', async () => {
    await renderWithProviders(<LeaveLibraryCard />);
    await waitFor(() => expect(screen.queryByLabelText('Leave library')).toBeNull());
  });

  it('asks first, then ends only your membership', async () => {
    mockAccount.libraries = [{ id: MOCK_LIBRARY_ID, name: "Cory's library", role: 'editor' }];
    await renderWithProviders(<LeaveLibraryCard />);
    await fireEvent.press(await screen.findByLabelText('Leave library'));

    expect(screen.getByText("You'll lose access to Cory's library. Your account stays.")).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('Leave library'));

    await waitFor(() => expect(mockAccount.libraries).toHaveLength(0));
  });

  it('can be cancelled', async () => {
    mockAccount.libraries = [{ id: MOCK_LIBRARY_ID, name: "Cory's library", role: 'viewer' }];
    await renderWithProviders(<LeaveLibraryCard />);
    await fireEvent.press(await screen.findByLabelText('Leave library'));
    await fireEvent.press(screen.getByLabelText('Cancel'));

    expect(screen.queryByText(/You'll lose access/)).toBeNull();
    expect(mockAccount.libraries).toHaveLength(1);
  });

  it('says why when the last owner cannot leave', async () => {
    mockAccount.libraries = [{ id: MOCK_LIBRARY_ID, name: "Cory's library", role: 'editor' }];
    server.use(http.delete('*/api/v1/libraries/:libraryId/membership', () => problem(409, 'library.last_owner')));
    await renderWithProviders(<LeaveLibraryCard />);
    await fireEvent.press(await screen.findByLabelText('Leave library'));
    await fireEvent.press(screen.getByLabelText('Leave library'));

    expect(await screen.findByText("A library needs an owner, so its last owner can't leave.")).toBeTruthy();
  });
});
