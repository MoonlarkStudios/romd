import {
  Alert,
  Badge,
  Button,
  Drawer,
  Group,
  Loader,
  Paper,
  Progress,
  Stack,
  Text,
  TextInput,
} from '@mantine/core';
import type {
  Dat,
  DatCatalogSubscriptionDto,
  ManagedSystemDto,
  SystemDatPreviewDto,
  UploadAccepted,
} from '@romd/admin-api-client';
import {
  checkCatalogSubscription,
  importSystemDat,
  previewSystemDat,
} from '@romd/admin-api-client';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router';
import { useDatsByPlatform } from '../../hooks/api/useDats';
import { useJob } from '../../hooks/api/useJobs';
import {
  managedSystemKeys,
  useManagedSystems,
  useSetSystemEnabled,
  useSetupCatalogs,
} from '../../hooks/api/useManagedSystems';
import { formatBytes } from '../../utils/format';
import { catalogProviderName } from './catalogProviderName';
import { DatUploadZone } from './components/DatUploadZone';
import { DatReplacementReviewModal } from './DatReplacementReviewModal';
import { matchesSystem } from './systemSearch';

export function setupFailure(error: unknown): string {
  if (error instanceof Error) return error.message;
  if (error && typeof error === 'object' && 'detail' in error && typeof error.detail === 'string')
    return error.detail;
  return 'This step could not be completed. Your active catalog is unchanged. Try again.';
}
export function AddSourceFlow({
  system,
  startWithUpload = false,
  onClose,
}: {
  system?: ManagedSystemDto;
  startWithUpload?: boolean;
  onClose: () => void;
}) {
  const systems = useManagedSystems();
  const catalogs = useSetupCatalogs();
  const enable = useSetSystemEnabled();
  const client = useQueryClient();
  const navigate = useNavigate();
  const [selected, setSelected] = useState(system);
  const [step, setStep] = useState(system ? 1 : 0);
  const [search, setSearch] = useState('');
  const [file, setFile] = useState<File>();
  const [inspection, setInspection] = useState<SystemDatPreviewDto>();
  const [review, setReview] = useState<DatCatalogSubscriptionDto>();
  const [replacement, setReplacement] = useState<Dat>();
  const [addSeparately, setAddSeparately] = useState(false);
  const [jobId, setJobId] = useState<string>();
  const job = useJob(jobId);
  const dats = useDatsByPlatform(selected?.key);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const available =
    catalogs.data?.catalogs.filter(
      (c) => c.systemId === selected?.key && c.health === 'healthy' && c.documentHash,
    ) ?? [];
  const replacements = dats.data?.filter((d) => inspection?.existingDatIds.includes(d.id)) ?? [];
  const jobSucceeded =
    job.data?.isTerminal && !job.data.hasErrors && job.data.phase === 'Completed';
  const systemState = systems.data?.find((s) => s.key === selected?.key)?.state;
  const successful = jobSucceeded && systemState === 'Ready';
  const failed = job.data?.isTerminal && (!jobSucceeded || systemState === 'NeedsAttention');
  useEffect(() => {
    if (!job.data?.isTerminal) return;
    void client.invalidateQueries({
      queryKey: managedSystemKeys.all,
    });
    void client.invalidateQueries({
      queryKey: managedSystemKeys.catalogs,
    });
    void client.invalidateQueries({
      queryKey: [
        'dats',
      ],
    });
  }, [
    job.data?.isTerminal,
    client,
  ]);
  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError(undefined);
    try {
      await action();
    } catch (e) {
      setError(setupFailure(e));
    } finally {
      setBusy(false);
    }
  }
  const accepted = (result: UploadAccepted) => {
    setReview(undefined);
    setReplacement(undefined);
    setJobId(result.jobId);
    setStep(3);
    void client.invalidateQueries({
      queryKey: managedSystemKeys.all,
    });
  };
  const finish = () => {
    onClose();
    if (selected) navigate(`/systems/${selected.key}`);
  };
  const inspect = (files: File[]) =>
    void run(async () => {
      const next = files[0];
      if (!next) return;
      if (next.size > 32 * 1024 * 1024)
        throw new Error('Catalog review supports DATs up to 32 MiB. Choose a smaller catalog.');
      setInspection(undefined);
      setAddSeparately(false);
      setFile(next);
      const response = await previewSystemDat({
        body: {
          file: next,
        },
      });
      if (!response.data || response.error) throw response.error;
      setInspection(response.data);
      if (!selected) {
        setSelected(systems.data?.find((s) => s.key === response.data.suggestedSystemKey));
        setStep(0);
      } else setStep(2);
    });
  return (
    <Drawer
      opened
      onClose={onClose}
      title={system ? `Add source · ${system.name}` : 'Add source from a DAT'}
      size="min(640px, 100%)"
      position="right"
      closeOnClickOutside={!busy}
    >
      <Stack gap="lg">
        <Text
          size="sm"
          c="dimmed"
        >
          {step === 0
            ? 'Confirm system'
            : step === 1
              ? 'Choose a source'
              : step === 2
                ? 'Review source'
                : 'Processing source'}
        </Text>
        {error && (
          <Alert
            color="red"
            title="Could not finish this step"
          >
            {error}
          </Alert>
        )}
        {step === 0 && (
          <>
            <Text>
              Choose the system this DAT describes. Adding its source also adds the system to Your
              systems.
            </Text>
            {inspection && (
              <Alert title="Confirm the system">
                DAT: {inspection.name}.{' '}
                {inspection.suggestedSystemKey
                  ? 'We found a possible match. Confirm it below before importing.'
                  : 'We could not identify one clear match. Choose the correct system below.'}
              </Alert>
            )}
            {startWithUpload && !inspection && (
              <DatUploadZone
                onDrop={inspect}
                loading={busy}
              />
            )}
            <TextInput
              label="Find a system"
              placeholder="Try PS1, PSX, or PlayStation"
              value={search}
              onChange={(e) => setSearch(e.currentTarget.value)}
            />
            {systems.isLoading ? (
              <Loader />
            ) : systems.isError ? (
              <Alert color="red">
                {systems.error.message}
                <Button onClick={() => void systems.refetch()}>Retry</Button>
              </Alert>
            ) : (
              <Stack
                gap="xs"
                mah={280}
                style={{
                  overflowY: 'auto',
                }}
                role="group"
                aria-label="Supported systems"
              >
                {systems.data
                  ?.filter((s) => matchesSystem(s, search))
                  .map((s) => {
                    const sources =
                      catalogs.data?.catalogs.filter(
                        (c) =>
                          c.systemId === s.key && c.health === 'healthy' && c.documentHash,
                      ) ?? [];
                    return (
                      <Paper
                        key={s.key}
                        withBorder
                        p="sm"
                        style={{
                          borderColor:
                            selected?.key === s.key
                              ? 'var(--mantine-primary-color-filled)'
                              : undefined,
                        }}
                      >
                        <Group
                          justify="space-between"
                          wrap="nowrap"
                        >
                          <Stack gap={2}>
                            <Text fw={600}>{s.name}</Text>
                            <Text
                              size="xs"
                              c="dimmed"
                            >
                              {[
                                s.shortName,
                                ...s.aliases,
                              ].join(' · ')}
                            </Text>
                            <Text size="sm">
                              {sources.length
                                ? sources
                                    .map(
                                      (c) =>
                                        `${catalogProviderName(c.provider)}: complete catalog · ${c.entryCount.toLocaleString()} entries`,
                                    )
                                    .join(' / ')
                                : catalogs.isPending
                                  ? 'Checking catalog availability…'
                                  : 'Upload a DAT or set up later'}
                            </Text>
                          </Stack>
                          <Button
                            variant={selected?.key === s.key ? 'filled' : 'light'}
                            aria-label={`Choose ${s.name}`}
                            onClick={() => setSelected(s)}
                          >
                            {s.enabled ? 'Manage' : 'Choose'}
                          </Button>
                        </Group>
                      </Paper>
                    );
                  })}
                {systems.data && !systems.data.some((s) => matchesSystem(s, search)) && (
                  <Text>No matching systems. Try another name or alias.</Text>
                )}
              </Stack>
            )}
            <Button
              disabled={!selected || busy}
              onClick={() => setStep(inspection ? 2 : 1)}
            >
              Continue with {selected?.name ?? 'selected system'}
            </Button>
            {!inspection && !startWithUpload && (
              <>
                <Text
                  size="sm"
                  c="dimmed"
                >
                  Already have a catalog? Drop a DAT to identify its system and review it first.
                </Text>
                <DatUploadZone
                  onDrop={inspect}
                  loading={busy}
                />
              </>
            )}
          </>
        )}
        {step === 1 && selected && (
          <>
            <Text fw={600}>{selected.name}</Text>
            <Text>Add another catalog source. Your existing sources stay in place.</Text>
            {(catalogs.isError || catalogs.data?.message) && (
              <Alert color="yellow">
                {catalogs.error?.message ?? catalogs.data?.message}
                <Button
                  variant="subtle"
                  onClick={() => void catalogs.refetch()}
                >
                  Retry catalog availability
                </Button>
              </Alert>
            )}
            {catalogs.isPending && <Text c="dimmed">Checking verified catalog availability…</Text>}
            {available.map((c) => {
              const connected = catalogs.data?.subscriptions.some(
                (s) => s.catalogId === c.catalogId,
              );
              const matching = dats.data?.filter((d) => d.name === c.name) ?? [];
              return (
                <Paper
                  key={c.catalogId}
                  withBorder
                  p="md"
                >
                  <Stack gap="xs">
                    <Text fw={600}>{`${catalogProviderName(c.provider)} subscription`}</Text>
                    <Text size="sm">
                      Complete catalog · {c.entryCount.toLocaleString()} entries ·{' '}
                      {c.fileCount.toLocaleString()} files
                    </Text>
                    <Text
                      size="sm"
                      c="dimmed"
                    >
                      Downloads come from the ROMD mirror. You review each update before activation.
                      ROMD checks daily. Every update waits for your review.
                    </Text>
                    <Button
                      disabled={connected || matching.length > 0 || dats.isPending || dats.isError}
                      loading={busy}
                      onClick={() =>
                        void run(async () => {
                          await enable.mutateAsync({
                            systemKey: selected.key,
                            enabled: true,
                          });
                          const response = await checkCatalogSubscription({
                            body: {
                              catalogId: c.catalogId,
                            },
                          });
                          if (!response.data || response.error) throw response.error;
                          void client.invalidateQueries({
                            queryKey: managedSystemKeys.catalogs,
                          });
                          if (
                            [
                              'ReadyToImport',
                              'UpdateAvailable',
                            ].includes(response.data.state)
                          ) {
                            setReview(response.data);
                            setStep(2);
                          } else if (response.data.state === 'UpToDate') finish();
                          else
                            throw new Error(
                              response.data.message ??
                                'Could not prepare a verified catalog. Retry when the publisher is available.',
                            );
                        })
                      }
                    >
                      {connected
                        ? 'Already connected'
                        : matching.length
                          ? 'Manage updates from the existing source'
                          : `Subscribe to ${catalogProviderName(c.provider)}`}
                    </Button>
                  </Stack>
                </Paper>
              );
            })}
            {!catalogs.isPending && !available.length && (
              <Text
                size="sm"
                c="dimmed"
              >
                ROMD recognizes this system, but no verified subscription catalog is available right
                now.
              </Text>
            )}
            <Text fw={600}>Upload a DAT</Text>
            <DatUploadZone
              onDrop={inspect}
              loading={busy}
            />
            <Text
              size="sm"
              c="dimmed"
            >
              Add a separate source you update manually. To update an existing source, choose Upload
              new version on that source.
            </Text>
            <Button
              variant="default"
              onClick={onClose}
            >
              Done
            </Button>
          </>
        )}
        {step === 2 && inspection && selected && !review && !replacement && (
          <>
            <Text fw={600}>Review source for {selected.name}</Text>
            <Text>{inspection.name}</Text>
            <Group>
              <Badge>{inspection.preview.candidateEntries.toLocaleString()} entries</Badge>
              <Badge>
                {(
                  inspection.preview.candidateFiles ?? inspection.preview.filesAdded
                ).toLocaleString()}{' '}
                files
              </Badge>
              <Badge>{formatBytes(inspection.bytes)}</Badge>
            </Group>
            <Text size="sm">
              Version {inspection.preview.candidateVersion ?? 'not specified'} ·{' '}
              {inspection.preview.candidateBiosEntries.toLocaleString()} BIOS entries
            </Text>
            <Alert>
              These counts describe this DAT. A manual upload does not establish that the catalog
              covers every release for the system. ROM files are imported separately.
            </Alert>
            {inspection.suggestedSystemKey !== selected.key && (
              <Alert color="yellow">
                The header does not identify this system unambiguously. You have selected{' '}
                {selected.name}; confirm that it is correct before continuing.
              </Alert>
            )}
            {dats.isPending ? (
              <Loader />
            ) : dats.isError ? (
              <Alert color="red">
                Existing catalogs could not be checked.
                <Button onClick={() => void dats.refetch()}>Retry</Button>
              </Alert>
            ) : replacements.length && !addSeparately ? (
              <>
                <Text>
                  This name matches an existing source. Choose whether to update that source or add
                  a separate source. A name match alone does not decide.
                </Text>
                {replacements
                  .filter(
                    (d) => !catalogs.data?.subscriptions.some((sub) => sub.activeDatId === d.id),
                  )
                  .map((d) => (
                    <Button
                      key={d.id}
                      onClick={() =>
                        void run(async () => {
                          await enable.mutateAsync({
                            systemKey: selected.key,
                            enabled: true,
                          });
                          setReplacement(d);
                        })
                      }
                    >
                      Update this source: {d.name} ({d.version ?? 'no version'})
                    </Button>
                  ))}
                <Button
                  variant="default"
                  onClick={() => setAddSeparately(true)}
                >
                  Add as a separate source
                </Button>
              </>
            ) : (
              <Button
                loading={busy}
                onClick={() =>
                  void run(async () => {
                    if (!file) return;
                    const response = await importSystemDat({
                      path: {
                        systemKey: selected.key,
                      },
                      body: {
                        file,
                      },
                      query: {
                        sha256: inspection.preview.candidateSha256,
                      },
                    });
                    if (!response.data || response.error) throw response.error;
                    accepted(response.data);
                  })
                }
              >
                Add reviewed source
              </Button>
            )}
            <Button
              variant="subtle"
              onClick={() => setStep(0)}
            >
              Change system
            </Button>
          </>
        )}
        {step === 2 && !inspection && !review && (
          <Button
            variant="default"
            onClick={() => setStep(system || selected ? 1 : 0)}
          >
            Back to source options
          </Button>
        )}
        {step === 3 && (
          <>
            <Text fw={600}>
              {successful
                ? 'Your source is ready'
                : failed
                  ? 'Setup needs attention'
                  : 'Processing your source'}
            </Text>
            {!successful && !failed && (
              <>
                <Progress
                  animated
                  value={Number(job.data?.progressPercent ?? 0)}
                />
                <Text size="sm">
                  {job.data?.currentItem ??
                    'ROMD is validating and building the catalog. You can close this window; processing continues.'}
                </Text>
              </>
            )}
            {job.isError && (
              <Alert color="yellow">
                Progress could not be loaded. Processing may still be running.
                <Button onClick={() => void job.refetch()}>Retry status</Button>
              </Alert>
            )}
            {successful ? (
              <>
                <Text>Next, import your ROMs to see which games you own.</Text>
                <Button
                  onClick={() => {
                    onClose();
                    navigate('/import');
                  }}
                >
                  Import your ROMs
                </Button>
                <Button
                  variant="default"
                  onClick={finish}
                >
                  Open system
                </Button>
              </>
            ) : failed ? (
              <>
                <Alert color="red">
                  {jobSucceeded
                    ? `Your document is saved. ${systems.data?.find((s) => s.key === selected?.key)?.message ?? 'The system is not ready yet. Open it for details.'}`
                    : 'The catalog did not finish cleanly. Any previously active catalog is preserved. Review the job, then retry setup.'}
                </Alert>
                <Button
                  component="a"
                  href={`/jobs/${jobId}`}
                >
                  View job
                </Button>
                <Button
                  onClick={() => {
                    if (jobSucceeded) void systems.refetch();
                    else {
                      setJobId(undefined);
                      setStep(1);
                    }
                  }}
                >
                  {jobSucceeded ? 'Refresh status' : 'Retry source setup'}
                </Button>
                {jobSucceeded && (
                  <Button
                    variant="default"
                    onClick={finish}
                  >
                    Open system
                  </Button>
                )}
              </>
            ) : null}
          </>
        )}
        {review && (
          <DatReplacementReviewModal
            subscription={review}
            onClose={() => {
              setReview(undefined);
              setStep(1);
            }}
            onAccepted={accepted}
          />
        )}
        {replacement && file && (
          <DatReplacementReviewModal
            dat={replacement}
            file={file}
            onClose={() => setReplacement(undefined)}
            onAccepted={accepted}
          />
        )}
      </Stack>
    </Drawer>
  );
}
