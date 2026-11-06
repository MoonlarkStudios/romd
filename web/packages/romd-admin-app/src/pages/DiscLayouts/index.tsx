import {
  Alert,
  Badge,
  Button,
  FileInput,
  Group,
  NativeSelect,
  Stack,
  Table,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import {
  getCdStorageRecipe,
  listOriginalDiscLayoutAudit,
  listOriginalDiscLayoutCandidates,
  listOriginalDiscLayouts,
  type OriginalDiscDependencyChoice,
  type OriginalDiscLayoutDto,
  startMediaReconciliationSubject,
  updateOriginalDiscLayout,
} from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { Link } from 'react-router';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useUploadGeneric } from '../../hooks/api/useUpload';
import { usePermissions } from '../../hooks/usePermissions';
import { collectPages } from '../../utils/collectPages';
import { formatBytes } from '../../utils/format';
import { createRequestId } from '../../utils/requestId';

export function DiscLayouts() {
  const { hasRole } = usePermissions();
  const [file, setFile] = useState<File | null>(null);
  const upload = useUploadGeneric();
  const query = useQuery({
    queryKey: [
      'originalDiscLayouts',
    ],
    enabled: hasRole('Admin'),
    queryFn: ({ signal }) =>
      collectPages(async (cursor) => {
        const response = await listOriginalDiscLayouts({
          signal,
          query: {
            cursor,
            limit: 100,
          },
        });
        if (response.error || !response.data)
          throw new Error('Could not load original disc layouts.');
        return response.data;
      }),
  });
  if (!hasRole('Admin')) return <Text>Disc descriptor repair requires an administrator.</Text>;
  return (
    <Stack
      className={workspace.page}
      gap="md"
    >
      <Group
        className={workspace.hero}
        justify="space-between"
      >
        <Title
          order={1}
          className={workspace.heading}
        >
          Disc descriptors
        </Title>
        <Button
          variant="light"
          onClick={() => void query.refetch()}
        >
          Refresh
        </Button>
      </Group>
      <section className={workspace.panel}>
        <Stack gap="sm">
          <Text>
            Preserve original CUE files and associate their exact tracks with a catalog release.
            Upload a CUE for repair, or import a ZIP/7z containing the original CUE and tracks.
            Processing happens in Jobs; refresh this list after completion.
          </Text>
          <FileInput
            label="Original descriptor or disc archive"
            accept=".cue,.zip,.7z"
            value={file}
            onChange={setFile}
            clearable
          />
          <Button
            {...workspaceActionProps}
            disabled={!file}
            loading={upload.isPending}
            onClick={() =>
              file &&
              upload.mutate({
                file,
                allowUnidentified: false,
                archiveOnly: false,
                trackedOnly: false,
              })
            }
          >
            Upload for review
          </Button>
          {upload.isError && (
            <Alert
              color="red"
              title="Upload failed"
            >
              {upload.error.message}
            </Alert>
          )}
          {upload.isSuccess && (
            <Text>
              Upload queued. <Link to={`/jobs/${upload.data.jobId}`}>View processing job</Link>
            </Text>
          )}
        </Stack>
      </section>
      {query.isPending && <Text>Loading original disc layouts…</Text>}
      {query.isError && (
        <Alert
          color="red"
          title="Could not load disc layouts"
        >
          <Button onClick={() => void query.refetch()}>Retry</Button>
        </Alert>
      )}
      {query.data?.length === 0 && (
        <Text>No descriptors. Upload an original CUE or import its disc archive to begin.</Text>
      )}
      {query.data?.map((layout) => (
        <LayoutReview
          key={`${layout.id}:${layout.revision}`}
          layout={layout}
        />
      ))}
    </Stack>
  );
}

