import {
  Alert,
  Button,
  Group,
  Progress,
  Select,
  Skeleton,
  Stack,
  Text,
  Title,
} from '@mantine/core';
import {
  getSystemEnrichmentDefaults,
  type PlatformMetadataPolicy,
  updateSystemEnrichmentDefaults,
} from '@romd/admin-api-client';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { apiError } from '../../components/Administration/apiError';
import { UnsavedChanges } from '../../components/Administration/UnsavedChanges';
import classes from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useJob } from '../../hooks/api/useJobs';
import { usePermissions } from '../../hooks/usePermissions';

const fields = [
  'Description',
  'Genre',
  'Publisher',
  'Developer',
  'ReleaseDate',
  'Players',
  'Rating',
];
interface PolicySnapshot {
  policy: PlatformMetadataPolicy;
  etag: string;
}

function snapshot(response: {
  data?: PlatformMetadataPolicy;
  error?: unknown;
  response?: Response;
}): PolicySnapshot {
  if (response.error || !response.data)
    throw new Error(apiError(response.error, 'Could not load metadata policy.'));
  const etag = response.response?.headers.get('ETag');
  if (!etag) throw new Error('Could not verify the saved policy. Reload and try again.');
  return {
    policy: response.data,
    etag,
  };
}

function PolicyProgress({ jobId }: { jobId: string }) {
  // Job polling remains sufficient when the socket is disconnected or unavailable.
  const job = useJob(jobId);
  if (job.isError)
    return (
      <Alert color="red">
        Could not load title update progress.
        <Button
          variant="subtle"
          onClick={() => void job.refetch()}
        >
          Retry
        </Button>
      </Alert>
    );
  const data = job.data;
  if (!data || data.jobType !== 'metadata-rematerialization')
    return <Text role="status">Updating titles…</Text>;
  if (data.isTerminal) {
    const deferred = Number(data.deferredCount ?? 0);
    const message =
      data.phase === 'Completed' || data.phase === 'CompletedWithErrors'
        ? `Titles updated.${deferred ? ` ${deferred.toLocaleString()} ${deferred === 1 ? 'title' : 'titles'} will retry automatically.` : ''}`
        : 'Title updates stopped. Check the job for details.';
    return (
      <Alert
        color={deferred || data.phase === 'Failed' ? 'yellow' : 'teal'}
        role="status"
      >
        {message}
      </Alert>
    );
  }
  return (
    <Stack
      gap="xs"
      role="status"
    >
      <Text size="sm">
        {data.phase === 'Pending'
          ? 'Title updates queued.'
          : `Updating titles · ${Number(data.processedCount ?? 0).toLocaleString()} of ${Number(data.totalTitles ?? 0).toLocaleString()} processed`}
      </Text>
      <Progress
        value={Number(data.progressPercent ?? 0)}
        aria-label="Title update progress"
      />
    </Stack>
  );
}

