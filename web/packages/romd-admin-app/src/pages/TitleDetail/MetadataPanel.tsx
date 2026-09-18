import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Card,
  Divider,
  Grid,
  Group,
  Menu,
  Modal,
  NativeSelect,
  NumberInput,
  SegmentedControl,
  Select,
  Skeleton,
  Slider,
  Stack,
  Table,
  Text,
  Textarea,
  TextInput,
  ThemeIcon,
  Tooltip,
  UnstyledButton,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type {
  ExternalIdDto,
  MetadataLayerDto,
  TitleDetail,
  TitleEnrichmentStateResponse,
  UpdateTitleMetadataRequest,
} from '@romd/admin-api-client';
import {
  IconCheck,
  IconChevronRight,
  IconDatabase,
  IconDeviceFloppy,
  IconDots,
  IconLink,
} from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useCatalogFilterFacets } from '../../hooks/api/useLibraryManagement';
import { useAssociateExternalId, useUpdateTitleMetadata } from '../../hooks/api/useTitleActions';
import { titleDetailKeys } from '../../hooks/api/useTitleDetail';
import {
  useConfirmExternalId,
  useSetFieldOverrides,
  useTitleEnrichment,
} from '../../hooks/api/useTitleEnrichment';
import { usePermissions } from '../../hooks/usePermissions';
import { ProviderMatches } from './ProviderMatches';
import classes from './TitleDetail.module.css';

interface MetadataPanelProps {
  title: TitleDetail;
  titleId: string;
}

type MetadataFieldKey =
  | 'description'
  | 'genre'
  | 'releaseDate'
  | 'publisher'
  | 'developer'
  | 'players'
  | 'rating';

type MetadataFieldKind =
  | 'text'
  | 'textarea'
  | 'date'
  | 'genre'
  | 'players'
  | 'rating';

interface MetadataFieldDefinition {
  key: MetadataFieldKey;
  overrideKey: string;
  label: string;
  kind: MetadataFieldKind;
}

const METADATA_FIELDS: MetadataFieldDefinition[] = [
  { key: 'description', overrideKey: 'Description', label: 'Description', kind: 'textarea' },
  { key: 'genre', overrideKey: 'Genre', label: 'Genre', kind: 'genre' },
  { key: 'releaseDate', overrideKey: 'ReleaseDate', label: 'Release Date', kind: 'date' },
  { key: 'publisher', overrideKey: 'Publisher', label: 'Publisher', kind: 'text' },
  { key: 'developer', overrideKey: 'Developer', label: 'Developer', kind: 'text' },
  { key: 'players', overrideKey: 'Players', label: 'Players', kind: 'players' },
  { key: 'rating', overrideKey: 'Rating', label: 'Rating', kind: 'rating' },
];

interface Option {
  value: string;
  label: string;
}

interface MetadataTaxonomy {
  genreOptions: Option[];
}

const BASE_GENRE_TAXONOMY: Option[] = [
  { value: 'Action', label: 'Action' },
  { value: 'Adventure', label: 'Adventure' },
  { value: 'Arcade', label: 'Arcade' },
  { value: 'Fighting', label: 'Fighting' },
  { value: 'Hack and slash/Beat em up', label: 'Hack and slash / Beat em up' },
  { value: 'Music', label: 'Music' },
  { value: 'Pinball', label: 'Pinball' },
  { value: 'Platform', label: 'Platform' },
  { value: 'Platformer', label: 'Platformer' },
  { value: 'Point-and-click', label: 'Point-and-click' },
  { value: 'Puzzle', label: 'Puzzle' },
  { value: 'Quiz/Trivia', label: 'Quiz / Trivia' },
  { value: 'Racing', label: 'Racing' },
  { value: 'Real Time Strategy (RTS)', label: 'Real Time Strategy (RTS)' },
  { value: 'Role-playing (RPG)', label: 'Role-playing (RPG)' },
  { value: 'RPG', label: 'RPG' },
  { value: 'Shooter', label: 'Shooter' },
  { value: 'Simulator', label: 'Simulator' },
  { value: 'Sport', label: 'Sport' },
  { value: 'Strategy', label: 'Strategy' },
  { value: 'Tactical', label: 'Tactical' },
  { value: 'Turn-based strategy (TBS)', label: 'Turn-based strategy (TBS)' },
  { value: 'Visual Novel', label: 'Visual Novel' },
];

