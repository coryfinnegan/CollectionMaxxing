import { defineConfig } from 'orval';

export default defineConfig({
  api: {
    input: '../contracts/openapi.json',
    output: {
      mode: 'tags-split',
      target: 'src/api/generated/endpoints.ts',
      schemas: 'src/api/generated/models',
      client: 'react-query',
      httpClient: 'fetch',
      mock: { generators: [{ type: 'msw', delay: 0 }] },
      clean: true,
      override: {
        mutator: { path: 'src/lib/http.ts', name: 'customFetch' },
      },
    },
  },
});
