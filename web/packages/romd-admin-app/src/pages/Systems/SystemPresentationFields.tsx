import {
  Badge,
  Box,
  Button,
  Group,
  Input,
  MultiSelect,
  NumberInput,
  Pill,
  Select,
  SimpleGrid,
  Stack,
  TagsInput,
  Text,
  Textarea,
  TextInput,
  VisuallyHidden,
} from '@mantine/core';
import type { CompanySummary, PlatformAlias } from '@romd/admin-api-client';
import { IconLock } from '@tabler/icons-react';
import { type ReactNode, useId } from 'react';
import { useReferenceCompanies } from '../../hooks/api/useReferenceCompanies';
import {
  localSystemKeyPrefix,
  localSystemKeySuffixMaxLength,
  validLocalSystemKeySuffix,
} from '../../utils/systemKey';
import classes from './SystemSetupFlow.module.css';
import type { SystemPresentation } from './systemPresentation';

const categories = [
  {
    value: 'console',
    label: 'Console',
  },
  {
    value: 'handheld',
    label: 'Handheld',
  },
  {
    value: 'hybrid',
    label: 'Hybrid',
  },
  {
    value: 'computer',
    label: 'Computer',
  },
  {
    value: 'arcade',
    label: 'Arcade',
  },
  {
    value: 'add-on',
    label: 'Add-on',
  },
  {
    value: 'software',
    label: 'Software platform',
  },
  {
    value: 'media-player',
    label: 'Media player',
  },
  {
    value: 'other',
    label: 'Other',
  },
];

export function systemCategoryLabel(category: string | null | undefined) {
  return categories.find((option) => option.value === category)?.label ?? category;
}

export function validSystemPresentation(value: SystemPresentation) {
  return (
    !!value.name.trim() &&
    (value.generation === null || (value.generation >= 1 && value.generation <= 99)) &&
    (value.releaseYear === null || (value.releaseYear >= 1000 && value.releaseYear <= 9999))
  );
}

const lockedField = {
  readOnly: true,
  rightSection: (
    <IconLock
      size={15}
      aria-hidden="true"
    />
  ),
  rightSectionPointerEvents: 'none' as const,
};

