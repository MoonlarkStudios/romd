const datImportDateFormat = new Intl.DateTimeFormat(undefined, {
  dateStyle: 'medium',
});

export function DatImportDate({ value }: { value?: string | null }) {
  if (!value) return '—';
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) return '—';
  return <time dateTime={value}>{datImportDateFormat.format(date)}</time>;
}

export function datCountLabel(count: number) {
  return `${count.toLocaleString()} ${count === 1 ? 'DAT' : 'DATs'}`;
}