function PolicyForm({ systemKey, initial }: { systemKey: string; initial: PolicySnapshot }) {
  const client = useQueryClient();
  const [baseline, setBaseline] = useState(initial);
  const [draft, setDraft] = useState(initial.policy.defaults);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const changed = fields.some(
    (field) => (draft[field] ?? '') !== (baseline.policy.defaults[field] ?? ''),
  );
  const adopt = (next: PolicySnapshot) => {
    setBaseline(next);
    setDraft(next.policy.defaults);
    client.setQueryData(
      [
        'metadata-policy',
        systemKey,
      ],
      next,
    );
  };
  const reload = async () => {
    setPending(true);
    try {
      await client.cancelQueries({
        queryKey: [
          'metadata-policy',
          systemKey,
        ],
      });
      adopt(
        snapshot(
          await getSystemEnrichmentDefaults({
            path: {
              systemKey,
            },
          }),
        ),
      );
      setError(null);
      setSaved(false);
    } catch (error) {
      setError(apiError(error, 'Could not reload policy.'));
    } finally {
      setPending(false);
    }
  };
  const save = async () => {
    setPending(true);
    setError(null);
    setSaved(false);
    try {
      // A GET started before this write must not replace its returned snapshot later.
      await client.cancelQueries({
        queryKey: [
          'metadata-policy',
          systemKey,
        ],
      });
      const response = await updateSystemEnrichmentDefaults({
        path: {
          systemKey,
        },
        headers: {
          'If-Match': baseline.etag,
        },
        body: {
          defaults: Object.fromEntries(
            fields
              .filter((field) => (draft[field] ?? '') !== (baseline.policy.defaults[field] ?? ''))
              .map((field) => [
                field,
                draft[field] || '',
              ]),
          ),
        },
      });
      const next = snapshot(response);
      // Also cancel reads started while the PATCH was in flight before publishing its snapshot.
      await client.cancelQueries({
        queryKey: [
          'metadata-policy',
          systemKey,
        ],
      });
      // The returned representation and validator become the next edit baseline together.
      adopt(next);
      setSaved(true);
    } catch (error) {
      setError(apiError(error, 'Could not save policy.'));
    } finally {
      setPending(false);
    }
  };
  const sourcePriority = [
    ...new Set(baseline.policy.globalSourcePriority),
  ];
  const options = [
    {
      value: '',
      label: `Automatic (${sourcePriority.join(' → ') || 'available providers'})`,
    },
    ...sourcePriority.map((source) => ({
      value: source,
      label: source.toUpperCase(),
    })),
  ];
  for (const source of Object.values(draft))
    if (source && !options.some((option) => option.value === source))
      options.push({
        value: source,
        label: source,
      });
  return (
    <Stack>
      <UnsavedChanges dirty={changed} />
      <Text
        size="sm"
        c="dimmed"
      >
        Applies to {initial.policy.affectedTitles.toLocaleString()} titles in this system. Title
        overrides and user-entered metadata take priority. A preferred provider falls back to
        automatic priority when it has no value.
      </Text>
      {fields.map((field) => (
        <Select
          key={field}
          label={field === 'ReleaseDate' ? 'Release date' : field}
          data={options}
          value={draft[field] ?? ''}
          disabled={pending}
          onChange={(value) => {
            setDraft((current) => ({
              ...current,
              [field]: value ?? '',
            }));
            setSaved(false);
          }}
        />
      ))}
      {error && (
        <Alert
          color="red"
          title="Policy not saved"
        >
          {error}
        </Alert>
      )}
      {saved && <Alert color="teal">Policy saved.</Alert>}
      {initial.policy.pendingJobId && <PolicyProgress jobId={initial.policy.pendingJobId} />}
      <Group justify="flex-end">
        <Button
          variant="default"
          disabled={pending}
          onClick={() => {
            if (!changed || window.confirm('Discard your changes and reload the saved policy?'))
              void reload();
          }}
        >
          Reload saved policy
        </Button>
        <Button
          {...workspaceActionProps}
          loading={pending}
          disabled={!changed}
          onClick={() => void save()}
        >
          Save policy
        </Button>
      </Group>
    </Stack>
  );
}
export function MetadataPolicy({ systemKey }: { systemKey: string }) {
  const { canEditMetadataPolicy } = usePermissions();
  const query = useQuery({
    queryKey: [
      'metadata-policy',
      systemKey,
    ],
    enabled: canEditMetadataPolicy,
    queryFn: async ({ signal }) => {
      const response = await getSystemEnrichmentDefaults({
        signal,
        path: {
          systemKey,
        },
      });
      return snapshot(response);
    },
  });
  if (!canEditMetadataPolicy) return null;
  return (
    <section className={classes.panel}>
      <Title
        order={2}
        className={classes.sectionHeading}
        mb="md"
      >
        Metadata policy
      </Title>
      {query.isPending && <Skeleton height={240} />}
      {query.isError && (
        <Alert color="red">
          {query.error.message}
          <Button
            variant="subtle"
            onClick={() => void query.refetch()}
          >
            Retry
          </Button>
        </Alert>
      )}
      {query.data && (
        <PolicyForm
          key={systemKey}
          systemKey={systemKey}
          initial={query.data}
        />
      )}
    </section>
  );
}
