// The recognition config: built-in defaults, refreshed from the server, cached on the phone, used offline.
// The app never waits on it and a bad response never replaces a good config.
import type { KeyValueStore } from '@/features/scan/tray/trayStore';
import {
  DEFAULT_LOCAL_TUNING,
  DEFAULT_RECOGNITION_CONFIG,
  type RecognitionConfig,
  type RecognitionSettings,
} from './defaults';

export type ConfigFetchResult = { status: 'ok'; etag?: string; body: unknown } | { status: 'notModified' };
/** Asks the server for the config; `etag` goes out as If-None-Match. Throws when offline or on an error status. */
export type ConfigFetcher = (etag?: string) => Promise<ConfigFetchResult>;

const STORAGE_KEY = 'recognition-config-v1';

type Obj = Record<string, unknown>;
const isObj = (v: unknown): v is Obj => typeof v === 'object' && v !== null && !Array.isArray(v);
const text = (v: unknown): v is string => typeof v === 'string' && v.trim() !== '';
const positive = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v) && v > 0;
const allPositive = (o: unknown, keys: string[]) => isObj(o) && keys.every((k) => positive(o[k]));

/** The server-owned config, or undefined when anything in it is missing or does not make sense. */
export function parseConfig(body: unknown): RecognitionConfig | undefined {
  if (!isObj(body) || !text(body.version)) return undefined;
  const { prompt, autoSnap, engine, splitting } = body;
  if (!isObj(prompt) || !text(prompt.template) || !text(prompt.outputRules)) return undefined;
  if (!allPositive(autoSnap, Object.keys(DEFAULT_RECOGNITION_CONFIG.autoSnap))) return undefined;
  if (!allPositive(engine, Object.keys(DEFAULT_RECOGNITION_CONFIG.engine))) return undefined;
  if (!allPositive(splitting, Object.keys(DEFAULT_RECOGNITION_CONFIG.splitting))) return undefined;
  // A server from before a section sends none: the built-in value stands. A bad one refuses the whole config.
  let { fingerprints, extractionPrompt } = body;
  if (fingerprints === undefined) fingerprints = DEFAULT_RECOGNITION_CONFIG.fingerprints;
  else if (!allPositive(fingerprints, Object.keys(DEFAULT_RECOGNITION_CONFIG.fingerprints))) return undefined;
  if (extractionPrompt === undefined) extractionPrompt = DEFAULT_RECOGNITION_CONFIG.extractionPrompt;
  else if (!isObj(extractionPrompt) || !text(extractionPrompt.template) || !text(extractionPrompt.outputRules)) return undefined;
  return { ...(body as unknown as RecognitionConfig), fingerprints, extractionPrompt } as RecognitionConfig;
}

interface Cached {
  etag?: string;
  config: RecognitionConfig;
}

export type RefreshOutcome = 'updated' | 'unchanged' | 'kept';

export class RecognitionConfigStore {
  private cached: Cached | undefined;
  private inFlight: Promise<RefreshOutcome> | undefined;

  constructor(private readonly deps: { storage: KeyValueStore; fetcher: ConfigFetcher }) {
    this.cached = this.load();
  }

  current(): RecognitionSettings {
    return { ...(this.cached?.config ?? DEFAULT_RECOGNITION_CONFIG), local: DEFAULT_LOCAL_TUNING };
  }

  /** Never throws. Overlapping calls share one request. */
  refresh(): Promise<RefreshOutcome> {
    this.inFlight ??= this.fetchOnce().finally(() => {
      this.inFlight = undefined;
    });
    return this.inFlight;
  }

  private async fetchOnce(): Promise<RefreshOutcome> {
    try {
      const result = await this.deps.fetcher(this.cached?.etag);
      if (result.status === 'notModified') return 'unchanged';
      const config = parseConfig(result.body);
      if (!config) return 'kept';
      this.cached = { etag: result.etag, config };
      this.save();
      return 'updated';
    } catch {
      return 'kept';
    }
  }

  private load(): Cached | undefined {
    try {
      const raw = this.deps.storage.getString(STORAGE_KEY);
      if (!raw) return undefined;
      const stored: unknown = JSON.parse(raw);
      if (!isObj(stored)) return undefined;
      const config = parseConfig(stored.config);
      return config ? { etag: typeof stored.etag === 'string' ? stored.etag : undefined, config } : undefined;
    } catch {
      return undefined;
    }
  }

  private save(): void {
    try {
      if (this.cached) this.deps.storage.set(STORAGE_KEY, JSON.stringify(this.cached));
    } catch {
      // The cache is a convenience; the in-memory config still works.
    }
  }
}
