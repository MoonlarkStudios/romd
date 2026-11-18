import { Alert, Button, Group, Radio, Stack, Text } from '@mantine/core';
import type {
  DatCatalogSubscription,
  ManagedSystem,
  PublishedDatCatalog,
} from '@romd/admin-api-client';
import { useState } from 'react';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import type { useDatsByPlatform } from '../../hooks/api/useDats';
import type { useDatCatalogs, useDatSubscriptions } from '../../hooks/api/useManagedSystems';
import { CatalogRefreshNotice } from './CatalogRefreshNotice';
import { catalogProviderName } from './catalogProviderName';
import { DatUploadZone } from './components/DatUploadZone';
import classes from './SystemSetupFlow.module.css';

export function verifiedOffers(
  directory: ReturnType<typeof useDatCatalogs>,
  systemKey: string,
): PublishedDatCatalog[] {
  if (!directory.isSuccess || !directory.data.enabled || directory.data.message) return [];
  return directory.data.catalogs.filter(
    (catalog) => catalog.systemId === systemKey && catalog.documentHash,
  );
}

export function SystemDatOptions({
  system,
  newSystem,
  pendingSubscription,
  directory,
  subscriptions,
  dats,
  busy,
  onSubscribe,
  onResume,
  onUpload,
  onSkip,
  onClose,
}: {
  system: ManagedSystem;
  newSystem: boolean;
  pendingSubscription?: DatCatalogSubscription;
  directory: ReturnType<typeof useDatCatalogs>;
  subscriptions: ReturnType<typeof useDatSubscriptions>;
  dats: ReturnType<typeof useDatsByPlatform>;
  busy: boolean;
  onSubscribe: (catalog: PublishedDatCatalog) => void;
  onResume: (subscription: DatCatalogSubscription) => void;
  onUpload: (files: File[]) => void;
  onSkip: () => void;
  onClose: () => void;
}) {
  const [chosen, setChosen] = useState<string>();
  const [showUpload, setShowUpload] = useState(false);
  const options = verifiedOffers(directory, system.key);
  const connected = new Set(subscriptions.data?.map((subscription) => subscription.catalogId));
  if (pendingSubscription) connected.add(pendingSubscription.catalogId);
  const installedNames = new Set(dats.data?.map((dat) => dat.name));
  const available = options.filter(
    (catalog) => !connected.has(catalog.catalogId) && !installedNames.has(catalog.name),
  );
  const eligibilityKnown = subscriptions.isSuccess && dats.isSuccess;
  const selectedId =
    chosen ?? (eligibilityKnown && available.length === 1 ? available[0]?.catalogId : undefined);
  const selected = available.find((catalog) => catalog.catalogId === selectedId);
  const uploadExpanded = showUpload || (!directory.isPending && !available.length);
  const resumable = [
    ...(pendingSubscription
      ? [
          pendingSubscription,
        ]
      : []),
    ...(subscriptions.data?.filter(
      (subscription) =>
        subscription.systemKey === system.key &&
        !subscription.activeDatId &&
        subscription.id !== pendingSubscription?.id,
    ) ?? []),
  ];
  const firstDat = system.catalogCount === 0;
  const disabled = directory.isSuccess && !directory.data.enabled;
  const unavailable = directory.isError || (!disabled && !!directory.data?.message);
  const subscriptionChecking = subscriptions.isPending || dats.isPending;
  return (
    <Stack gap="md">
      {directory.isPending && (
        <Text
          size="sm"
          c="dimmed"
          role="status"
        >
          Checking catalog availability…
        </Text>
      )}
      {unavailable && (
        <Alert
          color="yellow"
          title="Catalog availability could not be checked"
        >
          {directory.data?.message ?? 'You can still upload a DAT.'}
          <Button
            variant="subtle"
            onClick={() => void directory.refetch()}
          >
            Retry catalog availability
          </Button>
        </Alert>
      )}
      {disabled && (
        <Text
          size="sm"
          c="dimmed"
        >
          {directory.data?.message ?? 'Subscriptions are disabled on this server.'}
        </Text>
      )}
      {directory.isSuccess &&
        directory.data.enabled &&
        !unavailable &&
        !options.length &&
        !resumable.length && (
          <Text
            size="sm"
            c="dimmed"
          >
            There is no verified subscription available for this system.
          </Text>
        )}
      {(dats.isError || subscriptions.isError) && options.length > 0 && (
        <Alert
          color="yellow"
          title="Existing sources could not be checked"
        >
          <Button
            variant="subtle"
            onClick={() => {
              if (dats.isError) void dats.refetch();
              if (subscriptions.isError) void subscriptions.refetch();
            }}
          >
            Retry source status
          </Button>
        </Alert>
      )}
      {options.some(
        (catalog) => installedNames.has(catalog.name) && !connected.has(catalog.catalogId),
      ) && (
        <Text
          size="sm"
          c="dimmed"
        >
          A matching DAT is installed. Open that source to configure its updates.
        </Text>
      )}
      {options.length > 0 && !available.length && !resumable.length && !firstDat && (
        <Text
          size="sm"
          c="dimmed"
        >
          Subscriptions are managed from their installed sources.
        </Text>
      )}
      {resumable.map((subscription) => (
        <Alert
          key={subscription.id}
          title={subscription.name}
          color={subscription.state === 'CheckFailed' ? 'yellow' : 'teal'}
        >
          <Stack gap="xs">
            {subscription.message && <Text size="sm">{subscription.message}</Text>}
            <Button
              {...workspaceActionProps}
              variant="light"
              disabled={busy}
              onClick={() => onResume(subscription)}
            >
              {subscription.state === 'CheckFailed'
                ? 'Retry check'
                : subscription.state === 'Applying'
                  ? 'View import progress'
                  : subscription.state === 'ReadyToImport'
                    ? 'Review first DAT'
                    : subscription.state === 'UpdateAvailable'
                      ? 'Review update'
                      : 'Check subscription'}
            </Button>
          </Stack>
        </Alert>
      ))}
      {available.length > 0 && (
        <>
          <Radio.Group
            label={
              available.length > 1
                ? 'Choose the format that matches your ROM files'
                : 'Verified subscription'
            }
            value={selected?.catalogId ?? ''}
            onChange={setChosen}
          >
            <Stack
              gap="xs"
              mt="xs"
            >
              {available.map((catalog) => (
                <Radio
                  className={classes.catalogChoice}
                  key={catalog.catalogId}
                  value={catalog.catalogId}
                  disabled={busy || !eligibilityKnown}
                  label={catalog.name}
                  description={`${catalogProviderName(catalog.provider)} · ${catalog.entryCount.toLocaleString()} DAT entries · ${catalog.fileCount.toLocaleString()} expected files`}
                />
              ))}
            </Stack>
          </Radio.Group>
          <Text
            size="sm"
            c="dimmed"
          >
            ROMD checks subscribed sources for new DAT versions. You review each version before
            importing it.
          </Text>
          {selected && <CatalogRefreshNotice catalog={selected} />}
          {subscriptionChecking && (
            <Text
              size="sm"
              c="dimmed"
              role="status"
            >
              Checking existing sources…
            </Text>
          )}
        </>
      )}
      {(available.length > 0 || !uploadExpanded) && (
        <Group gap="xs">
          {available.length > 0 && (
            <Button
              {...workspaceActionProps}
              disabled={!selected || !eligibilityKnown}
              loading={busy}
              onClick={() => selected && onSubscribe(selected)}
            >
              {newSystem ? 'Add system and check' : 'Subscribe and check'}
            </Button>
          )}
          {!uploadExpanded && (
            <Button
              variant="default"
              disabled={busy}
              onClick={() => setShowUpload(true)}
            >
              Upload a DAT
            </Button>
          )}
        </Group>
      )}
      {uploadExpanded && (
        <Stack gap="xs">
          <Text
            size="sm"
            fw={600}
          >
            Upload a DAT
          </Text>
          <DatUploadZone
            onDrop={onUpload}
            loading={busy}
          />
        </Stack>
      )}
      {newSystem ? (
        <Button
          variant="subtle"
          w="fit-content"
          disabled={busy}
          onClick={onSkip}
        >
          Add without a DAT
        </Button>
      ) : (
        <Button
          variant="subtle"
          w="fit-content"
          disabled={busy}
          onClick={onClose}
        >
          Done
        </Button>
      )}
    </Stack>
  );
}
