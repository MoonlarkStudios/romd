import { Alert, Button, Group, Skeleton, Stack, Text, Textarea } from '@mantine/core';
import {
  getSignInMessageSettings,
  replaceSignInMessage,
  type SignInMessageSettings as SavedSignInMessage,
} from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { apiError } from '../../components/Administration/apiError';
import { UnsavedChanges } from '../../components/Administration/UnsavedChanges';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useAuth } from '../../contexts/AuthContext';
import { ifMatch, revisionFromETag } from '../../hooks/api/titleConflict';

const queryKey = [
  'server',
  'sign-in-message',
] as const;
const maxLength = 500;

function verifyRevision(
  settings: SavedSignInMessage,
  response: Response | undefined,
): SavedSignInMessage {
  if (revisionFromETag(response?.headers.get('ETag')) !== settings.revision)
    throw new Error('Could not verify the saved message revision. Reload the saved message.');
  return settings;
}

function MessageEditor() {
  const cache = useQueryClient();
  const query = useQuery({
    queryKey,
    queryFn: async ({ signal }) => {
      const result = await getSignInMessageSettings({
        signal,
      });
      if (!result.data) throw new Error('Could not load the sign-in message.');
      return verifyRevision(result.data, result.response);
    },
  });
  const [draft, setDraft] = useState<SavedSignInMessage | null>(null);
  const save = useMutation({
    mutationFn: async (value: SavedSignInMessage) => {
      const result = await replaceSignInMessage({
        body: {
          message: value.message,
        },
        headers: ifMatch(value.revision),
      });
      if (!result.data)
        throw new Error(apiError(result.error, 'Could not save the sign-in message.'));
      return verifyRevision(result.data, result.response);
    },
    onSuccess: async (settings) => {
      await cache.cancelQueries({
        queryKey,
      });
      cache.setQueryData(queryKey, settings);
      setDraft(null);
    },
  });

  if (query.isPending) return <Skeleton height={112} />;
  if (!query.data) {
    return (
      <Alert
        color="red"
        role="alert"
      >
        {query.error.message}
        <Button
          variant="subtle"
          loading={query.isFetching}
          onClick={() => void query.refetch()}
        >
          Reload
        </Button>
      </Alert>
    );
  }

  const settings = query.data;
  return (
    <Stack
      component="form"
      gap="sm"
      mt="md"
      onSubmit={(event) => {
        event.preventDefault();
        if (draft !== null) save.mutate(draft);
      }}
    >
      <UnsavedChanges dirty={draft !== null} />
      <Textarea
        label="Sign-in message"
        description="Visible to anyone who opens a ROMD sign-in page. Leave blank to hide it."
        value={draft?.message ?? settings.message}
        onChange={(event) => {
          const message = event.currentTarget.value;
          setDraft((current) =>
            message === settings.message
              ? null
              : {
                  revision: current?.revision ?? settings.revision,
                  message,
                },
          );
        }}
        maxLength={maxLength}
        disabled={save.isPending}
      />
      {save.isError && (
        <Alert
          color="red"
          role="alert"
        >
          {save.error.message}
          <Button
            variant="subtle"
            loading={query.isFetching}
            onClick={async () => {
              if (
                draft !== null &&
                !window.confirm('Discard the draft and reload the saved message?')
              )
                return;
              const result = await query.refetch();
              if (result.isSuccess) {
                setDraft(null);
                save.reset();
              }
            }}
          >
            Reload saved message
          </Button>
        </Alert>
      )}
      <Group
        justify="flex-end"
        gap="sm"
      >
        {save.isSuccess && draft === null && (
          <Text
            size="sm"
            c="dimmed"
            role="status"
          >
            Sign-in message saved.
          </Text>
        )}
        <Button
          {...workspaceActionProps}
          type="submit"
          disabled={draft === null}
          loading={save.isPending}
        >
          Save sign-in message
        </Button>
      </Group>
    </Stack>
  );
}

export function SignInMessageSettings() {
  const { user } = useAuth();
  return user?.roles.includes('Admin') ? <MessageEditor /> : null;
}
