import { expect, it } from 'vitest';
import { observedProviderSettings } from './providerQueries';

it.each([
  undefined,
  new Response(),
  new Response(null, {
    headers: {
      ETag: 'W/"observed"',
    },
  }),
  new Response(null, {
    headers: {
      ETag: '"other-writer"',
    },
  }),
])('rejects settings without their matching strong response validator (%#)', (response) => {
  expect(() =>
    observedProviderSettings(
      {
        revision: 'observed',
      },
      response,
    ),
  ).toThrow('Reload settings to read their current revision.');
});

it('accepts only a revision observed in the response validator', () => {
  const settings = {
    revision: 'observed',
  };
  expect(
    observedProviderSettings(
      settings,
      new Response(null, {
        headers: {
          ETag: '"observed"',
        },
      }),
    ),
  ).toBe(settings);
});
