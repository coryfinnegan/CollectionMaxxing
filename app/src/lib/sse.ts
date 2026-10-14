// A small server-sent-events client built on fetch, because React Native has no EventSource.
// - It reconnects after a drop and sends `Last-Event-ID`, so the server replays only what was missed.
// - After `maxFailures` failed connections in a row it switches to polling (`poll`), which is what the
//   session screen uses to call GET /scan-sessions/{id}?since=.
// - It never delivers the same event id twice, so a replay after a reconnect cannot duplicate anything.

export type SseMessage = { event: string; id?: string; data: string };
export type SseMode = 'connecting' | 'streaming' | 'polling';

export type SseOptions = {
  url: string;
  /** Extra headers per connection (the bearer token, which can change between reconnects). */
  headers?: () => Promise<Record<string, string>> | Record<string, string>;
  lastEventId?: string;
  fetchImpl?: typeof fetch;
  onMessage(message: SseMessage): void;
  onMode?(mode: SseMode): void;
  /** Returns what happened after `lastEventId`; used when streaming keeps failing. */
  poll?(lastEventId: string | undefined): Promise<SseMessage[]>;
  maxFailures?: number;
  retryMs?: number;
  pollMs?: number;
};

/** Splits a text chunk stream into messages. Exported for tests. */
export function createSseParser(onMessage: (m: SseMessage) => void) {
  let buffer = '';
  return {
    push(chunk: string) {
      buffer += chunk;
      buffer = buffer.replace(/\r\n?/g, '\n');
      let end = buffer.indexOf('\n\n');
      while (end !== -1) {
        const block = buffer.slice(0, end);
        buffer = buffer.slice(end + 2);
        const message = parseBlock(block);
        if (message) onMessage(message);
        end = buffer.indexOf('\n\n');
      }
    },
  };
}

function parseBlock(block: string): SseMessage | undefined {
  let event = 'message';
  let id: string | undefined;
  const data: string[] = [];
  for (const line of block.split('\n')) {
    if (!line || line.startsWith(':')) continue;
    const colon = line.indexOf(':');
    const field = colon === -1 ? line : line.slice(0, colon);
    const value = colon === -1 ? '' : line.slice(colon + 1).replace(/^ /, '');
    if (field === 'event') event = value;
    else if (field === 'id') id = value;
    else if (field === 'data') data.push(value);
  }
  return data.length === 0 && id === undefined ? undefined : { event, id, data: data.join('\n') };
}

const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

export function connectSse(options: SseOptions): { close(): void } {
  const { url, maxFailures = 3, retryMs = 1000, pollMs = 3000 } = options;
  const doFetch = options.fetchImpl ?? ((input, init) => fetch(input, init));
  let closed = false;
  let lastEventId = options.lastEventId;
  let failures = 0;
  const controller = new AbortController();
  const delivered = new Set<string>();

  const deliver = (message: SseMessage) => {
    if (message.id !== undefined) {
      if (delivered.has(message.id)) return; // a replay after reconnect
      delivered.add(message.id);
      lastEventId = message.id;
    }
    options.onMessage(message);
  };

  const readOnce = async () => {
    options.onMode?.('connecting');
    const response = await doFetch(url, {
      method: 'GET',
      signal: controller.signal,
      headers: {
        Accept: 'text/event-stream',
        ...(lastEventId !== undefined ? { 'Last-Event-ID': lastEventId } : {}),
        ...(await options.headers?.()),
      },
    });
    if (!response.ok) throw new Error(`SSE connect failed (${response.status})`);
    failures = 0;
    options.onMode?.('streaming');
    const parser = createSseParser(deliver);
    const reader = response.body?.getReader?.();
    if (!reader) {
      parser.push(await response.text()); // a runtime without streaming bodies still gets what the server sent
      return;
    }
    const decoder = new TextDecoder();
    for (;;) {
      const { done, value } = await reader.read();
      if (done || closed) return;
      parser.push(typeof value === 'string' ? value : decoder.decode(value, { stream: true }));
    }
  };

  const run = async () => {
    while (!closed) {
      if (options.poll && failures >= maxFailures) {
        options.onMode?.('polling');
        try {
          for (const message of await options.poll(lastEventId)) deliver(message);
        } catch {
          // keep polling; the next round may work
        }
        await sleep(pollMs);
        continue;
      }
      try {
        await readOnce();
      } catch {
        if (closed) return;
        failures += 1;
      }
      if (!closed) await sleep(retryMs);
    }
  };
  void run();

  return {
    close() {
      closed = true;
      controller.abort();
    },
  };
}
