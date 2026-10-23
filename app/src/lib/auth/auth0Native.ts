// Web never uses the native sign-in (it signs in through the server); Metro picks ./auth0Native.native.ts on iOS.
import { type NativeAuth, UnavailableNativeAuth } from './native';

export const createAuth0Native = (): NativeAuth => new UnavailableNativeAuth();
