import { Alert, Button, Group, Skeleton, Stack, Text } from '@mantine/core';
import type { ManagedSystemSetupDetails, PlatformAlias } from '@romd/admin-api-client';
import {
  deleteSystemAlias,
  deleteSystemOverride,
  updateManagedSystemPresentation,
} from '@romd/admin-api-client';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import {
  managedSystemKeys,
  useManagedSystemDetails,
  useRefreshSystemPresentation,
  useSystemNameAliasValidation,
} from '../../hooks/api/useManagedSystems';
import {
  platformKeys,
  usePlatformAliases,
  usePlatformOverrides,
} from '../../hooks/api/usePlatforms';
import { usePermissions } from '../../hooks/usePermissions';
import { SystemPresentationFields, validSystemPresentation } from './SystemPresentationFields';
import {
  presentationValues,
  type SystemPresentation,
  setupAliases,
  systemPresentationPatch,
} from './systemPresentation';

type MetadataDraft = {
  value: SystemPresentation;
  original: ManagedSystemSetupDetails;
};

export function SystemMetadata({ systemKey }: { systemKey: string }) {
  const client = useQueryClient();
  const { canEditReferenceData } = usePermissions();
  const details = useManagedSystemDetails(systemKey);
  const aliases = usePlatformAliases(systemKey);
  const builtIn = details.data?.system.ownership === 'Romd';
  const overrides = usePlatformOverrides(systemKey, canEditReferenceData && builtIn);
  const [draft, setDraft] = useState<MetadataDraft>();
  const refreshPresentation = useRefreshSystemPresentation();
  const { aliasesValid, ...aliasFeedback } = useSystemNameAliasValidation(
    draft ? setupAliases(draft.value.nameAliases) : [],
  );
  const save = useMutation({
    mutationFn: async ({ value, original }: MetadataDraft) => {
      const response = await updateManagedSystemPresentation({
        path: {
          systemKey,
        },
        query: {
          enableForSetup: false,
        },
        headers: {
          'If-Match': `"${original.system.revision}"`,
        },
        body: systemPresentationPatch(value, original),
      });
      if (response.response?.status === 412) {
        throw new Error(
          'These details changed while you were editing. Discard your draft to load the latest version.',
        );
      }
      if (!response.data || response.error)
        throw new Error(response.error?.detail || 'System details could not be saved. Try again.');
      return response.data;
    },
    onSuccess: async (updated) => {
      await refreshPresentation(updated);
      await Promise.all([
        details.refetch({
          cancelRefetch: false,
        }),
        aliases.refetch({
          cancelRefetch: false,
        }),
        updated.ownership === 'Romd'
          ? overrides.refetch({
              cancelRefetch: false,
            })
          : undefined,
      ]);
      setDraft(undefined);
    },
  });

  const remove = useMutation({
    mutationFn: async (alias: PlatformAlias) => {
      const response = await deleteSystemAlias({
        path: {
          systemKey,
          aliasId: alias.id,
        },
      });
      if (response.error)
        throw new Error(response.error.detail || 'The alias could not be removed. Try again.');
    },
    onSuccess: async (_, alias) => {
      const key = platformKeys.aliases(systemKey);
      await client.cancelQueries({
        queryKey: key,
      });
      client.setQueryData<PlatformAlias[]>(key, (current) =>
        current?.filter((item) => item.id !== alias.id),
      );
      void client.invalidateQueries({
        queryKey: managedSystemKeys.all,
      });
      await Promise.all([
        client.invalidateQueries({
          queryKey: key,
        }),
        client.invalidateQueries({
          queryKey: managedSystemKeys.nameAliasPreview,
        }),
      ]);
    },
  });
  async function reloadRestoredDescription(revision: string) {
    const latest = await details.refetch();
    if (!latest.isSuccess) {
      void overrides.refetch();
      return;
    }
    const { system } = latest.data;
    // Rebase just the restored description onto the ETag from the reset write.
    setDraft((previous) => {
      if (!previous) return undefined;
      const value = {
        ...previous.value,
        description: system.description ?? '',
      };
      const original = {
        ...previous.original,
        system: {
          ...previous.original.system,
          description: system.description,
          revision,
        },
      };
      return Object.keys(systemPresentationPatch(value, original)).length > 0
        ? {
            value,
            original,
          }
        : undefined;
    });
    await refreshPresentation(system);
    await overrides.refetch({
      cancelRefetch: false,
    });
  }

  const restore = useMutation({
    mutationFn: async (revision: string) => {
      const response = await deleteSystemOverride({
        path: {
          systemKey,
          field: 'description',
        },
        headers: {
          'If-Match': `"${revision}"`,
        },
      });
      if (response.response?.status === 412)
        throw new Error(
          'These details changed while you were editing. Reload the latest details before restoring the description.',
        );
      if (response.error)
        throw new Error(
          response.error.detail || 'The default description could not be restored. Try again.',
        );
      return response.response?.headers.get('ETag')?.replace(/^"|"$/g, '') ?? revision;
    },
    onSuccess: reloadRestoredDescription,
  });
  const busy = save.isPending || remove.isPending || restore.isPending;
  const actionError = restore.error ?? remove.error ?? save.error;
  const confirmation = restore.isSuccess
    ? 'Default description restored.'
    : remove.isSuccess
      ? 'Alias removed.'
      : save.isSuccess
        ? 'System details saved.'
        : undefined;

  if (details.isPending) return <Skeleton height={200} />;
  if (details.isError) {
    return (
      <Alert
        color="red"
        title="System details could not be loaded"
      >
        <Button
          variant="subtle"
          onClick={() =>
            void (restore.isSuccess ? reloadRestoredDescription(restore.data) : details.refetch())
          }
        >
          Try again
        </Button>
      </Alert>
    );
  }
  const current = details.data;
  const value = draft?.value ?? presentationValues(current);
  const dirty =
    !!draft && Object.keys(systemPresentationPatch(draft.value, draft.original)).length > 0;
  const savedDescription = current.system.description ?? '';
  const canClear = value.description.trim().length > 0;
  const canRetryDefaults = builtIn && overrides.isError;
  const canRestore =
    builtIn &&
    overrides.isSuccess &&
    (overrides.data.hasDescription || value.description !== savedDescription);
  const change = (next: SystemPresentation) => {
    save.reset();
    remove.reset();
    restore.reset();
    setDraft({
      value: next,
      original: draft?.original ?? current,
    });
  };
  return (
    <Stack
      gap="sm"
      maw={780}
    >
      <SystemPresentationFields
        value={value}
        systemKey={current.system.key}
        hardwareEditable={canEditReferenceData && current.system.ownership === 'Installation'}
        manufacturers={current.system.manufacturers}
        existingAliases={aliases.data?.filter((alias) => alias.type === 'name')}
        onRemoveAlias={
          canEditReferenceData
            ? (alias) => {
                if (busy) return;
                save.reset();
                restore.reset();
                remove.mutate(alias);
              }
            : undefined
        }
        onChange={canEditReferenceData ? change : undefined}
        descriptionActions={
          canEditReferenceData &&
          (canClear || canRestore || canRetryDefaults) && (
            <Group gap="xs">
              {canClear && (
                <Button
                  size="compact-sm"
                  variant="subtle"
                  color="gray"
                  disabled={busy}
                  onClick={() =>
                    change({
                      ...value,
                      description: '',
                    })
                  }
                >
                  Clear description
                </Button>
              )}
              {canRestore && (
                <Button
                  size="compact-sm"
                  variant="subtle"
                  disabled={busy}
                  loading={restore.isPending}
                  onClick={() => {
                    if (busy) return;
                    if (!overrides.data?.hasDescription) {
                      change({
                        ...value,
                        description: savedDescription,
                      });
                      return;
                    }
                    save.reset();
                    remove.reset();
                    restore.mutate((draft?.original ?? current).system.revision);
                  }}
                >
                  Restore default
                </Button>
              )}
              {canRetryDefaults && (
                <Button
                  size="compact-sm"
                  variant="subtle"
                  disabled={busy}
                  onClick={() => void overrides.refetch()}
                >
                  Retry description defaults
                </Button>
              )}
            </Group>
          )
        }
        {...aliasFeedback}
        disabled={busy}
      />
      {aliases.isError && (
        <Alert
          color="yellow"
          title="Existing aliases could not be loaded"
        >
          <Button
            variant="subtle"
            onClick={() => void aliases.refetch()}
          >
            Retry existing aliases
          </Button>
        </Alert>
      )}
      {actionError && (
        <Alert
          color="red"
          role="alert"
        >
          {actionError.message}
        </Alert>
      )}
      {confirmation && (
        <Text
          component="output"
          size="sm"
        >
          {confirmation}
        </Text>
      )}
      {canEditReferenceData && (
        <Group justify="flex-end">
          {dirty && (
            <Button
              variant="subtle"
              color="gray"
              disabled={busy}
              onClick={() => {
                setDraft(undefined);
                save.reset();
                restore.reset();
                void details.refetch();
              }}
            >
              Discard changes
            </Button>
          )}
          <Button
            {...workspaceActionProps}
            disabled={busy || !dirty || !validSystemPresentation(value) || !aliasesValid}
            loading={save.isPending}
            onClick={() => {
              if (!draft || busy) return;
              remove.reset();
              restore.reset();
              save.mutate(draft);
            }}
          >
            Save changes
          </Button>
        </Group>
      )}
    </Stack>
  );
}
