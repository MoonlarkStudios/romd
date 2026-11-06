import { defineConfig } from '@hey-api/openapi-ts';

export default defineConfig({
  input: '../../schemas/admin-v1.json',
  output: {
    path: './src/generated',
    // Generated artifacts use the generator's printer; workspace Biome checks handwritten code.
  },
  plugins: [
    '@hey-api/typescript',
    '@hey-api/client-fetch',
    {
      name: '@hey-api/sdk',
      operations: {
        strategy: 'flat',
        nesting: 'operationId',
      },
    },
  ],
  types: {
    enums: 'javascript',
  },
});
