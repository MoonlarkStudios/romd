import { ActionIcon, Group, Stack, Text } from '@mantine/core';
import type {
  CatalogTitle,
  Library,
  LibraryConfiguration,
  RatingBasisSelection,
  RatingBoard,
} from '@romd/admin-api-client';
import { IconX } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { gamePosterUrl } from '../../components/artworkPresentation';
import { GameCover } from '../../components/GameCover';
import { TitleSearchSelect } from '../../components/TitleSearchSelect';
import {
  type TitlePickerGame,
  titleLookupKeys,
  usePickedGameMap,
} from '../../hooks/api/useLibraryManagement';
import classes from './Libraries.module.css';
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
  selectedDiscSetIds: string[];
  contentRatingBasisSelection: RatingBasisSelection;
  contentRatingBoardPreference: RatingBoard[];
  maxMinimumAge: MaxMinimumAgeValue;
  allowRefusedClassification: boolean;
  unknownRatingPolicy: UnknownMetadataPolicyValue;
  allowedGenres: string[];
  unknownGenrePolicy: UnknownMetadataPolicyValue;
  showMissingGames: boolean;
  includeUntrackedTitles: boolean;
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

export function toFormState(library: Library | null): LibraryFormState {
  const configuration = library?.configuration ?? defaultLibraryConfiguration;
  const contentRatingPolicy = configuration.contentRatingPolicy ?? defaultContentRatingPolicy;

  return {
    name: library?.name ?? '',
    isDefault: library?.isDefault ?? false,
    titleSelectionMode: normalizeTitleSelectionMode(configuration.titleSelectionMode),
    allowedSystemKeys: [
      ...(configuration.allowedSystemKeys ?? []),
    ],
    selectedDiscSetIds: [
      ...(configuration.selectedDiscSetIds ?? []),
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
    includeUntrackedTitles: configuration.includeUntrackedTitles ?? false,
    includeTitleIds: [
      ...(configuration.includeTitleIds ?? []),
    ],
    excludeTitleIds: [
      ...(configuration.excludeTitleIds ?? []),
    ],
  };
}

export function toConfiguration(form: LibraryFormState): LibraryConfiguration {
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
      selectedDiscSetIds: form.selectedDiscSetIds,
      contentRatingPolicy,
      allowedGenres: [],
      unknownGenrePolicy: defaultLibraryConfiguration.unknownGenrePolicy,
      showMissingGames: form.showMissingGames,
      includeUntrackedTitles: form.includeUntrackedTitles,
      includeTitleIds: form.includeTitleIds,
      excludeTitleIds: [],
    };
  }

  return {
    titleSelectionMode: form.titleSelectionMode,
    allowedSystemKeys: form.allowedSystemKeys,
    excludedDatIds: form.excludedDatIds,
    selectedDiscSetIds: form.selectedDiscSetIds,
    contentRatingPolicy,
    allowedGenres: form.allowedGenres,
    unknownGenrePolicy: form.unknownGenrePolicy,
    showMissingGames: form.showMissingGames,
    includeUntrackedTitles: form.includeUntrackedTitles,
    includeTitleIds: form.includeTitleIds,
    excludeTitleIds: form.excludeTitleIds,
  };
}

export function TitleOverridePicker({
  label,
  description,
  value,
  onChange,
  excludeIds = [],
  localFirst,
  withinPortal,
}: {
  label: string;
  description?: string;
  value: string[];
  onChange: (ids: string[]) => void;
  excludeIds?: string[];
  localFirst?: boolean;
  withinPortal?: boolean;
}) {
  const games = usePickedGameMap(value);
  const queryClient = useQueryClient();

  const handleAdd = (titleId: string | null, title: CatalogTitle | null) => {
    if (!titleId || value.includes(titleId)) {
      return;
    }

    if (title) {
      queryClient.setQueryData<TitlePickerGame>(titleLookupKeys.detail(titleId), {
        id: titleId,
        name: title.name,
        posterUrl: gamePosterUrl(title.artwork),
      });
    }

    onChange([
      ...value,
      titleId,
    ]);
  };

  const handleRemove = (titleId: string) => {
    onChange(value.filter((id) => id !== titleId));
  };

  return (
    <Stack gap="xs">
      <TitleSearchSelect
        value={null}
        onChange={handleAdd}
        label={label}
        placeholder=""
        clearAfterSelect
        localFirst={localFirst}
        withinPortal={withinPortal}
        excludeIds={[
          ...excludeIds,
          ...value,
        ]}
      />
      {description && (
        <Text
          size="xs"
          c="dimmed"
        >
          {description}
        </Text>
      )}
      {value.length > 0 && (
        <ul
          className={classes.pickedGames}
          aria-label={`${label} selection`}
        >
          {value.map((titleId) => {
            const game = games.get(titleId);
            const name = game?.name ?? titleId;
            return (
              <Group
                component="li"
                key={titleId}
                gap="sm"
                wrap="nowrap"
              >
                <GameCover url={game?.posterUrl} />
                <Text
                  size="sm"
                  lineClamp={2}
                  flex={1}
                >
                  {name}
                </Text>
                <ActionIcon
                  aria-label={`Remove ${name}`}
                  variant="subtle"
                  color="gray"
                  onClick={() => handleRemove(titleId)}
                >
                  <IconX size={16} />
                </ActionIcon>
              </Group>
            );
          })}
        </ul>
      )}
    </Stack>
  );
}
