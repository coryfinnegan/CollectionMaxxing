// Web entry: no native module, no home screen icon. Never import './index' or anything native from here.
import { UnavailableAppIconEngine } from './UnavailableAppIconEngine';

export * from './types';
export { FakeAppIconEngine } from './FakeAppIconEngine';
export { UnavailableAppIconEngine } from './UnavailableAppIconEngine';

export const appIcon = new UnavailableAppIconEngine();
