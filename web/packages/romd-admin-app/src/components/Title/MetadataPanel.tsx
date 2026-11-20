import {
  Alert,
  Badge,
  Button,
  Group,
  Modal,
  NumberInput,
  Radio,
  Select,
  Stack,
  Text,
  Textarea,
  TextInput,
  Title,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type { MetadataLayer } from '@romd/admin-api-client';
import { IconDatabase } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { Fragment, useEffect, useId, useRef, useState } from 'react';
import { invalidateTitle } from '../../hooks/api/titleCache';
import { TitleConflictError } from '../../hooks/api/titleConflict';
import { useCatalogFilterFacets } from '../../hooks/api/useLibraryManagement';
import { usePlatform } from '../../hooks/api/usePlatforms';
import { useUpdateTitleMetadata } from '../../hooks/api/useTitleActions';
import type { TitleDetailView as TitleDetail } from '../../hooks/api/useTitleDetail';
import { useSetFieldOverrides, useTitleEnrichment } from '../../hooks/api/useTitleEnrichment';
import { usePermissions } from '../../hooks/usePermissions';
import { DraftGuardProvider, useDiscardEpoch, useDraft } from '../Administration/DraftGuard';
import { EditorFooter } from '../Workspace/EditorFooter';
import { SourceMarker } from '../Workspace/SourceMarker';
import workspace from '../Workspace/Workspace.module.css';
import { workspaceActionProps } from '../Workspace/workspaceActions';
import { MetadataIdentifiers } from './MetadataIdentifiers';
import layout from './MetadataPanel.module.css';
import {
  BASE_GENRE_TAXONOMY,
  buildTaxonomyOptions,
  buildWrites,
  type Draft,
  displayValue,
  emptyLayers,
  type FieldEdit,
  fieldSource,
  findLayer,
  layerValue,
  METADATA_FIELDS,
  type MetadataFieldDefinition,
  markerText,
  type Option,
  previewValue,
  sameSource,
  sourceLabel,
  sourceOptions,
} from './metadataDraft';
import { TitleConflictAlert } from './TitleConflictAlert';
import type { TitleEditorOutcome } from './titleWorkbenchModel';

interface MetadataPanelProps extends TitleEditorOutcome {
  initialField?: string;
  fieldRequestKey?: string;
  title: TitleDetail;
  titleId: string;
}

function MetadataInput({
  field,
  value,
  id,
  onChange,
  genreOptions,
  readOnly,
  error,
  placeholder,
}: {
  field: MetadataFieldDefinition;
  value: string;
  id: string;
  onChange: (value: string) => void;
  genreOptions: Option[];
  readOnly: boolean;
  error?: string;
  placeholder?: string;
}) {
  const commonProps = {
    id,
    readOnly,
    error,
    placeholder,
  };
  switch (field.kind) {
    case 'textarea':
      return (
        <Textarea
          {...commonProps}
          minRows={3}
          maxRows={9}
          autosize
          value={value}
          onChange={(event) => onChange(event.currentTarget.value)}
        />
      );
    case 'genre':
      return (
        <Select
          {...commonProps}
          data={genreOptions}
          value={value || null}
          onChange={(next) => onChange(next ?? '')}
          searchable
          nothingFoundMessage="No genre in taxonomy"
        />
      );
    case 'players':
    case 'rating':
      return (
        <NumberInput
          {...commonProps}
          value={value}
          min={1}
          max={field.kind === 'players' ? 16 : 100}
          allowDecimal={field.kind === 'rating'}
          allowNegative={false}
          clampBehavior="strict"
          onChange={(next) => onChange(displayValue(next))}
          suffix={field.kind === 'rating' ? ' / 100' : undefined}
        />
      );
    default:
      return (
        <TextInput
          {...commonProps}
          type={field.kind === 'date' ? 'date' : 'text'}
          required={field.key === 'name'}
          value={value}
          onChange={(event) => onChange(event.currentTarget.value)}
        />
      );
  }
}

function SourceChoices({
  field,
  edit,
  layers,
  override,
  onChange,
}: {
  field: MetadataFieldDefinition;
  edit?: FieldEdit;
  layers: MetadataLayer[];
  override?: string;
  onChange: (edit?: FieldEdit) => void;
}) {
  const options = sourceOptions(layers, field);
  const selection =
    edit?.kind === 'automatic'
      ? 'automatic'
      : edit?.kind === 'source'
        ? edit.sourceId
        : !edit && override && options.some((layer) => sameSource(layer.sourceId, override))
          ? override
          : '';
  return (
    <Stack gap="sm">
      <Radio.Group
        label={`${field.label} source`}
        value={selection}
        onChange={(next) =>
          onChange(
            next === 'automatic'
              ? {
                  kind: 'automatic',
                }
              : {
                  kind: 'source',
                  sourceId: next,
                },
          )
        }
      >
        <Stack
          gap="sm"
          mt="xs"
        >
          <Radio
            value="automatic"
            label="Use source automatically"
            description="Follow system preferences and remove the edited value."
          />
          {options.map((layer) => (
            <Radio
              key={layer.sourceId}
              value={layer.sourceId}
              label={`Use ${sourceLabel(layer.sourceId)}`}
              description={
                <Text
                  size="xs"
                  lineClamp={3}
                >
                  {layerValue(layer, field)}
                </Text>
              }
            />
          ))}
        </Stack>
      </Radio.Group>
      {edit && (
        <Button
          {...workspaceActionProps}
          variant="subtle"
          size="compact-sm"
          onClick={() => onChange()}
        >
          Restore saved field
        </Button>
      )}
    </Stack>
  );
}

export function MetadataPanel(props: MetadataPanelProps) {
  return (
    <DraftGuardProvider>
      <MetadataForm
        key={props.titleId}
        {...props}
      />
    </DraftGuardProvider>
  );
}

function MetadataForm({
  title,
  titleId,
  initialField,
  fieldRequestKey,
  density = 'comfortable',
  onResolved,
  onDismissed,
}: MetadataPanelProps) {
  const cache = useQueryClient();
  const { canManageTitles } = usePermissions();
  const enrichment = useTitleEnrichment(canManageTitles ? titleId : undefined);
  const state = enrichment.data;
  const layers = state?.layers ?? emptyLayers;
  const { data: platform } = usePlatform(title.systemKey);
  const { data: catalogFilters } = useCatalogFilterFacets();
  const update = useUpdateTitleMetadata();
  const setOverrides = useSetFieldOverrides();
  const formId = useId();
  const discardEpoch = useDiscardEpoch();
  const handledDiscard = useRef(discardEpoch);
  const active = useRef(true);
  const draftSession = useRef(0);
  const [draft, setDraft] = useState<Draft>();
  const [saving, setSaving] = useState(false);
  const [problem, setProblem] = useState<'conflict' | 'partial-conflict' | 'partial' | 'failure'>();
  const [providerPreview, setProviderPreview] = useState(false);
  const [providerId, setProviderId] = useState<string | null>(null);

  useEffect(() => {
    active.current = true;
    return () => {
      active.current = false;
    };
  }, []);
  useEffect(() => {
    if (handledDiscard.current === discardEpoch) return;
    handledDiscard.current = discardEpoch;
    draftSession.current += 1;
    setDraft(undefined);
    setProblem(undefined);
    setProviderPreview(false);
  }, [
    discardEpoch,
  ]);

  const requestedField = METADATA_FIELDS.find(
    (field) => field.key.toLowerCase() === initialField?.toLowerCase(),
  );
  const requestedId = requestedField ? `${formId}-${requestedField.key}` : undefined;
  const inputRequest = useRef<string | undefined>(undefined);
  useEffect(() => {
    const request = requestedId
      ? JSON.stringify([
          requestedId,
          fieldRequestKey,
        ])
      : undefined;
    if (request === inputRequest.current) return;
    inputRequest.current = request;
    if (requestedId) document.getElementById(requestedId)?.focus();
  }, [
    requestedId,
    fieldRequestKey,
  ]);

  const dirty = !!draft;
  useDraft(dirty, 'Your metadata draft has not been saved.');
  const invalidField = METADATA_FIELDS.find((field) => {
    const edit = draft?.edits[field.key];
    return edit?.kind === 'edited' && !edit.value.trim();
  });
  const hasSourceEdits = METADATA_FIELDS.some(
    (field) => field.overrideKey && draft?.edits[field.key],
  );
  const canSave =
    canManageTitles &&
    (!hasSourceEdits || !!state) &&
    !!draft &&
    !saving &&
    !invalidField &&
    problem !== 'conflict' &&
    problem !== 'partial-conflict';

  const discard = () => {
    draftSession.current += 1;
    setDraft(undefined);
    setProblem(undefined);
    onDismissed?.();
  };

  const editField = (field: MetadataFieldDefinition, edit?: FieldEdit) => {
    const removesDraft =
      !edit ||
      (edit.kind === 'edited' && edit.value === displayValue(title[field.key])) ||
      (edit.kind === 'source' &&
        sameSource(fieldSource(state?.fieldSourceOverrides, field), edit.sourceId)) ||
      (edit.kind === 'automatic' &&
        !fieldSource(state?.fieldSourceOverrides, field) &&
        !layerValue(findLayer(layers, 'user'), field));
    setDraft((current) => {
      const edits = {
        ...current?.edits,
      };
      if (removesDraft) delete edits[field.key];
      else edits[field.key] = edit;
      return Object.keys(edits).length > 0
        ? {
            edits,
            revision: current?.revision ?? title.revision,
            applied: current?.applied,
          }
        : undefined;
    });
    if (!draft?.applied && problem === 'failure') setProblem(undefined);
  };

  const save = async () => {
    if (!canSave || !draft) return;
    const session = draftSession.current;
    setSaving(true);
    setProblem(undefined);
    const remaining = [
      ...(draft.remaining ?? buildWrites(draft.edits, state)),
    ];
    let revision = draft.revision;
    let applied = draft.applied ?? 0;
    try {
      while (remaining.length) {
        const step = remaining[0];
        const accepted =
          step.kind === 'metadata'
            ? await update.mutateAsync({
                titleId,
                revision,
                metadata: step.body,
              })
            : await setOverrides.mutateAsync({
                titleId,
                revision,
                overrides: step.body,
              });
        revision = accepted.revision;
        applied += 1;
        remaining.shift();
      }
      if (active.current && session === draftSession.current) {
        setDraft(undefined);
        onResolved?.();
        notifications.show({
          title: 'Metadata saved',
          message: draft.edits.name?.kind === 'edited' ? draft.edits.name.value.trim() : title.name,
          color: 'green',
        });
      }
    } catch (reason) {
      if (!active.current || session !== draftSession.current) return;
      const conflicted = reason instanceof TitleConflictError;
      setProblem(
        conflicted ? (applied ? 'partial-conflict' : 'conflict') : applied ? 'partial' : 'failure',
      );
      setDraft({
        ...draft,
        revision,
        remaining,
        applied,
      });
    } finally {
      if (active.current) setSaving(false);
    }
  };

  const genreOptions = buildTaxonomyOptions(BASE_GENRE_TAXONOMY, catalogFilters?.genres, [
    title.genre ?? null,
    ...layers.map((layer) => layer.genre ?? null),
  ]);

  const providers = layers.filter((layer) => layer.sourceType === 'Provider');
  const provider = providers.find((layer) => layer.sourceId === providerId) ?? providers[0];
  const providerPreviewFields = METADATA_FIELDS.filter((field) => field.overrideKey).map(
    (field) => ({
      field,
      value: layerValue(provider, field),
      preserved:
        !!draft?.edits[field.key] ||
        !!fieldSource(state?.fieldSourceOverrides, field) ||
        !!layerValue(findLayer(layers, 'user'), field),
    }),
  );
  const providerFields = providerPreviewFields.filter(
    ({ value, preserved }) => value && !preserved,
  );
  const addProvider = () => {
    if (!provider || providerFields.length === 0) return;
    const fromProvider = Object.fromEntries(
      providerFields.map(({ field }) => [
        field.key,
        {
          kind: 'source',
          sourceId: provider.sourceId,
        },
      ]),
    );
    setDraft((current) => ({
      edits: {
        ...current?.edits,
        ...fromProvider,
      },
      revision: current?.revision ?? title.revision,
      applied: current?.applied,
    }));
    setProviderPreview(false);
  };

  return (
    <Stack gap="md">
      <form
        aria-label="Title metadata"
        className={`${workspace.panel} ${layout.panel}`}
        data-density={density}
        onSubmit={(event) => {
          event.preventDefault();
          void save();
        }}
      >
        <Group justify="space-between">
          <Title
            order={2}
            className={workspace.sectionHeading}
          >
            Title metadata
          </Title>
          {canManageTitles && providers.length > 0 && (
            <Button
              {...workspaceActionProps}
              variant="default"
              leftSection={<IconDatabase size={16} />}
              disabled={saving}
              onClick={() => setProviderPreview(true)}
            >
              Apply provider values
            </Button>
          )}
        </Group>
        {canManageTitles && enrichment.isError && (
          <Alert
            color="yellow"
            title="Metadata sources could not be loaded"
          >
            <Button
              variant="subtle"
              size="compact-sm"
              onClick={() => void enrichment.refetch()}
            >
              Retry sources
            </Button>
          </Alert>
        )}
        <fieldset
          className={layout.fields}
          disabled={saving}
        >
          {METADATA_FIELDS.map((field) => {
            const id = `${formId}-${field.key}`;
            const edit = draft?.edits[field.key];
            const override = fieldSource(state?.fieldSourceOverrides, field);
            const provenance =
              fieldSource(state?.fieldProvenance, field) ??
              fieldSource(title.fieldProvenance, field);
            const value = previewValue(title, field, edit, layers, !!override, provenance);
            return (
              <Fragment key={field.key}>
                <div
                  className={`${layout.field} ${field.kind === 'textarea' ? layout.wide : ''}`}
                  data-highlighted={field.key === requestedField?.key || undefined}
                >
                  <Group
                    justify="space-between"
                    gap="xs"
                    className={layout.fieldHeading}
                  >
                    <Text
                      component="label"
                      htmlFor={id}
                      size="sm"
                      fw={600}
                    >
                      {field.label}
                    </Text>
                    <SourceMarker
                      label={field.label}
                      value={markerText(field, edit, provenance, override)}
                      disabled={saving}
                    >
                      {canManageTitles && state && field.overrideKey && (
                        <SourceChoices
                          field={field}
                          edit={edit}
                          layers={layers}
                          override={override}
                          onChange={(next) => editField(field, next)}
                        />
                      )}
                    </SourceMarker>
                  </Group>
                  <MetadataInput
                    field={field}
                    value={value}
                    id={id}
                    onChange={(next) =>
                      editField(field, {
                        kind: 'edited',
                        value: next,
                      })
                    }
                    readOnly={!canManageTitles || (!!field.overrideKey && !state) || saving}
                    genreOptions={genreOptions}
                    error={
                      invalidField?.key === field.key
                        ? field.key === 'name'
                          ? 'Enter a title name.'
                          : 'Enter a value or choose a source.'
                        : undefined
                    }
                    placeholder={
                      edit?.kind === 'automatic' && !value ? 'Resolved on save' : undefined
                    }
                  />
                </div>
                {field.key === 'name' && (
                  <div className={layout.platform}>
                    <TextInput
                      label="Platform"
                      value={platform?.name ?? title.systemKey}
                      readOnly
                    />
                  </div>
                )}
              </Fragment>
            );
          })}
        </fieldset>
        {problem === 'failure' && <Alert color="red">Could not save your draft. Try again.</Alert>}
        {problem === 'partial' && (
          <Alert color="yellow">
            Some changes were saved. Your draft still contains the remaining changes. Save again to
            finish.
          </Alert>
        )}
        {(problem === 'conflict' || problem === 'partial-conflict') && (
          <>
            {problem === 'partial-conflict' && (
              <Alert color="yellow">Some changes were saved before the conflict.</Alert>
            )}
            <TitleConflictAlert
              draftLabel="metadata changes"
              onReload={async () => {
                await invalidateTitle(cache, titleId);
                if (active.current) discard();
              }}
            />
          </>
        )}
        {canManageTitles && (
          <EditorFooter
            dirty={dirty}
            saving={saving}
            disabled={!canSave}
            onSave={() => void save()}
            onDiscard={discard}
          />
        )}
      </form>
      <MetadataIdentifiers
        titleId={titleId}
        externalIds={state?.externalIds}
      />
      <Modal
        opened={providerPreview}
        onClose={() => setProviderPreview(false)}
        title="Apply provider values"
      >
        <Stack>
          <Select
            label="Provider"
            value={provider?.sourceId ?? null}
            onChange={setProviderId}
            data={providers.map((layer) => ({
              value: layer.sourceId,
              label: sourceLabel(layer.sourceId),
            }))}
          />
          <Text
            size="sm"
            c="dimmed"
          >
            Adds available values to your draft and keeps edited fields and locked sources.
          </Text>
          {providerPreviewFields.map(({ field, value, preserved }) => (
            <div
              key={field.key}
              className={layout.providerField}
            >
              <Group justify="space-between">
                <Text
                  fw={600}
                  size="sm"
                >
                  {field.label}
                </Text>
                <Badge variant="default">
                  {preserved ? 'Keep existing' : value ? 'Use provider' : 'Unavailable'}
                </Badge>
              </Group>
              {!preserved && value && (
                <Text
                  size="sm"
                  lineClamp={3}
                >
                  {value}
                </Text>
              )}
            </div>
          ))}
          <Button
            {...workspaceActionProps}
            onClick={addProvider}
            disabled={providerFields.length === 0}
          >
            Add to draft ({providerFields.length})
          </Button>
        </Stack>
      </Modal>
    </Stack>
  );
}