function formatCount(value: number | null | undefined): string {
  return (value ?? 0).toLocaleString();
}

function addOption(options: Map<string, Option>, option: Option): void {
  const value = option.value.trim();
  if (!value) return;

  options.set(value, { value, label: option.label });
}

function addMissingOption(options: Map<string, Option>, value: string | null): void {
  const trimmed = value?.trim();
  if (!trimmed || options.has(trimmed)) return;

  options.set(trimmed, { value: trimmed, label: trimmed });
}

function buildTaxonomyOptions(
  baseOptions: Option[],
  facets: { value: string; count: number }[] | undefined,
  activeValues: (string | null)[],
): Option[] {
  const options = new Map<string, Option>();

  for (const option of baseOptions) {
    addOption(options, option);
  }

  for (const facet of facets ?? []) {
    addOption(options, {
      value: facet.value,
      label: `${facet.value} (${formatCount(facet.count)})`,
    });
  }

  for (const value of activeValues) {
    addMissingOption(options, value);
  }

  return Array.from(options.values());
}

function getTitleValue(title: TitleDetail, field: MetadataFieldDefinition): string | null {
  const value = title[field.key];
  return value === null || value === undefined || value === '' ? null : String(value);
}

function getLayerValue(
  layer: MetadataLayerDto | null | undefined,
  field: MetadataFieldDefinition,
): string | null {
  if (!layer) return null;
  const value = layer[field.key];
  return value === null || value === undefined || value === '' ? null : String(value);
}

function buildTitleFieldPatch(
  field: MetadataFieldDefinition,
  value: string | null,
): Partial<TitleDetail> {
  switch (field.key) {
    case 'description':
      return { description: value };
    case 'genre':
      return { genre: value };
    case 'releaseDate':
      return { releaseDate: value };
    case 'publisher':
      return { publisher: value };
    case 'developer':
      return { developer: value };
    case 'players':
      return { players: toNullableNumber(value) };
    case 'rating':
      return { rating: toNullableNumber(value) };
  }
}

function getMetadataMapValue(
  map: Record<string, string> | null | undefined,
  field: MetadataFieldDefinition,
): string | null {
  return map?.[field.overrideKey] ?? map?.[field.key] ?? null;
}

function isSameSource(left: string | null | undefined, right: string): boolean {
  return left?.toLowerCase() === right.toLowerCase();
}

function getUserLayer(layers: MetadataLayerDto[]): MetadataLayerDto | undefined {
  return layers.find((layer) => layer.sourceId.toLowerCase() === 'user');
}

function getLayerBySource(
  layers: MetadataLayerDto[],
  sourceId: string,
): MetadataLayerDto | undefined {
  return layers.find((layer) => isSameSource(layer.sourceId, sourceId));
}

function formatSourceLabel(sourceId: string): string {
  if (sourceId.toLowerCase() === 'user') return 'User';
  if (sourceId.toLowerCase() === 'igdb') return 'IGDB';
  return sourceId;
}

function getSourceColor(sourceId: string | null | undefined): string {
  if (sourceId?.toLowerCase() === 'user') return 'green';
  if (sourceId?.toLowerCase() === 'igdb') return 'violet';
  return sourceId ? 'blue' : 'gray';
}

function normalizedInputValue(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}

function normalizedBoundedNumber(value: string, min: number, max: number): string | null {
  const trimmed = value.trim();
  if (!trimmed) return null;

  const parsed = Number(trimmed);
  if (!Number.isFinite(parsed)) return null;

  return String(Math.min(max, Math.max(min, Math.round(parsed))));
}