export function SystemPresentationFields({
  value,
  onChange,
  systemKey,
  onKeySuffixChange,
  aliasError,
  checkingAliases,
  retryAliasCheck,
  hardwareEditable,
  manufacturers = [],
  existingAliases,
  onRemoveAlias,
  descriptionActions,
  disabled,
}: {
  value: SystemPresentation;
  onChange?: (value: SystemPresentation) => void;
  systemKey: string;
  onKeySuffixChange?: (suffix: string) => void;
  aliasError?: string;
  checkingAliases?: boolean;
  retryAliasCheck?: () => void;
  hardwareEditable: boolean;
  manufacturers?: CompanySummary[];
  existingAliases?: PlatformAlias[];
  onRemoveAlias?: (alias: PlatformAlias) => void;
  descriptionActions?: ReactNode;
  disabled: boolean;
}) {
  const descriptionId = useId();
  const readOnly = !onChange;
  const keySuffix = systemKey.slice(localSystemKeyPrefix.length);
  const companies = useReferenceCompanies(hardwareEditable);
  const change = (patch: Partial<SystemPresentation>) =>
    onChange?.({
      ...value,
      ...patch,
    });
  const manufacturerOptions = (companies.data ?? manufacturers)
    .filter(
      (company) =>
        !('retired' in company && company.retired) || value.manufacturerKeys.includes(company.key),
    )
    .map((company) => ({
      value: company.key,
      label: company.name,
    }));

  return (
    <Stack
      gap="sm"
      className={classes.presentation}
    >
      <SimpleGrid
        cols={{
          base: 1,
          xs: 2,
        }}
        spacing="sm"
      >
        <TextInput
          label="Display name"
          readOnly={readOnly}
          required={!readOnly}
          maxLength={200}
          value={value.name}
          disabled={disabled}
          onChange={(event) =>
            change({
              name: event.currentTarget.value,
            })
          }
        />
        <TextInput
          label="System key"
          inputWrapperOrder={[
            'label',
            'input',
            'description',
            'error',
          ]}
          {...(onKeySuffixChange
            ? {
                leftSection: (
                  <Text
                    span
                    size="sm"
                    c="dimmed"
                    aria-hidden="true"
                  >
                    {localSystemKeyPrefix}
                  </Text>
                ),
                leftSectionWidth: 56,
                leftSectionPointerEvents: 'none',
                description: 'The local- prefix is fixed.',
                required: true,
                maxLength: localSystemKeySuffixMaxLength,
                value: keySuffix,
                disabled,
                error:
                  keySuffix && !validLocalSystemKeySuffix(keySuffix)
                    ? 'Use lowercase letters, numbers, and single hyphens between them.'
                    : undefined,
                onChange: (event) => onKeySuffixChange(event.currentTarget.value),
              }
            : {
                ...lockedField,
                value: systemKey,
              })}
        />
        {hardwareEditable ? (
          <>
            <MultiSelect
              label="Manufacturers"
              data={manufacturerOptions}
              value={value.manufacturerKeys}
              searchable
              maxValues={16}
              placeholder={companies.isPending ? 'Loading companies…' : 'Select companies'}
              disabled={disabled || companies.isPending}
              onChange={(manufacturerKeys) =>
                change({
                  manufacturerKeys,
                })
              }
            />
            <Select
              label="Category"
              data={categories}
              value={value.category}
              clearable
              placeholder="Select a category"
              disabled={disabled}
              onChange={(category) =>
                change({
                  category,
                })
              }
            />
            <NumberInput
              label="Generation"
              value={value.generation ?? ''}
              min={1}
              max={99}
              allowDecimal={false}
              allowNegative={false}
              hideControls
              clampBehavior="none"
              placeholder="Optional"
              disabled={disabled}
              error={
                value.generation !== null && (value.generation < 1 || value.generation > 99)
                  ? 'Enter 1 to 99'
                  : undefined
              }
              onChange={(generation) =>
                change({
                  generation: typeof generation === 'number' ? generation : null,
                })
              }
            />
            <NumberInput
              label="Release year"
              value={value.releaseYear ?? ''}
              min={1000}
              max={9999}
              allowDecimal={false}
              allowNegative={false}
              hideControls
              clampBehavior="none"
              placeholder="Optional"
              disabled={disabled}
              error={
                value.releaseYear !== null && (value.releaseYear < 1000 || value.releaseYear > 9999)
                  ? 'Enter a four-digit year'
                  : undefined
              }
              onChange={(releaseYear) =>
                change({
                  releaseYear: typeof releaseYear === 'number' ? releaseYear : null,
                })
              }
            />
          </>
        ) : (
          <>
            <TextInput
              {...lockedField}
              label="Manufacturers"
              value={manufacturers.map((company) => company.name).join(', ')}
              placeholder="Not recorded"
            />
            <TextInput
              {...lockedField}
              label="Category"
              value={systemCategoryLabel(value.category) ?? ''}
              placeholder="Not recorded"
            />
            <TextInput
              {...lockedField}
              label="Generation"
              value={value.generation ?? ''}
              placeholder="Not recorded"
            />
            <TextInput
              {...lockedField}
              label="Release year"
              value={value.releaseYear ?? ''}
              placeholder="Not recorded"
            />
          </>
        )}
      </SimpleGrid>
      {hardwareEditable && companies.isError && (
        <Button
          size="compact-sm"
          variant="subtle"
          w="fit-content"
          onClick={() => void companies.refetch()}
        >
          Retry manufacturers
        </Button>
      )}
      {existingAliases && (
        <Stack gap={4}>
          <Text
            size="sm"
            fw={500}
          >
            Existing aliases
          </Text>
          {existingAliases.length > 0 ? (
            <Group gap={4}>
              {existingAliases.map((alias) => {
                const local = alias.ownership === 'Installation';
                const label = alias.ownership === 'Romd' ? 'Built-in alias' : 'Ownership unknown';
                return local && onRemoveAlias ? (
                  <Pill
                    key={alias.id}
                    withRemoveButton
                    onRemove={() => onRemoveAlias(alias)}
                    removeButtonProps={{
                      'aria-label': `Remove alias ${alias.value} from this system`,
                      'aria-hidden': false,
                      disabled,
                      tabIndex: 0,
                    }}
                  >
                    {alias.value}
                  </Pill>
                ) : (
                  <Badge
                    variant="default"
                    key={alias.id}
                    title={local ? undefined : label}
                    leftSection={
                      local ? undefined : (
                        <>
                          <IconLock
                            size={12}
                            aria-hidden="true"
                          />
                          <VisuallyHidden>{label}</VisuallyHidden>
                        </>
                      )
                    }
                  >
                    {alias.value}
                  </Badge>
                );
              })}
            </Group>
          ) : (
            <Text
              size="sm"
              c="dimmed"
            >
              No name aliases
            </Text>
          )}
        </Stack>
      )}
      {!readOnly && (
        <TagsInput
          label="Add name aliases"
          placeholder="Type an alias and press Enter"
          description={
            checkingAliases ? (
              <output>Checking aliases…</output>
            ) : (
              'Aliases help ROMD match uploaded DATs.'
            )
          }
          error={aliasError ? <span role="alert">{aliasError}</span> : undefined}
          inputWrapperOrder={[
            'label',
            'input',
            'description',
            'error',
          ]}
          value={value.nameAliases}
          maxTags={16}
          maxLength={200}
          acceptValueOnBlur
          renderPill={({ value: alias, onRemove, disabled: pillDisabled }) => (
            <Pill
              withRemoveButton={!pillDisabled}
              disabled={pillDisabled}
              onRemove={onRemove}
              removeButtonProps={{
                'aria-label': `Remove alias ${alias}`,
                'aria-hidden': false,
                tabIndex: 0,
              }}
            >
              {alias}
            </Pill>
          )}
          disabled={disabled}
          onChange={(nameAliases) =>
            change({
              nameAliases,
            })
          }
        />
      )}
      {retryAliasCheck && (
        <Button
          size="compact-sm"
          variant="subtle"
          w="fit-content"
          disabled={disabled || checkingAliases}
          onClick={retryAliasCheck}
        >
          Retry alias check
        </Button>
      )}
      <Box>
        <Group
          justify="space-between"
          wrap="nowrap"
          gap="xs"
        >
          <Input.Label
            htmlFor={descriptionId}
            size="sm"
          >
            Description
          </Input.Label>
          {descriptionActions}
        </Group>
        <Textarea
          id={descriptionId}
          readOnly={readOnly}
          placeholder="Optional"
          autosize
          minRows={2}
          maxRows={12}
          maxLength={2000}
          value={value.description}
          disabled={disabled}
          onChange={(event) =>
            change({
              description: event.currentTarget.value,
            })
          }
        />
      </Box>
    </Stack>
  );
}
