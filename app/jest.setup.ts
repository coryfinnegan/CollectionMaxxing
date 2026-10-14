import { configure } from '@testing-library/react-native';
import { resetMockData } from '@/mocks/data';
import { server } from '@/mocks/server';

// findBy/waitFor default to 1s and Jest to 5s; a cold, loaded machine exceeds both on a screen's first render (#53).
configure({ asyncUtilTimeout: 8000 });
jest.setTimeout(30_000);

process.env.EXPO_PUBLIC_API_URL = 'http://api.test';

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
beforeEach(() => resetMockData());
afterEach(() => server.resetHandlers());
afterAll(() => server.close());
