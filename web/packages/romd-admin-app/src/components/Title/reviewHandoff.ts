export function reviewReturnPath(value: unknown): string | undefined {
  if (typeof value !== 'string' || !value.startsWith('/catalog?')) return;
  if (new URLSearchParams(value.slice('/catalog?'.length)).get('view') === 'review') return value;
}

export function libraryInspectionPath(titleId: string, reviewPath: string): string {
  return `/libraries?${new URLSearchParams({
    inspect: titleId,
    reviewReturn: reviewPath,
  })}`;
}

export function libraryPreviewReturnPath(value: unknown): string | undefined {
  if (typeof value !== 'string' || !/^\/libraries\/[a-z0-9]+\?/i.test(value)) return;
  const query = new URLSearchParams(value.slice(value.indexOf('?') + 1));
  if (query.get('tab') === 'preview' && query.get('inspect')) return value;
}
