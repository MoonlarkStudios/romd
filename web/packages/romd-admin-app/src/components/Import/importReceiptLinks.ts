export const importHistoryUrl = '/jobs?type=upload';
export const serverImportUrl = '/roms/import?source=server';

export function importReceiptUrl(jobId: string, filter = 'all', search = ''): string {
  const query = receiptFilters(filter, search).toString();
  return `/jobs/${encodeURIComponent(jobId)}${query ? `?${query}` : ''}#import-results`;
}

export function receiptFilters(filter: string, search: string): URLSearchParams {
  const params = new URLSearchParams();
  if (filter !== 'all') params.set('fileOutcome', filter);
  if (search) params.set('fileQuery', search.slice(0, 200));
  return params;
}

export function importResultTarget(path: string, jobId: string, filter: string, search: string) {
  const [pathname, query] = path.split('?');
  const params = new URLSearchParams(query);
  params.set('importJob', jobId);
  for (const [key, value] of receiptFilters(filter, search)) params.set(key, value);
  return `${pathname}?${params}`;
}

export function importReceiptReturn(params: URLSearchParams): string | undefined {
  const id = params.get('importJob');
  return id && /^[a-z0-9-]+$/i.test(id)
    ? importReceiptUrl(id, params.get('fileOutcome') || 'all', params.get('fileQuery') ?? '')
    : undefined;
}
