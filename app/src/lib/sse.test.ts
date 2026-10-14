import { connectSse, createSseParser, type SseMessage, type SseMode } from './sse';

const frame = (id: number, data: object, event = 'candidate.added') =>
  `event: ${event}\nid: ${id}\ndata: ${JSON.stringify(data)}\n\n`;

/** A response whose body yields the given chunks, then ends (a dropped connection). */
const streamResponse = (chunks: string[]) => {
  const encoder = new TextEncoder();
  let i = 0;
  return {
    ok: true,
    status: 200,
    body: {
      getReader: () => ({
        read: async () => (i < chunks.length ? { done: false, value: encoder.encode(chunks[i++]) } : { done: true, value: undefined }),
      }),
    },
    text: async () => chunks.join(''),
  } as unknown as Response;
};

const failResponse = (status = 503) => ({ ok: false, status }) as Response;

const until = async (check: () => boolean) => {
  for (let i = 0; i < 400 && !check(); i++) await new Promise((r) => setTimeout(r, 5));
  expect(check()).toBe(true);
};

describe('sse parser', () => {
  it('reads events split across chunks, with ids and multi-line data', () => {
    const seen: SseMessage[] = [];
    const parser = createSseParser((m) => seen.push(m));
    parser.push('event: candidate.added\nid: 7\nda');
    parser.push('ta: {"a":\ndata: 1}\n\n: keep-alive\n\n');
    expect(seen).toEqual([{ event: 'candidate.added', id: '7', data: '{"a":\n1}' }]);
  });
});

describe('sse client', () => {
  it('resumes with Last-Event-ID after the stream drops and never delivers an id twice', async () => {
    const calls: { lastEventId: string | undefined }[] = [];
    const responses = [
      streamResponse([frame(1, { n: 1 }), frame(2, { n: 2 })]),
      // The server replays event 2 along with the one that was missed.
      streamResponse([frame(2, { n: 2 }), frame(3, { n: 3 })]),
    ];
    const received: string[] = [];
    const fetchImpl = (async (_url: string, init: RequestInit) => {
      calls.push({ lastEventId: (init.headers as Record<string, string>)['Last-Event-ID'] });
      return responses.shift() ?? streamResponse([]);
    }) as unknown as typeof fetch;

    const sse = connectSse({ url: 'http://api.test/events', fetchImpl, retryMs: 1, onMessage: (m) => received.push(m.id ?? '') });
    await until(() => received.length >= 3);
    sse.close();

    expect(received).toEqual(['1', '2', '3']);
    expect(calls[0]?.lastEventId).toBeUndefined();
    expect(calls[1]?.lastEventId).toBe('2');
  });

  it('falls back to polling after repeated failures and keeps going from the last id', async () => {
    const modes: SseMode[] = [];
    const polled: (string | undefined)[] = [];
    const received: string[] = [];
    const sse = connectSse({
      url: 'http://api.test/events',
      fetchImpl: (async () => failResponse()) as unknown as typeof fetch,
      maxFailures: 2,
      retryMs: 1,
      pollMs: 1,
      onMode: (m) => modes.push(m),
      onMessage: (m) => received.push(m.id ?? ''),
      poll: async (last) => {
        polled.push(last);
        return last === undefined ? [{ event: 'candidate.added', id: '4', data: '{}' }] : [];
      },
    });
    await until(() => polled.length >= 2);
    sse.close();

    expect(modes).toContain('polling');
    expect(received).toEqual(['4']);
    expect(polled.slice(0, 2)).toEqual([undefined, '4']);
  });

  it('sends the auth headers it is given on every connection', async () => {
    const seen: Record<string, string>[] = [];
    const sse = connectSse({
      url: 'http://api.test/events',
      headers: () => ({ Authorization: 'Bearer test-token' }),
      fetchImpl: (async (_url: string, init: RequestInit) => {
        seen.push(init.headers as Record<string, string>);
        return streamResponse([]);
      }) as unknown as typeof fetch,
      retryMs: 1,
      onMessage: () => undefined,
    });
    await until(() => seen.length >= 2);
    sse.close();
    expect(seen.every((h) => h.Authorization === 'Bearer test-token' && h.Accept === 'text/event-stream')).toBe(true);
  });
});
