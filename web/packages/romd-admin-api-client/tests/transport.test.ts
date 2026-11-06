import { describe, expect, it, vi } from 'vitest';
import {
  createClient as createConsumerClient,
  getConsumerTitle,
} from '../../romd-consumer-api-client/src/index';
import { formDataBodySerializer } from '../src/generated/core/bodySerializer.gen';
import { createClient, updateTitle } from '../src/index';

describe('generated client transport contracts', () => {
  it('preserves numeric metadata, preconditions, authorization interceptors, and response envelopes', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(
      Response.json({
        id: 'title-1',
        rating: 8.5,
      }),
    );
    const client = createClient({
      baseUrl: 'https://romd.test',
      fetch,
    });
    client.interceptors.request.use((request) => {
      request.headers.set('Authorization', 'Bearer test-token');
      return request;
    });

    const result = await updateTitle({
      client,
      path: {
        titleId: 'title-1',
      },
      headers: {
        'If-Match': '"rev-1"',
      },
      body: {
        players: 2,
        rating: 8.5,
        description: null,
      },
    });

    const request = fetch.mock.calls[0]?.[0] as Request;
    expect(request.url).toBe('https://romd.test/api/titles/title-1');
    expect(request.method).toBe('PATCH');
    expect(request.headers.get('If-Match')).toBe('"rev-1"');
    expect(request.headers.get('Authorization')).toBe('Bearer test-token');
    expect(await request.json()).toEqual({
      players: 2,
      rating: 8.5,
      description: null,
    });
    expect(result.data).toEqual({
      id: 'title-1',
      rating: 8.5,
    });
    expect(result.error).toBeUndefined();
    expect(result.response.status).toBe(200);
  });

  it('retains consumer error envelopes and response interceptors', async () => {
    const problem = {
      title: 'Access denied',
      status: 403,
    };
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(
      Response.json(problem, {
        status: 403,
      }),
    );
    const client = createConsumerClient({
      baseUrl: 'https://romd.test',
      fetch,
    });
    const intercepted = vi.fn((response: Response) => response);
    client.interceptors.response.use(intercepted);

    const result = await getConsumerTitle({
      client,
      path: {
        titleId: 'title-1',
      },
    });

    expect(result.error).toEqual(problem);
    expect(result.data).toBeUndefined();
    expect(result.response.status).toBe(403);
    expect(intercepted).toHaveBeenCalledOnce();
  });

  it('keeps uploaded files binary and serializes multipart scalar fields', () => {
    const file = new File(
      [
        'rom-data',
      ],
      'game.rom',
      {
        type: 'application/octet-stream',
      },
    );

    const body = formDataBodySerializer.bodySerializer({
      file,
      count: 2,
      enabled: false,
      omitted: null,
    });

    expect(body.get('file')).toBe(file);
    expect(body.get('count')).toBe('2');
    expect(body.get('enabled')).toBe('false');
    expect(body.has('omitted')).toBe(false);
  });
});