function normalizedFieldValue(field: MetadataFieldDefinition, value: string): string | null {
  if (field.kind === 'rating') return normalizedBoundedNumber(value, 1, 100);
  if (field.kind === 'players') return normalizedBoundedNumber(value, 1, 16);

  return normalizedInputValue(value);
}

function toNullableNumber(value: string | number | null | undefined): number | null {
  if (value === null || value === undefined || value === '') return null;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function numberInputValue(value: string): number | '' {
  if (value === '') return '';

  const numberValue = Number(value);
  return Number.isFinite(numberValue) ? numberValue : '';
}

function numberValueToString(value: string | number): string {
  return value === '' ? '' : String(value);
}

function formatFieldValue(
  field: MetadataFieldDefinition,
  value: string | null,
  taxonomy: MetadataTaxonomy,
): string {
  if (!value) return '-';

  if (field.key === 'rating') {
    const rating = Number(value);
    const formatted = Number.isFinite(rating)
      ? new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(rating)
      : value;

    return `${formatted}/100`;
  }

  return value;
}

function buildUserMetadataRequest(
  title: TitleDetail,
  layers: MetadataLayerDto[],
  field: MetadataFieldDefinition,
  value: string | null,
): UpdateTitleMetadataRequest {
  const userLayer = getUserLayer(layers);

  return {
    name: title.name,
    description:
      field.key === 'description' ? value : getLayerValue(userLayer, METADATA_FIELDS[0]),
    genre: field.key === 'genre' ? value : getLayerValue(userLayer, METADATA_FIELDS[1]),
    releaseDate:
      field.key === 'releaseDate' ? value : getLayerValue(userLayer, METADATA_FIELDS[2]),
    publisher: field.key === 'publisher' ? value : getLayerValue(userLayer, METADATA_FIELDS[3]),
    developer: field.key === 'developer' ? value : getLayerValue(userLayer, METADATA_FIELDS[4]),
    players: toNullableNumber(
      field.key === 'players' ? value : getLayerValue(userLayer, METADATA_FIELDS[5]),
    ),
    rating: toNullableNumber(
      field.key === 'rating' ? value : getLayerValue(userLayer, METADATA_FIELDS[6]),
    ),
  };
}

function sourceHasFieldValue(layer: MetadataLayerDto, field: MetadataFieldDefinition): boolean {
  return getLayerValue(layer, field) !== null;
}

function getAutomaticCascadePreview(
  layers: MetadataLayerDto[],
  field: MetadataFieldDefinition,
  activeSource: string | null,
): { value: string | null; source: string | null } {
  const userLayer = getUserLayer(layers);
  const userValue = getLayerValue(userLayer, field);
  if (userValue !== null) {
    return { value: userValue, source: userLayer?.sourceId ?? 'user' };
  }

  const activeLayer = activeSource ? getLayerBySource(layers, activeSource) : undefined;
  const activeValue = getLayerValue(activeLayer, field);
  if (activeValue !== null) {
    return { value: activeValue, source: activeLayer?.sourceId ?? activeSource };
  }

  const firstLayerWithValue = layers.find((layer) => sourceHasFieldValue(layer, field));
  return {
    value: getLayerValue(firstLayerWithValue, field),
    source: firstLayerWithValue?.sourceId ?? null,
  };
}

function getSourceSelectionPreview(
  layers: MetadataLayerDto[],
  field: MetadataFieldDefinition,
  sourceOverride: string,
  activeSource: string | null,
): { value: string | null; source: string | null } {
  if (!sourceOverride) {
    return getAutomaticCascadePreview(layers, field, activeSource);
  }

  const layer = getLayerBySource(layers, sourceOverride);
  return {
    value: getLayerValue(layer, field),
    source: layer?.sourceId ?? sourceOverride,
  };
}

function buildFieldProvenancePatch(
  field: MetadataFieldDefinition,
  source: string | null,
): Record<string, string | null> {
  return {
    [field.overrideKey]: source,
    [field.key]: source,
  };
}

interface MetadataValueInputProps {
  field: MetadataFieldDefinition;
  value: string;
  onChange: (value: string) => void;
  taxonomy: MetadataTaxonomy;
  label?: string;
  ariaLabel?: string;
  preferNativeSelect?: boolean;
}

function MetadataValueInput({
  field,
  value,
  onChange,
  taxonomy,
  label,
  ariaLabel,
  preferNativeSelect = false,
}: MetadataValueInputProps) {
  const commonProps = {
    label,
    'aria-label': ariaLabel,
  };

  if (field.kind === 'textarea') {
    return (
      <Textarea
        {...commonProps}
        minRows={3}
        maxRows={6}
        autosize
        value={value}
        onChange={(event) => onChange(event.currentTarget.value)}
      />
    );
  }

  if (field.kind === 'genre') {
    if (preferNativeSelect) {
      return (
        <NativeSelect
          {...commonProps}
          data={[{ value: '', label: 'No user override' }, ...taxonomy.genreOptions]}
          value={value}
          onChange={(event) => onChange(event.currentTarget.value)}
        />
      );
    }

    return (
      <Select
        {...commonProps}
        data={taxonomy.genreOptions}
        value={value || null}
        onChange={(nextValue) => onChange(nextValue ?? '')}
        searchable
        clearable
        comboboxProps={{ withinPortal: false }}
        nothingFoundMessage="No genre in taxonomy"
      />
    );
  }

  if (field.kind === 'date') {
    return (
      <TextInput
        {...commonProps}
        type="date"
        value={value}
        onChange={(event) => onChange(event.currentTarget.value)}
      />
    );
  }

  if (field.kind === 'players') {
    return (
      <NumberInput
        {...commonProps}
        value={numberInputValue(value)}
        min={1}
        max={16}
        allowDecimal={false}
        allowNegative={false}
        clampBehavior="strict"
        onChange={(nextValue) => onChange(numberValueToString(nextValue))}
      />
    );
  }

  if (field.kind === 'rating') {
    const ratingValue = numberInputValue(value);
    const sliderValue = ratingValue === '' ? 50 : ratingValue;

    return (
      <Stack gap={6}>
        <NumberInput
          {...commonProps}
          value={ratingValue}
          min={1}
          max={100}
          allowDecimal={false}
          allowNegative={false}
          clampBehavior="strict"
          description="1-100"
          onChange={(nextValue) => onChange(numberValueToString(nextValue))}
        />
        <Slider
          aria-label={`${label ?? ariaLabel ?? field.label} range`}
          min={1}
          max={100}
          value={sliderValue}
          onChange={(nextValue) => onChange(String(nextValue))}
          marks={[
            { value: 1, label: '1' },
            { value: 50, label: '50' },
            { value: 100, label: '100' },
          ]}
        />
      </Stack>
    );
  }

  return (
    <TextInput
      {...commonProps}
      value={value}
      onChange={(event) => onChange(event.currentTarget.value)}
    />
  );
}

interface MetadataFieldProps {
  title: TitleDetail;
  titleId: string;
  field: MetadataFieldDefinition;
  enrichmentState?: TitleEnrichmentStateResponse;
  canManageTitles: boolean;
  taxonomy: MetadataTaxonomy;
  opened: boolean;
  onOpen: () => void;
  onClose: () => void;
}

function MetadataField({
  title,
  titleId,
  field,
  enrichmentState,
  canManageTitles,
  taxonomy,
  opened,
  onOpen,
  onClose,
}: MetadataFieldProps) {
  const [busy, setBusy] = useState(false);
  const value = getTitleValue(title, field);
  const provenance =
    getMetadataMapValue(enrichmentState?.fieldProvenance, field) ??
    getMetadataMapValue(title.fieldProvenance, field);
  const isDescription = field.key === 'description';
  const isInteractive = canManageTitles && enrichmentState !== undefined;

  const fieldContent = (
    <Stack gap={4}>
      <Group gap="xs" justify="space-between" wrap="nowrap">
        <Group gap="xs" wrap="nowrap">
          <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
            {field.label}
          </Text>
          {provenance && (
            <Badge size="xs" variant="dot" color={getSourceColor(provenance)}>
              {formatSourceLabel(provenance)}
            </Badge>
          )}
        </Group>
        {isInteractive && <IconChevronRight size={14} color="var(--mantine-color-dimmed)" />}
      </Group>
      <Text
        size="sm"
        c={value ? undefined : 'dimmed'}
        lineClamp={isDescription ? 4 : 2}
        style={isDescription ? { maxWidth: '65ch', lineHeight: 1.6 } : undefined}
      >
        {formatFieldValue(field, value, taxonomy)}
      </Text>
      <Text size="xs" c="dimmed">
        {getMetadataMapValue(enrichmentState?.fieldSourceOverrides, field)
          ? 'Selected source'
          : provenance?.toLowerCase() === 'user' ? 'Custom value' : 'Automatic'}
      </Text>
    </Stack>
  );

  if (!isInteractive) {
    return fieldContent;
  }

  return (
    <>
        <UnstyledButton
          aria-label={`${field.label}: ${value ?? 'No value'}`}
          aria-expanded={opened}
          aria-haspopup="dialog"
          className={classes.field}
          onClick={onOpen}
        >
          {fieldContent}
        </UnstyledButton>
      <Modal opened={opened} onClose={() => !busy && onClose()} title={`Edit ${field.label}`} size="lg" closeButtonProps={{ 'aria-label': `Close ${field.label} inspector`, disabled: busy }}>
        <FieldCascadeInspector
          key={opened ? 'open' : 'closed'}
          onSavingChange={setBusy}
          title={title}
          titleId={titleId}
          field={field}
          enrichmentState={enrichmentState}
          activeSource={provenance}
          taxonomy={taxonomy}
          onClose={onClose}
        />
      </Modal>
    </>
  );
}

interface FieldCascadeInspectorProps {
  onSavingChange: (saving: boolean) => void;
  title: TitleDetail;
  titleId: string;
  field: MetadataFieldDefinition;
  enrichmentState: TitleEnrichmentStateResponse;
  activeSource: string | null;
  taxonomy: MetadataTaxonomy;
  onClose: () => void;
}

function FieldCascadeInspector({
  onSavingChange,
  title,
  titleId,
  field,
  enrichmentState,
  activeSource,
  taxonomy,
  onClose,
}: FieldCascadeInspectorProps) {
  const queryClient = useQueryClient();
  const updateMetadata = useUpdateTitleMetadata();
  const setOverrides = useSetFieldOverrides();
  const layers = enrichmentState.layers ?? [];
  const userLayerValue = getLayerValue(getUserLayer(layers), field);
  const currentOverride = getMetadataMapValue(enrichmentState.fieldSourceOverrides, field) ?? '';
  type Mode = 'automatic' | 'source' | 'custom';
  const initialMode: Mode = isSameSource(currentOverride, 'user') || (!currentOverride && userLayerValue !== null)
    ? 'custom' : currentOverride ? 'source' : 'automatic';
  const [mode, setMode] = useState<Mode>(initialMode);
  const [userValue, setUserValue] = useState(userLayerValue ?? '');
  const providers = layers.filter((layer) => !isSameSource(layer.sourceId, 'user') && sourceHasFieldValue(layer, field));
  const [selectedSource, setSelectedSource] = useState(currentOverride || activeSource || providers[0]?.sourceId || '');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const dirty = mode !== initialMode
    || (mode === 'source' && selectedSource !== currentOverride)
    || (mode === 'custom' && normalizedFieldValue(field, userValue) !== userLayerValue);
  const valid = mode === 'automatic' || (mode === 'custom'
    ? normalizedFieldValue(field, userValue) !== null
    : providers.some((layer) => layer.sourceId === selectedSource));

  const handleSave = async () => {
    if (saving || !valid) return;
    setSaving(true);
    onSavingChange(true);
    setError(null);
    try {
      if (mode === 'custom') {
        if (currentOverride && !isSameSource(currentOverride, 'user')) {
          await setOverrides.mutateAsync({ titleId, overrides: { [field.overrideKey]: '' } });
        }
        await updateMetadata.mutateAsync({
          titleId,
          metadata: buildUserMetadataRequest(title, layers, field, normalizedFieldValue(field, userValue)),
        });
      } else if (mode === 'automatic') {
        if (userLayerValue !== null) {
          await updateMetadata.mutateAsync({ titleId, metadata: buildUserMetadataRequest(title, layers, field, null) });
        }
        await setOverrides.mutateAsync({ titleId, overrides: { [field.overrideKey]: '' } });
      } else {
        const preview = getSourceSelectionPreview(layers, field, selectedSource, activeSource);
        await setOverrides.mutateAsync({
          titleId,
          overrides: { [field.overrideKey]: selectedSource },
          optimisticTitle: buildTitleFieldPatch(field, preview.value),
          optimisticFieldProvenance: buildFieldProvenancePatch(field, preview.source),
          optimisticFieldSourceOverrides: { [field.overrideKey]: selectedSource, [field.key]: null },
        });
      }
      await queryClient.invalidateQueries({ queryKey: titleDetailKeys.detail(titleId) });
      notifications.show({ title: 'Metadata updated', message: `${field.label} selection saved.`, color: 'green' });
      onClose();
    } catch {
      setError('Could not finish saving. Some changes may have been applied. Your draft is still here; retry to complete it.');
    } finally {
      setSaving(false);
      onSavingChange(false);
    }
  };

  return (
    <div className={classes.editor}>
      <div className={classes.editorBody}>
        <SegmentedControl fullWidth aria-label="Selection mode" value={mode} disabled={saving}
          onChange={(value) => setMode(value as Mode)}
          data={[{ value: 'automatic', label: 'Automatic' }, { value: 'source', label: 'Choose source' }, { value: 'custom', label: 'Custom' }]} />
        {mode === 'automatic' && (
          <Stack gap="sm" mt="md">
            <Group gap="xs"><IconCheck size={16} /><Text size="sm" fw={600}>Automatic{initialMode === 'automatic' && activeSource ? ` · ${formatSourceLabel(activeSource)}` : ''}</Text></Group>
            {initialMode === 'automatic'
              ? <Text size="sm" className={classes.value}>{formatFieldValue(field, getTitleValue(title, field), taxonomy)}</Text>
              : <Text size="sm" c="dimmed">Use system preferences. Saving removes this field's custom value and source selection.</Text>}
          </Stack>
        )}
        {mode === 'source' && (
          <Stack gap="sm" mt="md" role="radiogroup" aria-label={`${field.label} source`}>
            {providers.map((layer) => {
              const selected = layer.sourceId === selectedSource;
              return (
                <UnstyledButton key={layer.sourceId} role="radio" aria-checked={selected}
                  aria-label={`Select ${formatSourceLabel(layer.sourceId)} for ${field.label}`}
                  disabled={saving} onClick={() => setSelectedSource(layer.sourceId)}
                  onKeyDown={(event) => {
                    const keys = ['ArrowDown', 'ArrowRight', 'ArrowUp', 'ArrowLeft', 'Home', 'End'];
                    if (!keys.includes(event.key)) return;
                    event.preventDefault();
                    const index = providers.indexOf(layer);
                    const next = event.key === 'Home' ? 0 : event.key === 'End' ? providers.length - 1
                      : (index + (event.key === 'ArrowDown' || event.key === 'ArrowRight' ? 1 : -1) + providers.length) % providers.length;
                    setSelectedSource(providers[next].sourceId);
                    const options = event.currentTarget.parentElement?.querySelectorAll<HTMLButtonElement>('[role="radio"]');
                    options?.[next]?.focus();
                  }}
                  className={classes.sourceOption} data-selected={selected || undefined}>
                  <Group justify="space-between" wrap="nowrap" mb="xs">
                    <Text fw={600} size="sm">{formatSourceLabel(layer.sourceId)}</Text>
                    <Group gap={6} wrap="nowrap" style={{ flexShrink: 0 }}>
                      {selected && <><IconCheck size={16} /><Text size="xs">Selected</Text></>}
                    </Group>
                  </Group>
                  <Text size="sm" className={classes.value}>{formatFieldValue(field, getLayerValue(layer, field), taxonomy)}</Text>
                </UnstyledButton>
              );
            })}
            {providers.length === 0 && <Text size="sm" c="dimmed">No source values available.</Text>}
          </Stack>
        )}
        {mode === 'custom' && (
          <Stack mt="md">
            <MetadataValueInput field={field} value={userValue} onChange={setUserValue}
              taxonomy={taxonomy} label="Custom value" ariaLabel={`${field.label} user override`} preferNativeSelect />
          </Stack>
        )}
        {error && <Alert color="red" mt="md">{error}</Alert>}
      </div>
      <Group className={classes.editorFooter} justify="flex-end">
        <Button variant="subtle" color="gray" onClick={onClose} disabled={saving}>Cancel</Button>
        <Button {...workspaceActionProps} leftSection={<IconDeviceFloppy size={16} />}
          onClick={handleSave} loading={saving} disabled={!dirty || !valid}>Save changes</Button>
      </Group>
    </div>
  );
}


/**
 * Panel displaying and editing title metadata with inline cascade inspection.
 */
export function MetadataPanel({ title, titleId }: MetadataPanelProps) {
  const { canManageTitles } = usePermissions();
  const [openFieldKey, setOpenFieldKey] = useState<MetadataFieldKey | null>(null);
  const applyProvider = useSetFieldOverrides();
  const [providerPreview, setProviderPreview] = useState(false);
  const [providerId, setProviderId] = useState<string | null>(null);
  const { data: catalogFilters } = useCatalogFilterFacets();
  const {
    data: enrichmentState,
    isLoading: enrichmentLoading,
  } = useTitleEnrichment(canManageTitles ? titleId : undefined);
  const enrichmentLayers = enrichmentState?.layers ?? [];
  const providers = enrichmentLayers.filter((layer) => layer.sourceType === 'Provider');
  const selectedProvider = providers.find((layer) => layer.sourceId === providerId) ?? providers[0];
  const previewFields = METADATA_FIELDS.map((field) => ({
    field,
    next: getLayerValue(selectedProvider, field),
    preserved: !!getMetadataMapValue(enrichmentState?.fieldSourceOverrides, field)
      || getLayerValue(getUserLayer(enrichmentLayers), field) !== null,
  }));
  const eligibleFields = previewFields.filter((item) => !item.preserved && item.next !== null);
  const handleApplyProvider = async () => {
    if (!selectedProvider || eligibleFields.length === 0) return;
    try {
      await applyProvider.mutateAsync({
        titleId,
        overrides: Object.fromEntries(eligibleFields.map(({ field }) => [field.overrideKey, selectedProvider.sourceId])),
        optimisticTitle: Object.assign({}, ...eligibleFields.map(({ field, next }) => buildTitleFieldPatch(field, next))),
        optimisticFieldProvenance: Object.fromEntries(eligibleFields.map(({ field }) => [field.overrideKey, selectedProvider.sourceId])),
        optimisticFieldSourceOverrides: Object.fromEntries(eligibleFields.map(({ field }) => [field.overrideKey, selectedProvider.sourceId])),
      });
      setProviderPreview(false);
      notifications.show({ title: 'Source applied', message: `${eligibleFields.length} fields now use ${formatSourceLabel(selectedProvider.sourceId)}.`, color: 'green' });
    } catch {
      notifications.show({ title: 'Update failed', message: 'Could not apply this provider. Your preview is still open.', color: 'red' });
    }
  };
  const taxonomy = useMemo<MetadataTaxonomy>(() => {
    const genreField = METADATA_FIELDS[1];

    return {
      genreOptions: buildTaxonomyOptions(
        BASE_GENRE_TAXONOMY,
        catalogFilters?.genres,
        [
          getTitleValue(title, genreField),
          ...enrichmentLayers.map((layer) => getLayerValue(layer, genreField)),
        ],
      ),
    };
  }, [catalogFilters?.genres, enrichmentLayers, title]);

  return (
    <Stack gap="lg">
      <Group justify="space-between">
        <Text fw={600}>Title metadata</Text>
        {canManageTitles && providers.length > 0 && (
          <Menu position="bottom-end">
            <Menu.Target>
              <ActionIcon variant="subtle" color="gray" aria-label="Metadata actions"><IconDots size={18} /></ActionIcon>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Item leftSection={<IconDatabase size={16} />} onClick={() => setProviderPreview(true)}>Apply from provider</Menu.Item>
            </Menu.Dropdown>
          </Menu>
        )}
      </Group>
        <Grid>
          <Grid.Col span={12}>
            <MetadataField
              title={title}
              titleId={titleId}
              field={METADATA_FIELDS[0]}
              enrichmentState={enrichmentState}
              canManageTitles={canManageTitles}
              taxonomy={taxonomy}
              opened={openFieldKey === METADATA_FIELDS[0].key}
              onOpen={() => setOpenFieldKey(METADATA_FIELDS[0].key)}
              onClose={() =>
                setOpenFieldKey((currentKey) =>
                  currentKey === METADATA_FIELDS[0].key ? null : currentKey,
                )
              }
            />
          </Grid.Col>
          {METADATA_FIELDS.slice(1).map((field) => (
            <Grid.Col key={field.key} span={{ base: 12, md: 6 }}>
              <MetadataField
                title={title}
                titleId={titleId}
                field={field}
                enrichmentState={enrichmentState}
                canManageTitles={canManageTitles}
                taxonomy={taxonomy}
                opened={openFieldKey === field.key}
                onOpen={() => setOpenFieldKey(field.key)}
                onClose={() =>
                  setOpenFieldKey((currentKey) => (currentKey === field.key ? null : currentKey))
                }
              />
            </Grid.Col>
          ))}
          {title.lastEnrichedAt && (
            <Grid.Col span={12}>
              <Text size="xs" c="dimmed">
                Last enriched: {new Date(title.lastEnrichedAt).toLocaleDateString()}
              </Text>
            </Grid.Col>
          )}
        </Grid>

      {canManageTitles && (
        <ProviderMatches
          titleId={titleId}
          titleName={title.name}
        />
      )}
      <Modal opened={providerPreview} onClose={() => !applyProvider.isPending && setProviderPreview(false)} title="Apply from provider" size="lg">
        <Stack>
          <Select label="Provider" value={selectedProvider?.sourceId ?? null} onChange={setProviderId} disabled={applyProvider.isPending} data={providers.map((layer) => ({ value: layer.sourceId, label: formatSourceLabel(layer.sourceId) }))} />
          <Text size="sm" c="dimmed">Existing custom values and selected sources will be kept.</Text>
          {previewFields.map(({ field, next, preserved }) => (
            <div key={field.key} className={classes.candidate}>
              <Group justify="space-between"><Text fw={600} size="sm">{field.label}</Text><Badge color={preserved ? 'gray' : next !== null ? 'teal' : 'gray'} variant="light">{preserved ? 'Keep existing' : next !== null ? 'Use provider' : 'Unavailable'}</Badge></Group>
              <Text size="sm" c="dimmed" mt="xs">{formatFieldValue(field, getTitleValue(title, field), taxonomy)}</Text>
              {!preserved && next !== null && <Text size="sm" mt="xs" style={{ overflowWrap: 'anywhere' }}>{formatFieldValue(field, next, taxonomy)}</Text>}
            </div>
          ))}
          <Button {...workspaceActionProps} onClick={handleApplyProvider} loading={applyProvider.isPending} disabled={eligibleFields.length === 0} leftSection={<IconCheck size={16} />}>Apply to {eligibleFields.length} fields</Button>
        </Stack>
      </Modal>
    </Stack>
  );
}
