import { client } from '@romd/admin-api-client';
import type { PDFDocumentLoadingTask, PDFDocumentProxy, PDFPageProxy } from 'pdfjs-dist';
import { getAuthToken } from '../../api/client';

export type ManualDocument = Pick<PDFDocumentProxy, 'numPages' | 'getData'> & {
  getPage: (page: number) => Promise<ManualPage>;
};
export type ManualPage = Pick<PDFPageProxy, 'getViewport' | 'render' | 'cleanup'>;

/** Where pdf.js finds its decoders, character maps and standard fonts; see web/config/pdfjsAssets.ts. */
const assets = '/pdfjs/';

/**
 * The manual's PDF is the media item's alternate representation on the authenticated admin API; the anonymous
 * image lane never serves documents.
 */
export function manualUrl(titleId: string, mediaId: string) {
  return `${client.getConfig().baseUrl ?? ''}/api/titles/${encodeURIComponent(titleId)}/media/${encodeURIComponent(mediaId)}`;
}

/**
 * Opens a manual with pdf.js, loaded on first use so the library does not carry it. pdf.js reads the document with
 * range requests, so a cover preview fetches only what its first page needs. Scripting, XFA forms and eval-based
 * font compilation stay off: a manual is only ever displayed.
 */
export async function openManual(
  titleId: string,
  mediaId: string,
  {
    preview = false,
  }: {
    preview?: boolean;
  } = {},
): Promise<{
  promise: Promise<ManualDocument>;
  cancel: () => void;
}> {
  const [pdfjs, worker] = await Promise.all([
    import('pdfjs-dist'),
    import('pdfjs-dist/build/pdf.worker.min.mjs?url'),
  ]);
  pdfjs.GlobalWorkerOptions.workerSrc = worker.default;
  const token = getAuthToken();
  const task: PDFDocumentLoadingTask = pdfjs.getDocument({
    url: manualUrl(titleId, mediaId),
    httpHeaders: {
      Accept: 'application/pdf',
      ...(token
        ? {
            Authorization: `Bearer ${token}`,
          }
        : {}),
    },
    withCredentials: false,
    disableAutoFetch: preview,
    enableXfa: false,
    wasmUrl: `${assets}wasm/`,
    cMapUrl: `${assets}cmaps/`,
    standardFontDataUrl: `${assets}standard_fonts/`,
  });
  return {
    promise: task.promise,
    cancel: () => void task.destroy(),
  };
}

/** The pages shown together: the cover alone, then facing pairs, as a printed booklet opens. */
export function spreadPages(page: number, pageCount: number): number[] {
  if (page <= 1 || pageCount <= 1)
    return [
      1,
    ];
  const left = page % 2 === 0 ? page : page - 1;
  return left + 1 <= pageCount
    ? [
        left,
        left + 1,
      ]
    : [
        left,
      ];
}

/** The first page of the next or previous view, in single-page or spread mode. */
export function stepPage(page: number, pageCount: number, spread: boolean, direction: 1 | -1) {
  if (!spread) return Math.min(pageCount, Math.max(1, page + direction));
  const shown = spreadPages(page, pageCount);
  const next = direction > 0 ? shown[shown.length - 1] + 1 : shown[0] - 1;
  return next < 1 || next > pageCount ? shown[0] : spreadPages(next, pageCount)[0];
}
