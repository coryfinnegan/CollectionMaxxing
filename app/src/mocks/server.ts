// Node server for Jest (jest.setup.ts starts it).
import { setupServer } from 'msw/node';
import { handlers } from './handlers';

export const server = setupServer(...handlers);
