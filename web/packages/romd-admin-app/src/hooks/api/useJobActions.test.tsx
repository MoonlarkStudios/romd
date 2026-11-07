import { client, type Job } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import { HttpResponse, http } from 'msw';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { subjectReconciliationJob } from '../../test/fixtures/mediaReconciliationJobs';
import { server } from '../../test/msw/server';
import { useRetryJob } from './useJobActions';
import { jobKeys } from './useJobs';

function setup() {
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
      mutations: {
        retry: false,
      },
    },
  });
  cache.setQueryData(jobKeys.list(), []);
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={cache}>{children}</QueryClientProvider>
  );
  return {
    ...renderHook(() => useRetryJob(), {
      wrapper,
    }),
    cache,
  };
}
beforeEach(() =>
  client.setConfig({
    baseUrl: 'http://localhost',
  }),
);
afterEach(() =>
  client.setConfig({
    baseUrl: '',
  }),
);

describe('typed job retries', () => {
  it('retains the exact UUID after a failed response and follows only the returned job reference', async () => {
    const bodies: unknown[] = [];
    const id = '11111111-2222-4333-8444-555555555555';
    server.use(
      http.post('http://localhost/api/media/reconciliation/:jobId/retry', async ({ request }) => {
        bodies.push(await request.json());
        if (bodies.length === 1)
          return HttpResponse.json(
            {
              detail: 'Response unavailable.',
            },
            {
              status: 503,
            },
          );
        return HttpResponse.json(
          {
            jobId: id,
          },
          {
            status: 202,
            headers: {
              Location: 'https://unrelated.example/ignored',
            },
          },
        );
      }),
    );
    const { result } = setup();
    const job = subjectReconciliationJob({
      phase: 'Failed',
      isTerminal: true,
    });
    await act(async () => {
      await expect(result.current.mutateAsync(job)).rejects.toThrow('Response unavailable.');
    });
    expect(bodies).toHaveLength(1);
    await act(async () => {
      expect(await result.current.mutateAsync(job)).toEqual({
        kind: 'media',
        jobId: id,
      });
    });
    expect(bodies[0]).toEqual({
      requestId: expect.any(String),
    });
    expect(bodies[1]).toEqual(bodies[0]);
    await act(async () => {
      await result.current.mutateAsync(job);
    });
    expect(bodies[2]).not.toEqual(bodies[0]);
  });
  it('keeps ordinary failed-item retries on their own route', async () => {
    let calls = 0;
    server.use(
      http.post('http://localhost/api/jobs/:jobId/retry-failed', () => {
        calls++;
        return HttpResponse.json({
          requeued: 2,
        });
      }),
    );
    const { result } = setup();
    const job: Job = {
      jobType: 'bulk_enrichment',
      id: 'ordinary',
      correlationId: 'ordinary',
      sourceFilename: 'Titles',
      phase: 'Failed',
      progressPercent: 100,
      errors: [],
      isTerminal: true,
    };
    await act(async () => {
      expect(await result.current.mutateAsync(job)).toEqual({
        kind: 'items',
        requeued: 2,
      });
    });
    expect(calls).toBe(1);
  });
  it.each([
    {
      detail: 17,
    },
    'opaque failure',
    {},
  ])('does not invent server details from an opaque error: %j', async (error) => {
    server.use(
      http.post('http://localhost/api/media/reconciliation/:jobId/retry', () =>
        HttpResponse.json(error, {
          status: 409,
        }),
      ),
    );
    const { result } = setup();
    await act(async () => {
      await expect(
        result.current.mutateAsync(
          subjectReconciliationJob({
            phase: 'Failed',
            isTerminal: true,
          }),
        ),
      ).rejects.toThrow('Could not retry media reconciliation.');
    });
  });
});
