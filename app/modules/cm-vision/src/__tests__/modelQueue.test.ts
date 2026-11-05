// Swift cannot be compiled in CI, so the queue promise is checked on the source. expo-modules-core runs every plain AsyncFunction on one
// serial queue, so the model's picture read used to wait behind the rectangle search and the text reads asked just before it.
import fs from 'node:fs';
import path from 'node:path';

const source = fs.readFileSync(path.join(__dirname, '..', '..', 'ios', 'CmVisionModule.swift'), 'utf8');

/** The text of one `AsyncFunction("name")`, from its header to the next function's header (or the end). */
function functionText(name: string): string {
  const start = source.indexOf(`AsyncFunction("${name}")`);
  expect(start).toBeGreaterThan(-1);
  const next = source.indexOf('AsyncFunction("', start + 1);
  return source.slice(start, next === -1 ? undefined : next);
}

describe('ios/CmVisionModule.swift queues', () => {
  it('dispatches the picture read and the text extraction on a queue of their own, so a Vision call queued before them never delays the model', () => {
    for (const name of ['readItems', 'extractItems']) {
      expect(functionText(name)).toMatch(/\}\.runOnQueue\(modelQueue\)\s*(\/\/[^\n]*\s*)*$/);
    }
  });

  it('defines that queue once, serial, with a label of its own', () => {
    expect(source.match(/DispatchQueue\(label:/g)).toHaveLength(1);
    expect(source).toMatch(/let modelQueue = DispatchQueue\(label: "cm-vision\.model", qos: \.userInitiated\)/);
    expect(source).not.toMatch(/\.concurrent/);
  });

  it('leaves the Vision calls and the capability check on Expo\'s own queue, so they never contend with the model', () => {
    for (const name of ['recognizeText', 'detectRectangles', 'modelCapabilities']) {
      expect(functionText(name)).not.toMatch(/runOnQueue/);
    }
    expect(source.match(/\.runOnQueue\(/g)).toHaveLength(2);
  });
});
