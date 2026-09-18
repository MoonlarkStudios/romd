import { getAuthToken } from '../../api/client';

export interface UploadProgress {
  /** Bytes sent so far for the current request. */
  loaded: number;
  /** Total bytes of the current request body. */
  total: number;
}

export interface TransferStats {
  /** Average upload rate in bytes/second (0 until measurable). */
  bytesPerSec: number;
  /** Estimated seconds remaining (Infinity until measurable). */
  etaSeconds: number;
}

/**
 * Pure throughput/ETA from cumulative bytes and elapsed time. Uses the running
 * average rather than an instantaneous sample, which keeps the ETA stable.
 */
export function computeTransferStats(
  loaded: number,
  total: number,
  elapsedSeconds: number,
): TransferStats {
  if (elapsedSeconds <= 0 || loaded <= 0) {
    return { bytesPerSec: 0, etaSeconds: Infinity };
  }
  const bytesPerSec = loaded / elapsedSeconds;
  const remaining = Math.max(0, total - loaded);
  return { bytesPerSec, etaSeconds: bytesPerSec > 0 ? remaining / bytesPerSec : Infinity };
}

export interface UploadWithProgressOptions {
  /** Query parameters appended to the URL (undefined values are skipped). */
  query?: Record<string, string | undefined>;
  /** Aborts the in-flight upload. */
  signal?: AbortSignal;
  /** Called as bytes are sent. */
  onProgress?: (progress: UploadProgress) => void;
}

/**
 * Uploads a single file as multipart/form-data via XMLHttpRequest so the caller
 * gets real transfer progress (fetch cannot report upload progress). Mirrors the
 * generated client's auth (Bearer token) and same-origin base URL.
 */
export function uploadWithProgress<T>(
  path: string,
  file: File,
  options: UploadWithProgressOptions = {},
): Promise<T> {
  const { query, signal, onProgress } = options;

  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query ?? {})) {
    if (value != null) params.set(key, value);
  }
  const queryString = params.toString();
  const url = queryString ? `${path}?${queryString}` : path;

  return new Promise<T>((resolve, reject) => {
    if (signal?.aborted) {
      reject(new DOMException('Upload aborted', 'AbortError'));
      return;
    }

    const xhr = new XMLHttpRequest();
    xhr.open('POST', url);
    xhr.responseType = 'json';
    // Let the browser set Content-Type (with the multipart boundary) from the FormData body.
    const token = getAuthToken();
    if (token) xhr.setRequestHeader('Authorization', `Bearer ${token}`);

    xhr.upload.onprogress = (event) => {
      if (event.lengthComputable) onProgress?.({ loaded: event.loaded, total: event.total });
    };

    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        resolve(xhr.response as T);
      } else {
        const detail = xhr.response?.detail;
        reject(new Error(typeof detail === 'string' ? detail : `Upload failed (${xhr.status}).`));
      }
    };
    xhr.onerror = () => reject(new Error('Upload failed (network error)'));
    xhr.onabort = () => reject(new DOMException('Upload aborted', 'AbortError'));

    if (signal) {
      signal.addEventListener('abort', () => xhr.abort(), { once: true });
    }

    const form = new FormData();
    form.append('file', file, file.name);
    xhr.send(form);
  });
}
