import { resetMockData } from '@/mocks/data';
import { server } from '@/mocks/server';

process.env.EXPO_PUBLIC_API_URL = 'http://api.test';

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
beforeEach(() => resetMockData());
afterEach(() => server.resetHandlers());
afterAll(() => server.close());
