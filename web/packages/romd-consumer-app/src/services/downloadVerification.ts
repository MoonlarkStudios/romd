import type { ConsumerReleaseManifestItemDto } from '@romd/consumer-api-client';

export type DownloadVerificationStatus =
  | 'idle'
  | 'downloading'
  | 'verified'
  | 'failed'
  | 'unavailable';

export interface VerifiedDownload {
  relativePath: string;
  sizeBytes: number;
  sha256: string;
  blob: Blob;
  objectUrl: string;
  contentType: string;
}

export interface DownloadFailure {
  message: string;
  retryRequiresManifest: boolean;
}

export type DownloadVerificationResult =
  | {
      status: 'verified';
      file: VerifiedDownload;
    }
  | {
      status: 'failed';
      failure: DownloadFailure;
    }
  | {
      status: 'unavailable';
      failure: DownloadFailure;
    };

export function parseSizeBytes(value: string): number {
  const size = Number(value);
  if (!Number.isSafeInteger(size) || size < 0) {
    throw new Error(`Invalid byte count: ${value}`);
  }

  return size;
}

export function getDownloadFileName(relativePath: string): string {
  const normalized = relativePath.replaceAll('\\', '/');
  const segments = normalized.split('/').filter(Boolean);

  return segments.at(-1) ?? 'download.bin';
}

export function saveVerifiedDownload(file: VerifiedDownload): void {
  const link = document.createElement('a');
  link.href = file.objectUrl;
  link.download = getDownloadFileName(file.relativePath);
  link.rel = 'noopener';
  document.body.append(link);
  link.click();
  link.remove();
}

export function revokeVerifiedDownload(file: VerifiedDownload): void {
  URL.revokeObjectURL(file.objectUrl);
}

export async function verifyManifestItemDownload(
  item: ConsumerReleaseManifestItemDto,
  signal?: AbortSignal,
  onPhase?: (phase: 'downloading' | 'checking') => void,
): Promise<DownloadVerificationResult> {
  if (!item.isAvailable || !item.contentGrant || !item.sha256) {
    return {
      status: 'unavailable',
      failure: {
        message: 'This manifest item is not available for download.',
        retryRequiresManifest: false,
      },
    };
  }

  let expectedSize: number;
  try {
    expectedSize = parseSizeBytes(item.sizeBytes);
  } catch {
    return {
      status: 'unavailable',
      failure: {
        message: 'This file is too large for full-object browser download verification.',
        retryRequiresManifest: false,
      },
    };
  }

  try {
    onPhase?.('downloading');
    const response = await fetch(item.contentGrant.downloadUrl, {
      method: 'GET',
      cache: 'no-store',
      credentials: 'omit',
      signal,
    });

    if (!response.ok || response.status !== 200) {
      return failedGrant(`Download grant returned ${response.status}.`);
    }

    const contentLength = response.headers.get('Content-Length');
    if (contentLength !== null && parseSizeBytes(contentLength) !== expectedSize) {
      return failedGrant('Download size header does not match the manifest.');
    }

    const bytes = await response.arrayBuffer();
    if (bytes.byteLength !== expectedSize) {
      return failedGrant('Downloaded byte count does not match the manifest.');
    }

    onPhase?.('checking');
    const actualSha256 = await computeSha256Hex(bytes);
    if (actualSha256.toLowerCase() !== item.sha256.toLowerCase()) {
      return failedGrant('Downloaded SHA-256 digest does not match the manifest.');
    }

    const contentType = response.headers.get('Content-Type') ?? 'application/octet-stream';
    const blob = new Blob([bytes], {
      type: contentType,
    });

    return {
      status: 'verified',
      file: {
        relativePath: item.relativePath,
        sizeBytes: expectedSize,
        sha256: actualSha256,
        blob,
        objectUrl: URL.createObjectURL(blob),
        contentType,
      },
    };
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      return {
        status: 'failed',
        failure: {
          message: 'Download was cancelled.',
          retryRequiresManifest: false,
        },
      };
    }

    return failedGrant('Download failed. Request a fresh manifest and try again.');
  }
}

export function getDownloadVerificationUnavailableReason(): string | null {
  if (!globalThis.crypto?.subtle) {
    return 'Browser playback requires SHA-256 verification, which is unavailable in this insecure browser context.';
  }
  return null;
}

async function computeSha256Hex(bytes: ArrayBuffer): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', bytes);
  return Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, '0')).join('');
}

function failedGrant(message: string): DownloadVerificationResult {
  return {
    status: 'failed',
    failure: {
      message,
      retryRequiresManifest: true,
    },
  };
}
