// Leaving a pushed screen without stranding the page. On a fresh load (a pasted link, a reload) the browser has
// no history, so `router.back()` does nothing and the person stays on a screen that just finished its job.
import { type Href, useRouter } from 'expo-router';

/** The slice of the router this needs, so tests and the back button can hand in their own. */
export type BackRouter = { canGoBack(): boolean; back(): void; replace(href: Href): void };

/** Goes back when there is a screen to go back to; otherwise replaces this one with `fallback`. */
export function goBackOr(router: BackRouter, fallback: Href): void {
  if (router.canGoBack()) router.back();
  else router.replace(fallback);
}

/** `goBackOr` bound to the current router, ready to hand to a button or a mutation callback. */
export function useGoBackOr(fallback: Href): () => void {
  const router = useRouter();
  return () => goBackOr(router, fallback);
}
