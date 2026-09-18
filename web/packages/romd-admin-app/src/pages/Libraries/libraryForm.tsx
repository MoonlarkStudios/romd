import { ActionIcon, Badge, Group, Stack, Text } from '@mantine/core';
import type {
  CatalogTitle,
  LibraryConfigurationDto,
  LibraryDto,
  RatingBasisSelection,
  RatingBoard,
} from '@romd/admin-api-client';
import { IconX } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { TitleSearchSelect } from '../../components/TitleSearchSelect';
import {
  type TitleLabel,
  titleLookupKeys,
  useTitleLabelMap,
} from '../../hooks/api/useLibraryManagement';
import {
  defaultContentRatingPolicy,
  defaultLibraryConfiguration,
  type MaxMinimumAgeValue,
  maxMinimumAgeOptions,
  type TitleSelectionModeValue,
  titleSelectionModeOptions,
  type UnknownMetadataPolicyValue,
  unknownMetadataPolicyOptions,
} from './libraryOptions';

interface LibraryFormState {
  name: string;
  isDefault: boolean;
  titleSelectionMode: TitleSelectionModeValue;
  allowedSystemKeys: string[];
  excludedDatIds: string[];
  contentRatingBasisSelection: RatingBasisSelection;
  contentRatingBoardPreference: RatingBoard[];
  maxMinimumAge: MaxMinimumAgeValue;
  allowRefusedClassification: boolean;
  unknownRatingPolicy: UnknownMetadataPolicyValue;
  allowedGenres: string[];
  unknownGenrePolicy: UnknownMetadataPolicyValue;
  showMissingGames: boolean;
  includeTitleIds: string[];
  excludeTitleIds: string[];
}

function isMaxMinimumAgeValue(value: string): value is MaxMinimumAgeValue {
  return maxMinimumAgeOptions.some((option) => option.value === value);
}

function isTitleSelectionModeValue(value: string): value is TitleSelectionModeValue {
  return titleSelectionModeOptions.some((option) => option.value === value);
}

function isUnknownMetadataPolicyValue(value: string): value is UnknownMetadataPolicyValue {
  return unknownMetadataPolicyOptions.some((option) => option.value === value);
}

function normalizeMaxMinimumAge(value: string | number | null | undefined): MaxMinimumAgeValue {
  const normalized = value == null ? 'none' : String(value);
  return isMaxMinimumAgeValue(normalized) ? normalized : '18';
}

function normalizeTitleSelectionMode(value: string | null | undefined): TitleSelectionModeValue {
  const normalized = String(value ?? defaultLibraryConfiguration.titleSelectionMode);
  return isTitleSelectionModeValue(normalized) ? normalized : 'Rules';
}

function normalizeUnknownMetadataPolicy(
  value: string | null | undefined,
  fallback: UnknownMetadataPolicyValue,
): UnknownMetadataPolicyValue {
  const normalized = String(value ?? fallback);
  return isUnknownMetadataPolicyValue(normalized) ? normalized : fallback;
}

export function toFormState(library: LibraryDto | null): LibraryFormState {
  const configuration = library?.configuration ?? defaultLibraryConfiguration;
  const contentRatingPolicy = configuration.contentRatingPolicy ?? defaultContentRatingPolicy;

  return {
    name: library?.name ?? '',
    isDefault: library?.isDefault ?? false,
    titleSelectionMode: normalizeTitleSelectionMode(configuration.titleSelectionMode),
    allowedSystemKeys: [
      ...(configuration.allowedSystemKeys ?? []),
    ],
    excludedDatIds: [
      ...(configuration.excludedDatIds ?? []),
    ],
    contentRatingBasisSelection:
      contentRatingPolicy.basisSelection ??
      defaultContentRatingPolicy.basisSelection ??
      'Strictest',
    contentRatingBoardPreference: [
      ...(contentRatingPolicy.boardPreference ?? defaultContentRatingPolicy.boardPreference ?? []),
    ],
    maxMinimumAge: normalizeMaxMinimumAge(contentRatingPolicy.maxMinimumAge),
    allowRefusedClassification: contentRatingPolicy.allowRefusedClassification ?? false,
    unknownRatingPolicy: normalizeUnknownMetadataPolicy(
      contentRatingPolicy.unknownRatingPolicy,
      'NeedsReview',
    ),
    allowedGenres: [
      ...(configuration.allowedGenres ?? []),
    ],
    unknownGenrePolicy: normalizeUnknownMetadataPolicy(configuration.unknownGenrePolicy, 'Allow'),
    showMissingGames: configuration.showMissingGames ?? false,
    includeTitleIds: [
      ...(configuration.includeTitleIds ?? []),
    ],
    excludeTitleIds: [
      ...(configuration.excludeTitleIds ?? []),
    ],
  };
}

