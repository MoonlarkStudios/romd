import {
  Alert,
  Badge,
  Button,
  Group,
  Loader,
  Modal,
  Progress,
  Skeleton,
  Stack,
  Text,
  Title,
} from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import type {
  Dat,
  DatCatalogSubscription,
  JobReference,
  ManagedSystem,
  ManagedSystemSetupDetails,
  PublishedDatCatalog,
  SystemDatPreview,
} from '@romd/admin-api-client';
import {
  checkDatSubscription,
  createDatSubscription,
  createManagedSystem,
  importSystemDat,
  previewSystemDat,
  updateManagedSystemPresentation,
} from '@romd/admin-api-client';
import { IconDeviceGamepad2, IconStack2 } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { Link, useNavigate } from 'react-router';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { datKeys, useDatsByPlatform } from '../../hooks/api/useDats';
import { useJob } from '../../hooks/api/useJobs';
import {
  managedSystemKeys,
  useDatCatalogs,
  useDatSubscriptions,
  useManagedSystemDetails,
  useManagedSystems,
  useRefreshSystemPresentation,
  useSetSystemEnabled,
  useSystemNameAliasValidation,
} from '../../hooks/api/useManagedSystems';
import { usePlatformAliases } from '../../hooks/api/usePlatforms';
import { formatBytes } from '../../utils/format';
import {
  localSystemKeyPrefix,
  suggestSystemKey,
  validLocalSystemKeySuffix,
} from '../../utils/systemKey';
import { catalogProviderName } from './catalogProviderName';
import { DatUploadZone } from './components/DatUploadZone';
import { DatReplacementReviewModal } from './DatReplacementReviewModal';
import { SystemDatOptions, verifiedOffers } from './SystemDatOptions';
import { SystemLogo, useSystemIcons } from './SystemLogo';
import { SystemPresentationFields, validSystemPresentation } from './SystemPresentationFields';
import classes from './SystemSetupFlow.module.css';
import { SystemSetupPicker } from './SystemSetupPicker';
import {
  presentationValues,
  type SystemPresentation,
  setupAliases,
  systemPresentationPatch,
} from './systemPresentation';

export type OpenSetupSystem = (systemKey: string, tab?: 'sources') => void;
type SetupStep =
  | 'system'
  | 'known-details'
  | 'custom'
  | 'file'
  | 'source'
  | 'manual-review'
  | 'job';
type KnownDraft = SystemPresentation & {
  original: ManagedSystemSetupDetails;
};
type CustomDraft = SystemPresentation & {
  keySuffixOverride?: string;
  registered?: boolean;
};

function setupFailure(error: unknown): string {
  if (error instanceof Error) return error.message;
  if (error && typeof error === 'object' && 'detail' in error && typeof error.detail === 'string')
    return error.detail;
  return 'This step could not be completed. Your active catalog is unchanged. Try again.';
}

function hasRetainedSetup(system: ManagedSystem, subscriptions: DatCatalogSubscription[]) {
  return (
    system.state !== 'NeedsCatalog' ||
    system.catalogCount > 0 ||
    system.ownedTitles > 0 ||
    (system.trackedTitles ?? 0) > 0 ||
    subscriptions.some((subscription) => subscription.systemKey === system.key)
  );
}

