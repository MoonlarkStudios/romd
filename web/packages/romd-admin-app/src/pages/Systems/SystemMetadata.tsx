import { Alert, Button, Group, Skeleton, Stack, Text } from '@mantine/core';
import type { ManagedSystemSetupDetails } from '@romd/admin-api-client';
import { updateManagedSystemPresentation } from '@romd/admin-api-client';
import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import {
  useManagedSystemDetails,
  useRefreshSystemPresentation,
  useSystemNameAliasValidation,
} from '../../hooks/api/useManagedSystems';
import { usePlatformAliases } from '../../hooks/api/usePlatforms';
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
  const { canEditReferenceData } = usePermissions();
  const details = useManagedSystemDetails(systemKey);
  const aliases = usePlatformAliases(systemKey);
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
      ]);
      setDraft(undefined);
    },
  });

  if (details.isPending) return <Skeleton height={200} />;
  if (details.isError) {
    return (
      <Alert
        color="red"
        title="System details could not be loaded"
      >
        <Button
          variant="subtle"
          onClick={() => void details.refetch()}
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
        existingAliases={aliases.data
          ?.filter((alias) => alias.type === 'name')
          .map((alias) => alias.value)}
        onChange={
          canEditReferenceData
            ? (next) => {
                save.reset();
                setDraft({
                  value: next,
                  original: draft?.original ?? current,
                });
              }
            : undefined
        }
        {...aliasFeedback}
        disabled={save.isPending}
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
      {save.isError && (
        <Alert
          color="red"
          role="alert"
        >
          {save.error.message}
        </Alert>
      )}
      {save.isSuccess && (
        <Text
          component="output"
          size="sm"
        >
          System details saved.
        </Text>
      )}
      {canEditReferenceData && (
        <Group justify="flex-end">
          {dirty && (
            <Button
              variant="subtle"
              color="gray"
              disabled={save.isPending}
              onClick={() => {
                setDraft(undefined);
                save.reset();
                void details.refetch();
              }}
            >
              Discard changes
            </Button>
          )}
          <Button
            {...workspaceActionProps}
            disabled={!dirty || !validSystemPresentation(value) || !aliasesValid}
            loading={save.isPending}
            onClick={() => draft && save.mutate(draft)}
          >
            Save changes
          </Button>
        </Group>
      )}
    </Stack>
  );
}