export function toConfiguration(form: LibraryFormState): LibraryConfigurationDto {
  const contentRatingPolicy = {
    basisSelection: form.contentRatingBasisSelection,
    boardPreference: form.contentRatingBoardPreference,
    maxMinimumAge: form.maxMinimumAge === 'none' ? null : Number(form.maxMinimumAge),
    allowRefusedClassification: form.allowRefusedClassification,
    unknownRatingPolicy: form.unknownRatingPolicy,
  };

  if (form.titleSelectionMode === 'IncludeOnly') {
    return {
      titleSelectionMode: form.titleSelectionMode,
      allowedSystemKeys: [],
      excludedDatIds: form.excludedDatIds,
      contentRatingPolicy,
      allowedGenres: [],
      unknownGenrePolicy: defaultLibraryConfiguration.unknownGenrePolicy,
      showMissingGames: form.showMissingGames,
      includeTitleIds: form.includeTitleIds,
      excludeTitleIds: [],
    };
  }

  return {
    titleSelectionMode: form.titleSelectionMode,
    allowedSystemKeys: form.allowedSystemKeys,
    excludedDatIds: form.excludedDatIds,
    contentRatingPolicy,
    allowedGenres: form.allowedGenres,
    unknownGenrePolicy: form.unknownGenrePolicy,
    showMissingGames: form.showMissingGames,
    includeTitleIds: form.includeTitleIds,
    excludeTitleIds: form.excludeTitleIds,
  };
}

export function TitleOverridePicker({
  label,
  description,
  emptyMessage = 'No manual title overrides.',
  value,
  onChange,
  excludeIds = [],
}: {
  label: string;
  description: string;
  emptyMessage?: string;
  value: string[];
  onChange: (ids: string[]) => void;
  excludeIds?: string[];
}) {
  const [knownTitles, setKnownTitles] = useState<Map<string, string>>(new Map());
  const [searchKey, setSearchKey] = useState(0);
  const titleLabels = useTitleLabelMap(value);
  const queryClient = useQueryClient();

  const getTitleLabel = (titleId: string) =>
    knownTitles.get(titleId) ?? titleLabels.get(titleId) ?? titleId;

  const handleAdd = (titleId: string | null, title: CatalogTitle | null) => {
    if (!titleId || value.includes(titleId)) {
      return;
    }

    if (title) {
      setKnownTitles((current) => new Map(current).set(titleId, title.name));
      queryClient.setQueryData<TitleLabel>(titleLookupKeys.detail(titleId), {
        id: titleId,
        name: title.name,
      });
    }

    onChange([
      ...value,
      titleId,
    ]);
    setSearchKey((current) => current + 1);
  };

  const handleRemove = (titleId: string) => {
    onChange(value.filter((id) => id !== titleId));
  };

  return (
    <Stack gap="xs">
      <TitleSearchSelect
        key={searchKey}
        value={null}
        onChange={handleAdd}
        label={label}
        placeholder="Search titles to add"
        excludeIds={[
          ...excludeIds,
          ...value,
        ]}
      />
      <Text
        size="xs"
        c="dimmed"
      >
        {description}
      </Text>
      {value.length > 0 ? (
        <Group gap="xs">
          {value.map((titleId) => (
            <Badge
              key={titleId}
              variant="light"
              color="blue"
              rightSection={
                <ActionIcon
                  aria-label={`Remove ${getTitleLabel(titleId)}`}
                  size="xs"
                  variant="transparent"
                  color="blue"
                  onClick={() => handleRemove(titleId)}
                >
                  <IconX size={12} />
                </ActionIcon>
              }
            >
              {getTitleLabel(titleId)}
            </Badge>
          ))}
        </Group>
      ) : (
        <Text
          size="xs"
          c="dimmed"
        >
          {emptyMessage}
        </Text>
      )}
    </Stack>
  );
}
