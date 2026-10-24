import { QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react-native';
import type { ReactNode } from 'react';
import { MOCK_LIBRARY_ID, mockAccount } from '@/mocks/data';
import { useLibraryStore } from '@/state/library';
import { makeQueryClient, renderWithProviders } from '@/test/render';
import { LibraryGate } from './LibraryGate';
import { useActiveLibraryId } from './useLibraries';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const SECOND = 'f0000000-0000-4000-8000-000000000002';

const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={makeQueryClient()}>{children}</QueryClientProvider>;

beforeEach(() => {
  mockRouter.replace.mockClear();
  useLibraryStore.setState({ activeLibraryId: null });
});

describe('useActiveLibraryId', () => {
  it('uses the library remembered on this device', async () => {
    mockAccount.libraries = [
      { id: MOCK_LIBRARY_ID, name: 'A', role: 'owner' },
      { id: SECOND, name: 'B', role: 'viewer' },
    ];
    useLibraryStore.setState({ activeLibraryId: SECOND });
    const { result } = await renderHook(() => useActiveLibraryId(), { wrapper });

    await waitFor(() => expect(result.current.libraryId).toBe(SECOND));
  });

  it('falls back to the first library when the remembered one is not yours any more', async () => {
    useLibraryStore.setState({ activeLibraryId: SECOND });
    const { result } = await renderHook(() => useActiveLibraryId(), { wrapper });

    await waitFor(() => expect(result.current.libraryId).toBe(MOCK_LIBRARY_ID));
  });

  it('has no library for a new account', async () => {
    mockAccount.libraries = [];
    const { result } = await renderHook(() => useActiveLibraryId(), { wrapper });

    await waitFor(() => expect(result.current.isLoading).toBe(false));
    expect(result.current.libraryId).toBeUndefined();
  });
});

describe('LibraryGate (web and iPhone)', () => {
  it('sends an account with no libraries to Welcome', async () => {
    mockAccount.libraries = [];
    await renderWithProviders(<LibraryGate />);

    await waitFor(() => expect(mockRouter.replace).toHaveBeenCalledWith('/welcome'));
  });

  it('leaves an account with a library alone', async () => {
    await renderWithProviders(<LibraryGate />);
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(mockRouter.replace).not.toHaveBeenCalled();
  });
});
