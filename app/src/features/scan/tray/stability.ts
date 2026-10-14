// Stability filter: a code only counts once the camera has seen it in `required` of the last `window` frames,
// so a one-frame misread never reaches the tray. Each stable code is reported once; it can be reported again
// only after it has left the view for a whole window.

export type StabilityFilter = { observe(codes: string[]): string[] };

export function createStabilityFilter({ required = 2, window = 4 }: { required?: number; window?: number } = {}): StabilityFilter {
  const frames: Set<string>[] = [];
  const reported = new Set<string>();
  return {
    observe(codes) {
      frames.push(new Set(codes));
      if (frames.length > window) frames.shift();
      const stable: string[] = [];
      const seenAnywhere = new Set<string>();
      for (const frame of frames) for (const code of frame) seenAnywhere.add(code);
      for (const code of reported) if (!seenAnywhere.has(code)) reported.delete(code);
      for (const code of new Set(codes)) {
        if (reported.has(code)) continue;
        const count = frames.reduce((n, f) => n + (f.has(code) ? 1 : 0), 0);
        if (count >= required) {
          reported.add(code);
          stable.push(code);
        }
      }
      return stable;
    },
  };
}
