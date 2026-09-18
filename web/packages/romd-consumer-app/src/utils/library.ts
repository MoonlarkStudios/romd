import type { ConsumerMediaRefDto } from '@romd/consumer-api-client';

export function getMediaByType(media: ConsumerMediaRefDto[], types: string[]): ConsumerMediaRefDto[] {
  const normalizedTypes = types.map((type) => type.toLowerCase());

  return media.filter((item) => normalizedTypes.includes(item.type.toLowerCase()));
}

export function getPreferredMedia(media: ConsumerMediaRefDto[], types: string[]): ConsumerMediaRefDto | null {
  const typedMedia = getMediaByType(media, types);

  return (
    typedMedia.find((item) => item.isPrimary) ??
    typedMedia[0] ??
    media.find((item) => item.isPrimary) ??
    media[0] ??
    null
  );
}

export function formatList(values: readonly string[], fallback = 'Unspecified'): string {
  return values.length > 0 ? values.join(', ') : fallback;
}
