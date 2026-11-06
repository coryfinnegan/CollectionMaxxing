// Swift cannot be compiled in CI, so the promise is checked on the source: the build number the eval run reports is the app bundle's own.
import fs from 'node:fs';
import path from 'node:path';

const ios = path.join(__dirname, '..', '..', 'ios');
const moduleSource = fs.readFileSync(path.join(ios, 'CmVisionModule.swift'), 'utf8');
const contractSource = fs.readFileSync(path.join(ios, 'CmVisionContract.swift'), 'utf8');

describe('ios deviceStatus', () => {
  it('reads the build number from the bundle\'s CFBundleVersion', () => {
    const start = moduleSource.indexOf('AsyncFunction("deviceStatus")');
    const end = moduleSource.indexOf('AsyncFunction("detectRectangles")');
    expect(start).toBeGreaterThan(-1);
    expect(end).toBeGreaterThan(start);
    expect(moduleSource.slice(start, end)).toMatch(/buildNumber: Bundle\.main\.object\(forInfoDictionaryKey: "CFBundleVersion"\) as\? String/);
  });

  it('sends the build number only when there is one', () => {
    expect(contractSource).toMatch(/if let buildNumber = buildNumber, !buildNumber\.isEmpty \{ status\["buildNumber"\] = buildNumber \}/);
  });
});
