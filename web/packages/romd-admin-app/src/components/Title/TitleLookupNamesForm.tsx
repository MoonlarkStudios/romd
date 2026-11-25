import { Alert, Button, Group, Stack, TagsInput, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import {
  getTitleLookupNames,
  replaceTitleLookupNames,
  type TitleLookupNames,
} from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { invalidateTitle } from '../../hooks/api/titleCache';
import { ifMatch, TitleConflictError } from '../../hooks/api/titleConflict';
import { titleDetailKeys } from '../../hooks/api/useTitleDetail';

export function TitleLookupNamesForm({
  titleId,
  onClose,
}: {
  titleId: string;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const queryKey = [
    ...titleDetailKeys.detail(titleId),
    'lookup-names',
  ] as const;
  const [draft, setDraft] = useState<TitleLookupNames>();
  const query = useQuery({
    queryKey,
    refetchOnMount: 'always',
    refetchOnWindowFocus: false,
    queryFn: async ({ signal }) => {
      const response = await getTitleLookupNames({
        path: {
          titleId,
        },
        signal,
      });
      if (response.error || !response.data) throw new Error('Could not load lookup names.');
      return response.data;
    },
  });
  const save = useMutation({
    mutationFn: async (value: TitleLookupNames) => {
      const response = await replaceTitleLookupNames({
        path: {
          titleId,
        },
        body: {
          names: value.names,
        },
        headers: ifMatch(value.revision),
      });
      if (response.response?.status === 412) throw new TitleConflictError();
      if (response.error || !response.data) {
        throw new Error(response.error?.detail ?? 'Could not save lookup names.');
      }
      return response.data;
    },
    onSuccess: (value) => {
      queryClient.setQueryData(queryKey, value);
      void invalidateTitle(queryClient, titleId);
      notifications.show({
        title: 'Lookup names saved',
        message: 'The title is searchable by its saved names.',
        color: 'green',
      });
      onClose();
    },
  });

  if (!query.data) {
    return query.isError ? (
      <Stack>
        <Alert color="red">Could not load lookup names.</Alert>
        <Button
          variant="light"
          onClick={() => query.refetch()}
        >
          Retry
        </Button>
      </Stack>
    ) : (
      <Text role="status">Loading lookup names…</Text>
    );
  }

  const baseline = query.data;
  const conflict = save.error instanceof TitleConflictError;
  return (
    <Stack gap="md">
      <TagsInput
        label="Lookup names"
        description="Press Enter to add a name. Search finds these names and shows the title’s display name."
        placeholder="Add an alternate name"
        splitChars={[]}
        maxTags={Math.max(32, baseline.names.length)}
        maxLength={200}
        value={draft?.names ?? baseline.names}
        disabled={save.isPending || conflict}
        onChange={(names) => {
          if (save.isError) save.reset();
          setDraft({
            names,
            revision: draft?.revision ?? baseline.revision,
          });
        }}
      />
      {conflict ? (
        <Alert color="yellow">
          The title changed while you were editing. Reload its lookup names before saving.
          <Button
            mt="xs"
            variant="light"
            onClick={async () => {
              const latest = await query.refetch();
              if (latest.isSuccess) {
                setDraft(undefined);
                save.reset();
              }
            }}
          >
            Discard draft and reload
          </Button>
        </Alert>
      ) : (
        save.error && <Alert color="red">{save.error.message}</Alert>
      )}
      <Group justify="flex-end">
        <Button
          variant="default"
          onClick={onClose}
        >
          Cancel
        </Button>
        <Button
          disabled={!draft || conflict}
          loading={save.isPending}
          onClick={() => draft && save.mutate(draft)}
        >
          Save lookup names
        </Button>
      </Group>
    </Stack>
  );
}
