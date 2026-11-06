import { afterEach, describe, expect, it, vi } from 'vitest';
import { fetchDownloadContent, startBrowserDownload } from './index';

afterEach(() => vi.restoreAllMocks());
describe('download transport', () => {
  it('hands a same-origin URL to the browser without fetching or buffering', () => {
    let href = '';
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      href = this.href;
    });
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    startBrowserDownload('/api/download-tickets/ticket');
    expect(click).toHaveBeenCalledOnce();
    expect(href).toBe(`${window.location.origin}/api/download-tickets/ticket`);
    expect(fetchSpy).not.toHaveBeenCalled();
    expect(document.querySelector('a')).toBeNull();
  });
  it.each([
    'javascript:alert(1)',
    'https://other.example/file',
    'data:text/html,test',
  ])('rejects unsafe download URL %s', (url) => {
    expect(() => startBrowserDownload(url)).toThrow();
  });
  it('fetches playback bytes without credentials and passes cancellation', async () => {
    const response = new Response('content');
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(response);
    const controller = new AbortController();
    expect(await fetchDownloadContent('/delivery/content/grant', controller.signal)).toBe(response);
    expect(fetchSpy).toHaveBeenCalledWith('/delivery/content/grant', {
      method: 'GET',
      cache: 'no-store',
      credentials: 'omit',
      signal: controller.signal,
    });
  });
});
