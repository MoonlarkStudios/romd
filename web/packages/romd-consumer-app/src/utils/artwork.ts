import type { ResolvedArtworkDto } from '@romd/consumer-api-client';

export type ArtworkRole = 'Poster' | 'Hero' | 'Logo' | 'Backdrop';

/** Select delivery variants only; source originals are never fetched by the client. */
export function selectArtwork(
  artwork: ReadonlyArray<ResolvedArtworkDto> | undefined,
  role: ArtworkRole,
  displayWidth: number,
  pixelRatio = 1,
) {
  const resolved = artwork?.find((item) => item.role === role);
  if (!resolved) return null;
  const targetWidth = Math.max(1, displayWidth) * Math.min(2, Math.max(1, pixelRatio));
  const variants = resolved.variants
    .filter((variant) => variant.name.toLowerCase() !== 'original' && variant.width > 0 && variant.height > 0)
    .sort((left, right) => left.width - right.width || left.name.localeCompare(right.name));
  const selected = variants.find((variant) => variant.width >= targetWidth) ?? variants.at(-1);
  return {
    url: selected?.url ?? resolved.url,
    contentVersion: selected?.contentVersion ?? resolved.contentVersion,
    fit: resolved.fit === 'Cover' ? ('cover' as const) : ('contain' as const),
    focalX: resolved.focalX ?? 50,
    focalY: resolved.focalY ?? 50,
  };
}

/** Delivery stays on this ROMD server; provider URLs are never rendered or cached. */
export function artworkDeliveryUrl(value: string | null | undefined, origin: string): string | null {
  if (!value) return null;
  try {
    const url = new URL(value, origin);
    if (
      url.origin !== origin || url.username || url.password || url.search || url.hash ||
      (!url.pathname.startsWith('/artwork/') && !url.pathname.startsWith('/media/'))
    ) return null;
    if (url.pathname.startsWith('/artwork/') && decodeURIComponent(url.pathname.split('/')[3] ?? '').toLowerCase() === 'original') return null;
    return url.href;
  } catch {
    return null;
  }
}