export function AddSourceFlow({
  system,
  startWithUpload = false,
  onClose,
  onSystemOpen,
}: {
  system?: ManagedSystem;
  startWithUpload?: boolean;
  onClose: () => void;
  onSystemOpen?: OpenSetupSystem;
}) {
  const addingSystem = !system && !startWithUpload;
  const mobile = useMediaQuery('(max-width: 48em)');
  const [selected, setSelected] = useState(system);
  const [knownDraft, setKnownDraft] = useState<KnownDraft>();
  const [customDraft, setCustomDraft] = useState<CustomDraft>();
  const customKeySuffix =
    customDraft?.keySuffixOverride ??
    suggestSystemKey(customDraft?.name ?? '').slice(localSystemKeyPrefix.length);
  const customKey = `${localSystemKeyPrefix}${customKeySuffix}`;
  const draft = customDraft ?? knownDraft;
  const aliasesToCheck = draft && !customDraft?.registered ? setupAliases(draft.nameAliases) : [];
  const { aliasesValid, ...aliasFeedback } = useSystemNameAliasValidation(aliasesToCheck);
  const canRegisterCustom =
    !!customDraft &&
    validSystemPresentation(customDraft) &&
    validLocalSystemKeySuffix(customKeySuffix) &&
    aliasesValid;
  const unregisteredCustom = !!customDraft && !customDraft.registered;
  const [step, setStep] = useState<SetupStep>(
    system ? 'source' : startWithUpload ? 'file' : 'system',
  );
  const [search, setSearch] = useState('');
  const [file, setFile] = useState<File>();
  const [inspection, setInspection] = useState<SystemDatPreview>();
  const [review, setReview] = useState<DatCatalogSubscription>();
  const [replacement, setReplacement] = useState<Dat>();
  const [pendingSubscription, setPendingSubscription] = useState<DatCatalogSubscription>();
  const [addSeparately, setAddSeparately] = useState(false);
  const [jobId, setJobId] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [enabledKey, setEnabledKey] = useState<string>();
  const [restoreAfterReplacement, setRestoreAfterReplacement] = useState(false);
  const heading = useRef<HTMLHeadingElement>(null);
  const systems = useManagedSystems('', {
    enabled: !!selected || startWithUpload,
  });
  const catalogs = useDatCatalogs();
  const subscriptions = useDatSubscriptions();
  const enable = useSetSystemEnabled();
  const job = useJob(jobId);
  const dats = useDatsByPlatform(unregisteredCustom ? undefined : selected?.key);
  const setupKey = addingSystem && !customDraft ? selected?.key : undefined;
  const presentation = useManagedSystemDetails(setupKey);
  const platformAliases = usePlatformAliases(setupKey);
  const details = presentation.data;
  const canConfirmKnown =
    !setupKey ||
    (!!details && aliasesValid && (!knownDraft || validSystemPresentation(knownDraft)));
  const icons = useSystemIcons();
  const client = useQueryClient();
  const refreshSystemDetails = useRefreshSystemPresentation();
  const navigate = useNavigate();
  const replacements = unregisteredCustom
    ? []
    : (dats.data?.filter((dat) => inspection?.existingDatIds.includes(dat.id)) ?? []);
  const jobSucceeded =
    job.data?.isTerminal && !job.data.hasErrors && job.data.phase === 'Completed';
  const savedSystem = systems.data?.find((item) => item.key === selected?.key);
  const pickerSelection =
    selected ?? systems.data?.find((item) => item.key === inspection?.suggestedSystemKey);
  const enabled =
    !!selected && (selected.enabled || selected.key === enabledKey || savedSystem?.enabled);
  const successful = jobSucceeded && savedSystem?.state === 'Ready';
  const failed = job.data?.isTerminal && (!jobSucceeded || savedSystem?.state === 'NeedsAttention');
  const restoring =
    addingSystem &&
    selected &&
    !customDraft &&
    !enabled &&
    hasRetainedSetup(selected, subscriptions.data ?? []);

  useEffect(() => {
    if (!job.data?.isTerminal) return;
    void client.invalidateQueries({
      queryKey: managedSystemKeys.all,
    });
    void client.invalidateQueries({
      queryKey: managedSystemKeys.subscriptions,
    });
    void client.invalidateQueries({
      queryKey: datKeys.all,
    });
  }, [
    job.data?.isTerminal,
    client,
  ]);

  useEffect(() => {
    if (!review && !replacement && step !== 'system' && step !== 'file') heading.current?.focus();
  }, [
    step,
    review,
    replacement,
  ]);

  async function run(action: () => Promise<void>) {
    if (busy) return;
    setBusy(true);
    setError(undefined);
    try {
      await action();
    } catch (error) {
      setError(setupFailure(error));
    } finally {
      setBusy(false);
    }
  }

  function openSystem(key: string, tab?: 'sources') {
    onClose();
    if (onSystemOpen) onSystemOpen(key, tab);
    else if (key !== system?.key)
      navigate(`/systems/${encodeURIComponent(key)}${tab ? `?tab=${tab}` : ''}`);
  }

  function finish() {
    if (selected) openSystem(selected.key, 'sources');
  }

  async function saveKnownDetails() {
    if (!knownDraft || !selected) return false;
    if (!aliasesValid) throw new Error('Check the name aliases before continuing.');
    const patch = systemPresentationPatch(knownDraft, knownDraft.original);
    if (!Object.keys(patch).length) return false;
    const response = await updateManagedSystemPresentation({
      path: {
        systemKey: selected.key,
      },
      headers: {
        'If-Match': `"${knownDraft.original.system.revision}"`,
      },
      body: patch,
    });
    if (response.response?.status === 412) {
      throw new Error(
        'System details changed while you were editing. Choose the system again to load the latest details.',
      );
    }
    if (!response.data || response.error) throw response.error;
    await refreshSystemDetails(response.data);
    setEnabledKey(selected.key);
    setSelected({
      ...selected,
      name: response.data.name,
    });
    setKnownDraft(undefined);
    return true;
  }

  async function registerCustom() {
    if (!aliasesValid) throw new Error('Check the name aliases before continuing.');
    if (!customDraft || !canRegisterCustom)
      throw new Error('Enter a display name and a valid system key.');
    if (customDraft.registered) return customKey;
    const name = customDraft.name.trim();
    const nameAliases = setupAliases(customDraft.nameAliases);
    const response = await createManagedSystem({
      body: {
        key: customKey,
        name,
        compactLabel: name.slice(0, 40).trimEnd(),
        description: customDraft.description.trim() || null,
        ...(customDraft.category !== null && {
          category: customDraft.category,
        }),
        ...(customDraft.generation !== null && {
          generation: customDraft.generation,
        }),
        ...(customDraft.releaseYear !== null && {
          releaseYear: customDraft.releaseYear,
        }),
        ...(customDraft.manufacturerKeys.length > 0 && {
          manufacturerKeys: customDraft.manufacturerKeys,
        }),
        ...(nameAliases.length > 0 && {
          nameAliases,
        }),
      },
    });
    if (response.response?.status === 409) {
      throw new Error(setupFailure(response.error));
    }
    if (!response.data || response.error) throw response.error;
    await refreshSystemDetails(response.data);
    setCustomDraft({
      ...customDraft,
      keySuffixOverride: response.data.key.slice(localSystemKeyPrefix.length),
      registered: true,
    });
    setEnabledKey(response.data.key);
    return response.data.key;
  }

  async function enableSystem() {
    if (!selected) return;
    if ((await saveKnownDetails()) || enabled) return;
    await enable.mutateAsync({
      systemKey: selected.key,
      enabled: true,
    });
    setEnabledKey(selected.key);
  }

  function trackImport(result: Pick<JobReference, 'jobId'>) {
    setReview(undefined);
    setReplacement(undefined);
    setJobId(result.jobId);
    setStep('job');
    void client.invalidateQueries({
      queryKey: managedSystemKeys.all,
    });
  }

  function handleSubscription(subscription: DatCatalogSubscription) {
    setPendingSubscription(subscription);
    void client.invalidateQueries({
      queryKey: managedSystemKeys.subscriptions,
    });
    if (subscription.state === 'ReadyToImport' || subscription.state === 'UpdateAvailable') {
      setReview(subscription);
    } else if (subscription.state === 'Applying' && subscription.jobId) {
      trackImport({
        jobId: subscription.jobId,
      });
    } else if (subscription.state === 'UpToDate') {
      finish();
    }
  }

  function subscribe(catalog: PublishedDatCatalog) {
    void run(async () => {
      await enableSystem();
      try {
        const response = await createDatSubscription({
          body: {
            catalogId: catalog.catalogId,
          },
        });
        if (!response.data || response.error) throw response.error;
        handleSubscription(response.data);
      } finally {
        // Enrollment can commit even if its response is interrupted. Read local state before offering another action.
        void client.invalidateQueries({
          queryKey: managedSystemKeys.subscriptions,
        });
      }
    });
  }

  function resumeSubscription(subscription: DatCatalogSubscription) {
    if (
      subscription.state === 'ReadyToImport' ||
      subscription.state === 'UpdateAvailable' ||
      (subscription.state === 'Applying' && subscription.jobId)
    ) {
      handleSubscription(subscription);
      return;
    }
    void run(async () => {
      const response = await checkDatSubscription({
        path: {
          subscriptionId: subscription.id,
        },
      });
      if (!response.data || response.error) throw response.error;
      handleSubscription(response.data);
    });
  }

  function chooseSystem(candidate: ManagedSystem) {
    if (addingSystem && candidate.enabled) {
      openSystem(candidate.key, candidate.state === 'NeedsCatalog' ? 'sources' : undefined);
      return;
    }
    setError(undefined);
    setSelected(candidate);
    setKnownDraft(undefined);
    setPendingSubscription(undefined);
    if (addingSystem) setStep('known-details');
  }

  function systemHint(candidate: ManagedSystem) {
    if (!addingSystem) return undefined;
    if (candidate.enabled) return 'In your systems';
    if (hasRetainedSetup(candidate, subscriptions.data ?? [])) return 'Saved setup';
    const offers = verifiedOffers(catalogs, candidate.key);
    if (offers.length === 1) return `${catalogProviderName(offers[0].provider)} available`;
    if (offers.length > 1) return `${offers.length} subscription options`;
    if (catalogs.isSuccess && catalogs.data.enabled && !catalogs.data.message)
      return 'Upload a DAT';
    return undefined;
  }

  function inspect(files: File[]) {
    void run(async () => {
      const next = files[0];
      if (!next) return;
      if (next.size > 32 * 1024 * 1024)
        throw new Error('Catalog review supports DATs up to 32 MiB. Choose a smaller catalog.');
      setInspection(undefined);
      setAddSeparately(false);
      const response = await previewSystemDat({
        body: {
          file: next,
        },
      });
      if (!response.data || response.error) throw response.error;
      setFile(next);
      setInspection(response.data);
      if (step === 'custom' && customDraft) {
        setSelected({
          key: customKey,
          name: customDraft.name.trim(),
          shortName: customKey,
          manufacturer: null,
          enabled: !!customDraft.registered,
          state: 'NeedsCatalog',
          message: null,
          catalogCount: 0,
          ownedTitles: 0,
          knownTitles: 0,
        });
      }
      if (step === 'file') {
        setSelected(undefined);
        setStep('system');
      } else {
        setStep('manual-review');
      }
    });
  }

  function skipDat() {
    void run(async () => {
      await enableSystem();
      finish();
    });
  }

  if (review) {
    return (
      <DatReplacementReviewModal
        subscription={review}
        onClose={() => setReview(undefined)}
        onAccepted={trackImport}
      />
    );
  }
  if (replacement && file) {
    return (
      <DatReplacementReviewModal
        dat={replacement}
        file={file}
        onClose={() => setReplacement(undefined)}
        onAccepted={(result) => {
          trackImport(result);
          if (!enabled) {
            setRestoreAfterReplacement(true);
            void run(async () => {
              await enableSystem();
              setRestoreAfterReplacement(false);
            });
          }
        }}
      />
    );
  }

  function chooseKnownMode() {
    if (knownDraft) void presentation.refetch();
    setCustomDraft(undefined);
    setSelected(undefined);
    setKnownDraft(undefined);
    setPendingSubscription(undefined);
    setInspection(undefined);
    setError(undefined);
    setStep('system');
  }

  function chooseCustomMode() {
    chooseKnownMode();
    setCustomDraft(presentationValues());
    setStep('custom');
  }

  const showSetupMode =
    addingSystem && (step === 'system' || step === 'known-details' || step === 'custom');
  const title = addingSystem
    ? 'Add system'
    : !system
      ? 'Upload a DAT'
      : system.catalogCount > 0
        ? 'Add catalog source'
        : 'Set up catalog';
  const sourceOptions = selected && (
    <SystemDatOptions
      key={selected.key}
      system={selected}
      newSystem={addingSystem && !enabled}
      pendingSubscription={pendingSubscription}
      directory={catalogs}
      subscriptions={subscriptions}
      dats={dats}
      busy={busy}
      canConfirm={canConfirmKnown}
      onSubscribe={subscribe}
      onResume={resumeSubscription}
      onUpload={inspect}
      onSkip={skipDat}
      onClose={enabled && addingSystem ? finish : onClose}
      onBack={
        addingSystem
          ? () => {
              setError(undefined);
              setStep('known-details');
            }
          : undefined
      }
    />
  );
  return (
    <Modal
      opened
      onClose={onClose}
      title={
        addingSystem ? (
          <Group
            gap="sm"
            wrap="nowrap"
          >
            <span className={classes.headingIcon}>
              <IconDeviceGamepad2
                size={24}
                aria-hidden="true"
              />
            </span>
            <span className={workspace.heading}>{title}</span>
          </Group>
        ) : (
          title
        )
      }
      size={addingSystem ? '52rem' : 'lg'}
      classNames={
        addingSystem
          ? {
              content: classes.setupDialog,
              header: classes.setupHeader,
              body: classes.setupBody,
            }
          : undefined
      }
      fullScreen={mobile}
      centered
      closeButtonProps={{
        'aria-label': 'Close',
      }}
      closeOnClickOutside={!busy}
      closeOnEscape={!busy}
      withCloseButton={!busy}
    >
      <Stack gap="md">
        {showSetupMode && (
          <Group
            className={classes.setupMode}
            gap={4}
            aria-label="System type"
            role="group"
          >
            <Button
              variant="subtle"
              leftSection={<IconDeviceGamepad2 size={17} />}
              aria-pressed={step !== 'custom'}
              disabled={busy || !!customDraft?.registered}
              onClick={() => step === 'custom' && chooseKnownMode()}
            >
              Known system
            </Button>
            <Button
              variant="subtle"
              leftSection={<IconStack2 size={17} />}
              aria-pressed={step === 'custom'}
              disabled={busy}
              onClick={() => step !== 'custom' && chooseCustomMode()}
            >
              Custom system
            </Button>
          </Group>
        )}
        {error && (
          <Alert
            color="red"
            title="Could not finish this step"
            role="alert"
          >
            {error}
          </Alert>
        )}
        {step === 'file' && (
          <>
            <Text size="sm">
              Choose a DAT. Review its contents and confirm the matching system before importing.
            </Text>
            <DatUploadZone
              onDrop={inspect}
              loading={busy}
            />
          </>
        )}
        {step === 'system' && (
          <>
            {inspection && (
              <Alert title="Confirm the system">
                DAT: {inspection.name}.{' '}
                {inspection.suggestedSystemKey
                  ? 'We found a possible match. Confirm it below before importing.'
                  : 'We could not identify one clear match. Choose the correct system below.'}
              </Alert>
            )}
            <SystemSetupPicker
              search={search}
              onSearch={setSearch}
              selectedKey={inspection ? (pickerSelection?.key ?? '') : undefined}
              getHint={systemHint}
              onChoose={chooseSystem}
              disabled={busy}
            />
            {addingSystem && (
              <Group
                className={classes.setupFooter}
                gap="xs"
                justify="flex-end"
              >
                <Button
                  variant="default"
                  disabled={busy}
                  onClick={onClose}
                >
                  Cancel
                </Button>
              </Group>
            )}
            {inspection && (
              <Group justify="space-between">
                <Button
                  variant="subtle"
                  disabled={busy}
                  onClick={() => {
                    setSelected(undefined);
                    setInspection(undefined);
                    setError(undefined);
                    setStep('file');
                  }}
                >
                  Choose another file
                </Button>
                <Button
                  {...workspaceActionProps}
                  disabled={!pickerSelection || busy}
                  onClick={() => {
                    setSelected(pickerSelection);
                    setStep('manual-review');
                  }}
                >
                  {pickerSelection ? `Continue with ${pickerSelection.name}` : 'Continue'}
                </Button>
              </Group>
            )}
          </>
        )}
        {step === 'custom' && customDraft && (
          <div className={classes.setupColumns}>
            <Stack
              gap="sm"
              className={classes.setupDetails}
            >
              <Title
                order={2}
                size="h5"
                ref={heading}
                tabIndex={-1}
              >
                System details
              </Title>
              <SystemPresentationFields
                value={customDraft}
                onChange={(value) =>
                  setCustomDraft({
                    ...customDraft,
                    ...value,
                  })
                }
                systemKey={customKey}
                onKeySuffixChange={
                  customDraft.registered
                    ? undefined
                    : (keySuffixOverride) =>
                        setCustomDraft({
                          ...customDraft,
                          keySuffixOverride,
                        })
                }
                {...aliasFeedback}
                hardwareEditable
                disabled={busy || !!customDraft.registered}
              />
            </Stack>
            <Stack
              className={classes.catalogPanel}
              gap="sm"
            >
              <Title
                order={3}
                size="h5"
              >
                Choose the first DAT
              </Title>
              <DatUploadZone
                onDrop={inspect}
                loading={busy}
                disabled={!canRegisterCustom}
                compact
              />
            </Stack>
            <Group
              className={classes.setupFooter}
              gap="xs"
              justify="flex-end"
            >
              <Button
                variant="subtle"
                color="gray"
                disabled={busy}
                onClick={onClose}
              >
                Cancel
              </Button>
              <Button
                variant="default"
                loading={busy}
                disabled={!canRegisterCustom}
                onClick={() => void run(async () => openSystem(await registerCustom(), 'sources'))}
              >
                {customDraft.registered ? 'Open system' : 'Add without a DAT'}
              </Button>
            </Group>
          </div>
        )}
        {step === 'known-details' && selected && (
          <div className={classes.setupStack}>
            <Group
              className={classes.selectedPlatform}
              gap="sm"
              wrap="nowrap"
            >
              <SystemLogo icon={icons.get(selected.key)} />
              <div className={classes.platformLabel}>
                <Title
                  order={2}
                  size="h5"
                  ref={heading}
                  tabIndex={-1}
                >
                  {selected.name}
                </Title>
                {selected.manufacturer && (
                  <Text
                    size="xs"
                    c="dimmed"
                  >
                    {selected.manufacturer}
                  </Text>
                )}
              </div>
              <Button
                className={classes.changeSystem}
                variant="subtle"
                color="gray"
                size="compact-sm"
                aria-label="Choose another system"
                disabled={busy}
                onClick={chooseKnownMode}
              >
                Change
              </Button>
            </Group>
            {details ? (
              <SystemPresentationFields
                value={knownDraft ?? presentationValues(details)}
                hardwareEditable={details.system.ownership === 'Installation'}
                manufacturers={details.system.manufacturers}
                existingAliases={platformAliases.data?.filter((alias) => alias.type === 'name')}
                onChange={(value) =>
                  setKnownDraft({
                    ...value,
                    original: knownDraft?.original ?? details,
                  })
                }
                systemKey={selected.key}
                {...aliasFeedback}
                disabled={busy}
              />
            ) : presentation.isError ? (
              <Alert
                color="yellow"
                title="System details could not be loaded"
              >
                <Button
                  variant="subtle"
                  onClick={() => void presentation.refetch()}
                >
                  Retry details
                </Button>
              </Alert>
            ) : (
              <Skeleton height={110} />
            )}
            {platformAliases.isError && (
              <Button
                size="compact-sm"
                variant="subtle"
                w="fit-content"
                onClick={() => void platformAliases.refetch()}
              >
                Retry existing aliases
              </Button>
            )}
            {restoring && <Text size="sm">Restore this system with its saved catalog setup.</Text>}
            <Group
              className={classes.setupFooter}
              gap="xs"
              justify="flex-end"
            >
              <Button
                variant="subtle"
                color="gray"
                disabled={busy}
                onClick={onClose}
              >
                Cancel
              </Button>
              <Button
                {...workspaceActionProps}
                loading={busy}
                disabled={!canConfirmKnown || (!restoring && subscriptions.isPending)}
                onClick={restoring ? skipDat : () => setStep('source')}
              >
                {restoring ? 'Restore system' : 'Continue to DAT options'}
              </Button>
            </Group>
          </div>
        )}
        {step === 'source' && selected && (
          <div className={classes.setupStack}>
            <Group
              gap="sm"
              wrap="nowrap"
            >
              <SystemLogo icon={icons.get(selected.key)} />
              <div className={classes.platformLabel}>
                <Title
                  order={2}
                  size="h4"
                  ref={heading}
                  tabIndex={-1}
                >
                  {knownDraft?.name.trim() || selected.name}
                </Title>
                {!addingSystem && selected.shortName && (
                  <Text
                    size="xs"
                    c="dimmed"
                  >
                    {selected.shortName}
                  </Text>
                )}
              </div>
            </Group>
            {!addingSystem && (
              <Title
                order={3}
                size="h5"
              >
                {selected.catalogCount === 0 ? 'Choose the first DAT' : 'Add a catalog source'}
              </Title>
            )}
            {sourceOptions}
          </div>
        )}
        {step === 'manual-review' && inspection && selected && (
          <>
            <Title
              order={2}
              size="h4"
              ref={heading}
              tabIndex={-1}
            >
              Review DAT for {knownDraft?.name.trim() || selected.name}
            </Title>
            <Text>{inspection.name}</Text>
            <Group>
              <Badge>{inspection.preview.candidateEntries.toLocaleString()} DAT entries</Badge>
              <Badge>
                {(
                  inspection.preview.candidateFiles ?? inspection.preview.filesAdded
                ).toLocaleString()}{' '}
                expected files
              </Badge>
              <Badge>{formatBytes(inspection.bytes)}</Badge>
            </Group>
            <Text size="sm">
              Version {inspection.preview.candidateVersion ?? 'not specified'} ·{' '}
              {inspection.preview.candidateBiosEntries.toLocaleString()} BIOS entries
            </Text>
            <Alert>
              A DAT may cover only part of the system’s catalog. Import ROM files separately.
            </Alert>
            {inspection.suggestedSystemKey !== selected.key && (
              <Alert color="yellow">
                The header does not identify this system unambiguously. You have selected{' '}
                {selected.name}; confirm that it is correct before continuing.
              </Alert>
            )}
            {dats.isPending && !unregisteredCustom ? (
              <Loader />
            ) : dats.isError ? (
              <Alert color="red">
                Existing catalogs could not be checked.
                <Button onClick={() => void dats.refetch()}>Retry</Button>
              </Alert>
            ) : replacements.length && !addSeparately ? (
              <>
                <Text size="sm">
                  This DAT has the same name as an installed source. Choose whether to update that
                  source or add a separate source.
                </Text>
                {subscriptions.isError && (
                  <Alert color="yellow">
                    Source subscriptions could not be checked.
                    <Button
                      variant="subtle"
                      onClick={() => void subscriptions.refetch()}
                    >
                      Retry subscriptions
                    </Button>
                  </Alert>
                )}
                {subscriptions.isSuccess &&
                  replacements
                    .filter(
                      (dat) =>
                        !subscriptions.data.some(
                          (subscription) => subscription.activeDatId === dat.id,
                        ),
                    )
                    .map((dat) => (
                      <Button
                        key={dat.id}
                        onClick={() => setReplacement(dat)}
                      >
                        Update this source: {dat.name} ({dat.version ?? 'no version'})
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
                {...workspaceActionProps}
                loading={busy}
                disabled={customDraft ? !canRegisterCustom : !canConfirmKnown}
                onClick={() =>
                  void run(async () => {
                    if (!file) return;
                    const key = customDraft ? await registerCustom() : selected.key;
                    if (!customDraft) await saveKnownDetails();
                    const response = await importSystemDat({
                      path: {
                        systemKey: key,
                      },
                      body: {
                        file,
                      },
                      query: {
                        sha256: inspection.preview.candidateSha256,
                      },
                    });
                    if (!response.data || response.error) throw response.error;
                    trackImport(response.data);
                  })
                }
              >
                {unregisteredCustom ? 'Add system and import DAT' : 'Add reviewed source'}
              </Button>
            )}
            <Button
              variant="subtle"
              color="gray"
              w="fit-content"
              disabled={busy}
              onClick={() => {
                setError(undefined);
                setStep(customDraft ? 'custom' : startWithUpload ? 'system' : 'source');
              }}
            >
              {startWithUpload ? 'Change system' : 'Back to DAT options'}
            </Button>
          </>
        )}
        {step === 'job' && (
          <>
            <Title
              order={2}
              size="h4"
              ref={heading}
              tabIndex={-1}
            >
              {successful
                ? 'Your source is ready'
                : failed
                  ? 'Setup needs attention'
                  : 'Processing your source'}
            </Title>
            {!successful && !failed && (
              <>
                <Progress
                  aria-label="DAT import progress"
                  animated={!job.data?.isTerminal}
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
                <Button
                  variant="subtle"
                  onClick={() => void job.refetch()}
                >
                  Retry status
                </Button>
              </Alert>
            )}
            {restoreAfterReplacement && !busy && (
              <Button
                variant="light"
                onClick={() =>
                  void run(async () => {
                    await enableSystem();
                    setRestoreAfterReplacement(false);
                  })
                }
              >
                Restore system
              </Button>
            )}
            {successful && (
              <Group>
                <Button
                  {...workspaceActionProps}
                  onClick={finish}
                >
                  Open catalog sources
                </Button>
                <Button
                  variant="default"
                  onClick={() => {
                    onClose();
                    navigate('/import');
                  }}
                >
                  Import your ROMs
                </Button>
              </Group>
            )}
            {failed && (
              <>
                <Alert color="red">
                  {jobSucceeded
                    ? `Your document is saved. ${savedSystem?.message ?? 'The system is not ready yet. Open it for details.'}`
                    : 'The DAT did not finish processing. Any previously active catalog is preserved.'}
                </Alert>
                <Group>
                  {jobId && (
                    <Button
                      component={Link}
                      to={`/jobs/${jobId}`}
                    >
                      View job
                    </Button>
                  )}
                  <Button
                    variant="default"
                    onClick={() => {
                      if (jobSucceeded) void systems.refetch();
                      else {
                        setJobId(undefined);
                        setPendingSubscription(undefined);
                        setStep(inspection ? 'manual-review' : 'source');
                      }
                    }}
                  >
                    {jobSucceeded ? 'Refresh status' : 'Retry source setup'}
                  </Button>
                  <Button
                    variant="subtle"
                    onClick={finish}
                  >
                    Open catalog sources
                  </Button>
                </Group>
              </>
            )}
          </>
        )}
      </Stack>
    </Modal>
  );
}
