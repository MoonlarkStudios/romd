import type { ConsumerReleaseManifestItemDto } from '@romd/consumer-api-client';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  getDownloadFileName,
  getDownloadVerificationUnavailableReason,
  parseSizeBytes,
  verifyManifestItemDownload,
} from './downloadVerification';

const helloSha256 = '2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824';

describe('verifyManifestItemDownload', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn());
    vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:verified');
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('downloads a full object and verifies byte count and SHA-256', async () => {
    const fetchMock = vi.mocked(fetch);
    fetchMock.mockResolvedValue(
      new Response(new TextEncoder().encode('hello'), {
        status: 200,
        headers: {
          'Content-Length': '5',
          'Content-Type': 'application/octet-stream',
        },
      }),
    );

    const result = await verifyManifestItemDownload(createItem());

    expect(fetchMock).toHaveBeenCalledWith(
      '/delivery/content/signed-token',
      expect.objectContaining({
        method: 'GET',
        cache: 'no-store',
        credentials: 'omit',
      }),
    );
    const init = fetchMock.mock.calls[0]?.[1] as RequestInit;
    expect(new Headers(init.headers).has('Range')).toBe(false);
    expect(result.status).toBe('verified');
    if (result.status === 'verified') {
      expect(result.file.relativePath).toBe('Nintendo/Mario.rom');
      expect(result.file.sizeBytes).toBe(5);
      expect(result.file.objectUrl).toBe('blob:verified');
    }
  });

  it('fails when the byte count does not match the manifest', async () => {
    vi.mocked(fetch).mockResolvedValue(
      new Response(new TextEncoder().encode('hello!'), {
        status: 200,
        headers: {
          'Content-Length': '6',
        },
      }),
    );

    const result = await verifyManifestItemDownload(createItem());

    expect(result.status).toBe('failed');
    if (result.status === 'failed') {
      expect(result.failure.retryRequiresManifest).toBe(true);
      expect(result.failure.message).toMatch(/size header/i);
    }
  });

  it('fails when the SHA-256 digest does not match the manifest', async () => {
    vi.mocked(fetch).mockResolvedValue(
      new Response(new TextEncoder().encode('hullo'), {
        status: 200,
        headers: {
          'Content-Length': '5',
        },
      }),
    );

    const result = await verifyManifestItemDownload(createItem());

    expect(result.status).toBe('failed');
    if (result.status === 'failed') {
      expect(result.failure.message).toMatch(/sha-256/i);
    }
  });

  it('marks unavailable manifest items without fetching a grant', async () => {
    const result = await verifyManifestItemDownload({
      relativePath: 'Nintendo/Missing.rom',
      role: 'rom',
      sizeBytes: '10',
      isAvailable: false,
      sha256: null,
      contentGrant: null,
    });

    expect(fetch).not.toHaveBeenCalled();
    expect(result.status).toBe('unavailable');
  });

  it('marks valid Int64 sizes above browser precision unavailable without fetching', async () => {
    const result = await verifyManifestItemDownload({
      ...createItem(),
      sizeBytes: '9007199254740992',
    });

    expect(fetch).not.toHaveBeenCalled();
    expect(result.status).toBe('unavailable');
    if (result.status === 'unavailable') {
      expect(result.failure.message).toMatch(/too large/i);
      expect(result.failure.retryRequiresManifest).toBe(false);
    }
  });

  it('reports when Web Crypto SHA-256 is unavailable for playback preflight', () => {
    vi.stubGlobal('crypto', {});

    expect(getDownloadVerificationUnavailableReason()).toMatch(/insecure browser context/i);
  });
});

describe('download helpers', () => {
  it('parses safe byte counts', () => {
    expect(parseSizeBytes('42')).toBe(42);
    expect(() => parseSizeBytes('-1')).toThrow(/invalid byte count/i);
  });

  it('uses the final relative path segment as the browser download name', () => {
    expect(getDownloadFileName('Nintendo/SNES/Super Metroid.sfc')).toBe('Super Metroid.sfc');
    expect(getDownloadFileName('Nintendo\\NES\\Mario.nes')).toBe('Mario.nes');
  });
});

function createItem(): ConsumerReleaseManifestItemDto {
  return {
    relativePath: 'Nintendo/Mario.rom',
    role: 'rom',
    sizeBytes: '5',
    sha256: helloSha256,
    isAvailable: true,
    contentGrant: {
      downloadUrl: '/delivery/content/signed-token',
      expiresAt: '2026-06-04T12:00:00Z',
    },
  };
}
