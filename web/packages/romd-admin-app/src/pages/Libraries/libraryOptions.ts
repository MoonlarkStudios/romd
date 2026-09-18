import type {
  LibraryConfigurationDto,
  LibraryTitleSelectionMode,
  UnknownMetadataPolicy,
} from '@romd/admin-api-client';

export type MaxMinimumAgeValue = '0' | '8' | '10' | '12' | '13' | '15' | '16' | '17' | '18' | 'none';
export type UnknownMetadataPolicyValue = UnknownMetadataPolicy;
export type TitleSelectionModeValue = LibraryTitleSelectionMode;
export type ConfigurationState = 'Valid' | 'Invalid' | 'RequiresMigration';

export const titleSelectionModeOptions: { value: TitleSelectionModeValue; label: string }[] = [
  { value: 'Rules', label: 'Rule-based' },
  { value: 'IncludeOnly', label: 'Whitelist only' },
];

export const maxMinimumAgeOptions: { value: MaxMinimumAgeValue; label: string }[] = [
  { value: '0', label: '0+' },
  { value: '8', label: '8+' },
  { value: '10', label: '10+' },
  { value: '12', label: '12+' },
  { value: '13', label: '13+' },
  { value: '15', label: '15+' },
  { value: '16', label: '16+' },
  { value: '17', label: '17+' },
  { value: '18', label: '18+' },
  { value: 'none', label: 'No ceiling' },
];

export const unknownMetadataPolicyOptions: {
  value: UnknownMetadataPolicyValue;
  label: string;
}[] = [
  { value: 'Allow', label: 'Show' },
  { value: 'Hide', label: 'Hide' },
  { value: 'NeedsReview', label: 'Flag for review' },
];

export const defaultContentRatingPolicy: NonNullable<LibraryConfigurationDto['contentRatingPolicy']> = {
  basisSelection: 'Strictest',
  boardPreference: ['Esrb', 'Pegi', 'Cero', 'Usk', 'Grac', 'ClassInd', 'Acb'],
  maxMinimumAge: 18,
  allowRefusedClassification: false,
  unknownRatingPolicy: 'NeedsReview',
};

export const defaultLibraryConfiguration: Required<LibraryConfigurationDto> = {
  titleSelectionMode: 'Rules',
  allowedSystemKeys: [],
  excludedDatIds: [],
  contentRatingPolicy: defaultContentRatingPolicy,
  allowedGenres: [],
  unknownGenrePolicy: 'Allow',
  showMissingGames: false,
  includeTitleIds: [],
  excludeTitleIds: [],
};

export function getTitleSelectionModeLabel(value: string | null | undefined): string {
  return (
    titleSelectionModeOptions.find((option) => option.value === String(value))?.label ??
    'Unknown'
  );
}

export function getMaxMinimumAgeLabel(value: string | number | null | undefined): string {
  return (
    maxMinimumAgeOptions.find((option) => option.value === String(value ?? 'none'))?.label ??
    'Unknown'
  );
}

export function getUnknownMetadataPolicyLabel(value: string | null | undefined): string {
  return (
    unknownMetadataPolicyOptions.find((option) => option.value === String(value))?.label ??
    'Unknown'
  );
}

export function getConfigurationStateColor(state: string): string {
  if (state === 'Valid') {
    return 'green';
  }

  if (state === 'Invalid') {
    return 'red';
  }

  if (state === 'RequiresMigration') {
    return 'yellow';
  }

  return 'gray';
}

export function isConfigurationState(value: string): value is ConfigurationState {
  return value === 'Valid' || value === 'Invalid' || value === 'RequiresMigration';
}
