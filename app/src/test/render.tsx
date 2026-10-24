import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react-native';
import type { ReactElement } from 'react';
import { SafeAreaProvider, type Metrics } from 'react-native-safe-area-context';

export const makeQueryClient = () =>
  new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity }, mutations: { retry: false } } });

// Jest has no native safe-area metrics, and the provider renders nothing until metrics exist;
// zero insets match the old provider-less behavior.
const testMetrics: Metrics = {
  frame: { x: 0, y: 0, width: 390, height: 844 },
  insets: { top: 0, bottom: 0, left: 0, right: 0 },
};

/** Renders with a fresh QueryClient so tests never share cache, inside a SafeAreaProvider like the app root. */
export async function renderWithProviders(ui: ReactElement) {
  const client = makeQueryClient();
  return await render(
    <QueryClientProvider client={client}>
      <SafeAreaProvider initialMetrics={testMetrics}>{ui}</SafeAreaProvider>
    </QueryClientProvider>,
  );
}