function LayoutReview({ layout }: { layout: OriginalDiscLayoutDto }) {
  const recipe = useQuery({
    queryKey: [
      'cdCompressionRecipe',
    ],
    queryFn: async () => {
      const response = await getCdStorageRecipe();
      if (response.error || !response.data)
        throw new Error('Could not load CD compression availability.');
      return response.data;
    },
  });
  const compressionRequest = useRef<{
    layoutId: string;
    requestId: string;
  } | null>(null);
  const compression = useMutation({
    mutationFn: async () => {
      if (compressionRequest.current?.layoutId !== layout.id)
        compressionRequest.current = {
          layoutId: layout.id,
          requestId: createRequestId(),
        };
      const response = await startMediaReconciliationSubject({
        path: {
          subjectKind: 'OriginalLayout',
          subjectId: layout.id,
        },
        body: {
          requestId: compressionRequest.current.requestId,
          conversionRequested: true,
        },
      });
      if (response.error || !response.data)
        throw new Error(
          'Compression requires a complete original BIN set matching one catalog release. Reload if this layout changed.',
        );
      return response.data;
    },
  });
  const queryClient = useQueryClient();
  const [reviewing, setReviewing] = useState(false);
  const [historyOpen, setHistoryOpen] = useState(false);
  const history = useQuery({
    queryKey: [
      'originalDiscLayoutAudit',
      layout.id,
      layout.revision,
    ],
    enabled: historyOpen,
    queryFn: ({ signal }) =>
      collectPages(async (cursor) => {
        const response = await listOriginalDiscLayoutAudit({
          signal,
          path: {
            layoutId: layout.id,
          },
          query: {
            cursor,
            limit: 100,
          },
        });
        if (response.error || !response.data)
          throw new Error('Could not load the complete decision history.');
        return response.data;
      }, layout.audit),
  });
  const [releaseId, setReleaseId] = useState('');
  const [choices, setChoices] = useState<Record<string, string>>({});
  const [reason, setReason] = useState('');
  const candidates = useQuery({
    queryKey: [
      'originalDiscLayoutCandidates',
      layout.id,
    ],
    enabled: reviewing,
    queryFn: ({ signal }) =>
      collectPages(async (cursor) => {
        const response = await listOriginalDiscLayoutCandidates({
          signal,
          path: {
            layoutId: layout.id,
          },
          query: {
            cursor,
            limit: 100,
          },
        });
        if (response.error || !response.data)
          throw new Error('Could not load matching catalog content.');
        return response.data;
      }),
  });
  const candidate = candidates.data?.find((c) => c.releaseId === releaseId);
  const cuePaths = layout.dependencies.map((d) => d.path);
  const coveredNames = new Set(
    cuePaths.flatMap(
      (path) => candidate?.files.filter((f) => f.romId === choices[path]).map((f) => f.name) ?? [],
    ),
  );
  const requiredPaths = [
    ...new Set([
      ...cuePaths,
      ...(candidate?.requirements
        .filter((r) => !r.satisfiedByDescriptor && !coveredNames.has(r.name))
        .map((r) => r.name) ?? []),
    ]),
  ];
  const dependencies: OriginalDiscDependencyChoice[] = requiredPaths.flatMap((path) =>
    choices[path]
      ? [
          {
            path,
            romId: choices[path],
          },
        ]
      : [],
  );
  const mutation = useMutation({
    mutationFn: async (action: string) => {
      const response = await updateOriginalDiscLayout({
        headers: {
          'If-Match': `"${layout.revision}"`,
        },
        path: {
          layoutId: layout.id,
        },
        body: {
          action,
          releaseId: action === 'confirm' ? releaseId : null,
          dependencies: action === 'confirm' ? dependencies : null,
          reason: reason || null,
          replacesLayoutId: action === 'confirm' ? (candidate?.currentLayoutId ?? null) : null,
          replacesRevision:
            action === 'confirm' ? (candidate?.currentLayoutRevision ?? null) : null,
        },
      });
      if (response.error)
        throw new Error(
          response.response?.status === 409 || response.response?.status === 412
            ? 'This layout or catalog changed. Reload before confirming a repair.'
            : 'Could not save the descriptor decision. Review the dependency matches.',
        );
    },
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: [
            'originalDiscLayouts',
          ],
        }),
        queryClient.invalidateQueries({
          queryKey: [
            'originalDiscLayoutCandidates',
          ],
        }),
      ]);
    },
  });
  return (
    <section className={workspace.panel}>
      <Stack gap="sm">
        <Group justify="space-between">
          <Text
            component="h2"
            className="romd-section-heading"
            m={0}
          >
            {layout.entryPoint}
          </Text>
          <Badge>{layout.state}</Badge>
        </Group>
        <Text size="sm">
          Original CUE · {formatBytes(layout.descriptorSizeBytes)} · SHA-256{' '}
          {layout.descriptorSha256}
        </Text>
        <Text size="sm">
          Source: {layout.sourceRelativePath} ·{' '}
          <Link to={`/jobs/${layout.sourceJobId}`}>Processing job</Link>
        </Text>
        {layout.issue && (
          <Alert
            color="yellow"
            title="Repair required"
          >
            {layout.issue}
          </Alert>
        )}
        {layout.releaseId && (
          <Text size="sm">
            Bound release: {layout.releaseId}. Confirming a replacement preserves original bytes and
            updates the selected layout.
          </Text>
        )}
        <Table>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Dependency path</Table.Th>
              <Table.Th>Bound content</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {layout.dependencies.map((d) => (
              <Table.Tr key={d.path}>
                <Table.Td>{d.path}</Table.Td>
                <Table.Td>
                  {d.sha256 ? `${d.sha256} · ${formatBytes(d.sizeBytes ?? '0')}` : 'Not bound'}
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
        {recipe.data?.available &&
          [
            'pending',
            'accepted',
          ].includes(layout.state) && (
            <Group>
              <Button
                {...workspaceActionProps}
                variant="light"
                loading={compression.isPending}
                disabled={layout.dependencies.some((dependency) => !dependency.sha256)}
                onClick={() => compression.mutate()}
              >
                Compress to CHD
              </Button>
              {compression.data && (
                <Link to={`/jobs/${compression.data.jobId}`}>Compression job</Link>
              )}
            </Group>
          )}
        {compression.error && <Alert color="red">{compression.error.message}</Alert>}
        <Button
          {...workspaceActionProps}
          variant="light"
          onClick={() => setReviewing(!reviewing)}
        >
          {reviewing ? 'Close repair' : 'Review and repair'}
        </Button>
        {reviewing && (
          <Stack gap="sm">
            <Text size="sm">
              Choose the exact release, then inspect and select the content for every dependency.
              Names help locate candidates; confirmation pins the displayed hashes and sizes.
            </Text>
            {candidates.isPending && <Text>Loading catalog candidates…</Text>}
            {candidates.isError && (
              <Alert
                color="red"
                title="Could not load candidates"
              >
                <Button onClick={() => void candidates.refetch()}>Retry candidates</Button>
              </Alert>
            )}
            <NativeSelect
              label="Catalog release"
              value={releaseId}
              data={[
                {
                  value: '',
                  label: 'Choose a release',
                },
                ...(candidates.data ?? []).map((c) => ({
                  value: c.releaseId,
                  label: `${c.name} · ${c.systemName}`,
                })),
              ]}
              onChange={(e) => {
                setReleaseId(e.currentTarget.value);
                setChoices({});
              }}
            />
            {candidate && (
              <>
                <Text size="sm">Content fingerprint: {candidate.fingerprint}</Text>
                {candidate.currentLayoutId && (
                  <Alert
                    color="yellow"
                    title="Replace selected layout"
                  >
                    Confirming will replace the currently selected original descriptor. Its bytes
                    and decision history remain retained.
                  </Alert>
                )}
                {candidate.requirements
                  .filter((r) => r.satisfiedByDescriptor)
                  .map((r) => (
                    <Text
                      key={r.name}
                      size="sm"
                    >
                      Provided by descriptor: {r.name}
                    </Text>
                  ))}
                {candidate.requirements
                  .filter((r) => !r.originalAvailable && !r.satisfiedByDescriptor)
                  .map((r) => (
                    <Alert
                      key={r.name}
                      color="yellow"
                      title="Missing original companion"
                    >
                      {r.name} · {formatBytes(r.sizeBytes)}. Import its original bytes before
                      confirming.
                    </Alert>
                  ))}
                {requiredPaths.map((path) => (
                  <NativeSelect
                    key={path}
                    label={`Content for ${path}`}
                    value={choices[path] ?? ''}
                    data={[
                      {
                        value: '',
                        label: 'Choose exact track content',
                      },
                      ...candidate.files.map((f) => ({
                        value: f.romId,
                        label: `${f.name} · ${formatBytes(f.sizeBytes)} · ${f.sha256}`,
                      })),
                    ]}
                    onChange={(e) => {
                      const value = e.currentTarget.value;
                      setChoices((current) => ({
                        ...current,
                        [path]: value,
                      }));
                    }}
                  />
                ))}
              </>
            )}
            {candidates.data?.length === 0 && (
              <Text>No candidate releases. Import the matching catalog tracks, then retry.</Text>
            )}
            <TextInput
              label="Repair decision reason"
              value={reason}
              maxLength={1000}
              onChange={(e) => setReason(e.currentTarget.value)}
            />
            <Group>
              <Button
                {...workspaceActionProps}
                disabled={
                  !candidate ||
                  dependencies.length !== requiredPaths.length ||
                  requiredPaths.length === 0 ||
                  candidate.requirements.some(
                    (r) => !r.originalAvailable && !r.satisfiedByDescriptor,
                  )
                }
                loading={mutation.isPending}
                onClick={() => mutation.mutate('confirm')}
              >
                Confirm exact layout
              </Button>
              <Button
                color="red"
                variant="light"
                disabled={mutation.isPending}
                onClick={() => mutation.mutate('reject')}
              >
                Reject descriptor
              </Button>
              <Button
                color="red"
                variant="subtle"
                disabled={
                  mutation.isPending ||
                  ![
                    'accepted',
                    'stale',
                  ].includes(layout.state)
                }
                onClick={() => mutation.mutate('detach')}
              >
                Detach layout
              </Button>
            </Group>
          </Stack>
        )}
        {mutation.isError && (
          <Alert
            color="red"
            title="Could not save repair"
          >
            {mutation.error.message}
            <Button
              variant="subtle"
              onClick={() =>
                void Promise.all([
                  queryClient.invalidateQueries({
                    queryKey: [
                      'originalDiscLayouts',
                    ],
                  }),
                  queryClient.invalidateQueries({
                    queryKey: [
                      'originalDiscLayoutCandidates',
                    ],
                  }),
                ])
              }
            >
              Reload layouts
            </Button>
          </Alert>
        )}
        {(layout.audit.items.length > 0 || layout.audit.hasNextPage) && (
          <details onToggle={(event) => setHistoryOpen(event.currentTarget.open)}>
            <summary>Decision history</summary>
            {history.isFetching && <Text size="sm">Loading decision history…</Text>}
            {history.isError && (
              <Alert color="red">
                {history.error.message}
                <Button onClick={() => void history.refetch()}>Retry history</Button>
              </Alert>
            )}
            {(history.data ?? layout.audit.items).map((a) => (
              <Text
                key={a.revision}
                size="sm"
              >
                {a.at} · {a.action} · {a.reason ?? 'No reason supplied'}
              </Text>
            ))}
          </details>
        )}
      </Stack>
    </section>
  );
}
