import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react-native';
import type { ReactElement } from 'react';

export const makeQueryClient = () =>
  new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity }, mutations: { retry: false } } });

/** Renders with a fresh QueryClient so tests never share cache. */
export async function renderWithProviders(ui: ReactElement) {
  const client = makeQueryClient();
  return await render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}
