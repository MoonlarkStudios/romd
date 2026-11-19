import type {
  ManagedSystemHardware,
  ManagedSystemPatch,
  ManagedSystemSetupDetails,
} from '@romd/admin-api-client';

export type SystemPresentation = ManagedSystemHardware & {
  name: string;
  description: string;
  manufacturerKeys: string[];
  nameAliases: string[];
};

export function presentationValues(details?: ManagedSystemSetupDetails): SystemPresentation {
  return {
    name: details?.system.name ?? '',
    description: details?.system.description ?? '',
    category: details?.hardware?.category ?? null,
    generation: details?.hardware?.generation ?? null,
    releaseYear: details?.hardware?.releaseYear ?? null,
    manufacturerKeys: details?.system.manufacturers?.map((company) => company.key) ?? [],
    nameAliases: [],
  };
}

export function setupAliases(values: string[]) {
  return values.map((value) => value.trim()).filter(Boolean);
}

export function systemPresentationPatch(
  draft: SystemPresentation,
  details: ManagedSystemSetupDetails,
): ManagedSystemPatch {
  const original = presentationValues(details);
  const name = draft.name.trim();
  const description = draft.description.trim() || null;
  const nameAliases = setupAliases(draft.nameAliases);
  const hardwareEditable = details.system.ownership === 'Installation';
  const manufacturerChanged =
    draft.manufacturerKeys.length !== original.manufacturerKeys.length ||
    draft.manufacturerKeys.some((key) => !original.manufacturerKeys.includes(key));
  return {
    ...(name !== original.name && {
      name,
    }),
    ...(description !== (original.description || null) && {
      description,
    }),
    ...(nameAliases.length > 0 && {
      nameAliases,
    }),
    ...(hardwareEditable &&
      draft.category !== original.category && {
        category: draft.category,
      }),
    ...(hardwareEditable &&
      draft.generation !== original.generation && {
        generation: draft.generation,
      }),
    ...(hardwareEditable &&
      draft.releaseYear !== original.releaseYear && {
        releaseYear: draft.releaseYear,
      }),
    ...(hardwareEditable &&
      manufacturerChanged && {
        manufacturerKeys: draft.manufacturerKeys,
      }),
  };
}
