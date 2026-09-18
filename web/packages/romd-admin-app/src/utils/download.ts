import { getAuthToken } from '../api/client';

/**
 * Downloads a file from an authenticated API endpoint.
 * Uses fetch with the Bearer token instead of window.open,
 * which cannot send authorization headers.
 */
export async function authenticatedDownload(url: string, fallbackFilename: string): Promise<void> {
  const token = getAuthToken();
  const response = await fetch(url, {
    headers: token ? { Authorization: `Bearer ${token}` } : {},
  });

  if (!response.ok) {
    throw new Error(`Download failed (${response.status})`);
  }

  const contentType = response.headers.get('Content-Type')?.toLowerCase() ?? '';
  if (contentType.includes('text/html')) {
    throw new Error('Download returned an HTML page instead of a file.');
  }

  const blob = await response.blob();
  const blobUrl = URL.createObjectURL(blob);

  // Extract filename from Content-Disposition header if available
  const disposition = response.headers.get('Content-Disposition');
  const filenameMatch = disposition?.match(/filename\*?=(?:UTF-8''|"?)([^";]+)/i);
  const filename = filenameMatch ? decodeURIComponent(filenameMatch[1]) : fallbackFilename;

  const a = document.createElement('a');
  a.href = blobUrl;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(blobUrl);
}
