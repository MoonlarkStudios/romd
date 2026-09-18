import { defineConfig } from '@hey-api/openapi-ts';

export default defineConfig({
  input: '../../schemas/admin-v1.json',
  output: {
    path: './src/generated',
    format: 'prettier',
    lint: 'biome',
  },
  plugins: [
    '@hey-api/typescript',
    '@hey-api/client-fetch',
    {
      name: '@hey-api/sdk',
      asClass: false,
      operationId: true,
    },
  ],
  types: {
    enums: 'javascript',
  },
});
