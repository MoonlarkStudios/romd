import type { ManagedSystemDto } from '@romd/admin-api-client';

export function matchesSystem(system: ManagedSystemDto, search: string) {
  const normalize = (s: string) =>
    s
      .normalize('NFKD')
      .replace(/\p{Diacritic}/gu, '')
      .toLowerCase()
      .replace(/[^\p{L}\p{N}]/gu, '');
  const query = normalize(search);
  return [
    system.name,
    system.shortName,
    system.manufacturer ?? '',
    ...system.aliases,
  ].some((s) => normalize(s).includes(query));
}
