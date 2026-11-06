import { describe, expect, it } from 'vitest';
import { buildClientParams as adminParams } from '../../../romd-admin-api-client/src/generated/core/params.gen';
import { buildClientParams as consumerParams } from '../../../romd-consumer-api-client/src/generated/core/params.gen';

describe.each([
  [
    'admin',
    adminParams,
  ],
  [
    'consumer',
    consumerParams,
  ],
] as const)('%s generated client', (_name, buildClientParams) => {
  it('does not inherit attacker properties from a prefixed prototype key', () => {
    const params = buildClientParams(
      [
        {
          q: 'search',
          $query___proto__: {
            isAdmin: true,
          },
        },
      ],
      [
        {
          args: [
            {
              in: 'query',
              key: 'q',
            },
          ],
        },
      ],
    );

    expect(params.query.q).toBe('search');
    expect(params.query.isAdmin).toBeUndefined();
    expect(Object.getPrototypeOf(params.query)).toBeNull();
    expect(Object.keys(params.query)).not.toContain('isAdmin');
  });
});
