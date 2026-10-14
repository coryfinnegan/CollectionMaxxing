// The app logger. Secrets (pairing codes, tokens) must never be passed to it; tests capture it to prove that.

export const logger = {
  warn: (message: string) => console.warn(`[cm] ${message}`),
  error: (message: string) => console.error(`[cm] ${message}`),
};
