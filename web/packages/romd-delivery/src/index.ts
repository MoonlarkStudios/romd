/** Initiates a browser-managed save. Resolution means handoff, never completion.
 * Authorization belongs to the caller: only pass a fresh ticket/grant URL. */
export function startBrowserDownload(downloadUrl: string, filename?: string): void {
  const url = new URL(downloadUrl, window.location.href);
  if (
    ![
      'http:',
      'https:',
    ].includes(url.protocol) ||
    url.origin !== window.location.origin ||
    url.username ||
    url.password
  ) {
    throw new Error('Download URL must use the application origin.');
  }
  const link = document.createElement('a');
  link.href = url.href;
  link.download = filename ?? '';
  link.rel = 'noopener noreferrer';
  // Keep a failed/expired download from replacing the SPA. The browser handles
  // Content-Disposition attachments; error responses open in a separate tab.
  link.target = '_blank';
  link.referrerPolicy = 'no-referrer';
  document.body.append(link);
  link.click();
  link.remove();
}

/** Byte-fetching transport for playback/verification. Never sends application credentials. */
export function fetchDownloadContent(downloadUrl: string, signal?: AbortSignal): Promise<Response> {
  return fetch(downloadUrl, {
    method: 'GET',
    cache: 'no-store',
    credentials: 'omit',
    signal,
  });
}
