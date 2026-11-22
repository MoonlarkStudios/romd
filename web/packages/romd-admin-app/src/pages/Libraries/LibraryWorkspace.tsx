import {
  Alert,
  Badge,
  Button,
  Group,
  Menu,
  Modal,
  NativeSelect,
  SimpleGrid,
  Skeleton,
  Stack,
  Tabs,
  Text,
  Title,
} from '@mantine/core';
import type { ExportLibraryRequest } from '@romd/admin-api-client';
import {
  IconArrowLeft,
  IconArrowRight,
  IconDots,
  IconDownload,
  IconEye,
  IconRefresh,
  IconSettings,
  IconTrash,
} from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import { useDiscardEpoch } from '../../components/Administration/DraftGuard';
import { UnsavedChanges } from '../../components/Administration/UnsavedChanges';
import { libraryInspectionPath, reviewReturnPath } from '../../components/Title/reviewHandoff';
import { CurationHelp } from '../../components/Workspace/CurationHelp';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useExportLibrary } from '../../hooks/api/useExport';
import { useJob, useJobs } from '../../hooks/api/useJobs';
import { useLibraryAttachments } from '../../hooks/api/useLibraryExperience';
import {
  libraryManagementKeys,
  useDeleteLibrary,
  useLibrary,
} from '../../hooks/api/useLibraryManagement';
import {
  libraryPublicationReviewKey,
  useLibraryPublicationStatus,
} from '../../hooks/api/useLibraryPublication';
import { archiveFormatLabel } from '../../utils/archiveFormats';
import classes from './Libraries.module.css';
import { LibraryCollections } from './LibraryCollections';
import { LibraryDiagnostics } from './LibraryDiagnostics';
import { LibraryGames } from './LibraryGames';
import { LibraryPeople } from './LibraryPeople';
import { LibraryPreview } from './LibraryPreview';
import { LibraryPublicationProgress, LibraryPublishDialog } from './LibraryPublishDialog';
import { LibraryRules } from './LibraryRules';
import { awaitsFirstPublication, needsLibraryRefresh, publicationStatus } from './libraryRefresh';

const tabs = [
  'overview',
  'games',
  'collections',
  'access',
  'preview',
] as const;

export function LibraryWorkspace() {
  const { libraryId = '' } = useParams();
  return (
    <LibraryWorkspaceContent
      key={libraryId}
      libraryId={libraryId}
    />
  );
}

function LibraryWorkspaceContent({ libraryId }: { libraryId: string }) {
  const query = useLibrary(libraryId);
  const attachments = useLibraryAttachments(libraryId);
  const cache = useQueryClient();
  const publication = useLibraryPublicationStatus(libraryId);
  const [acceptedId, setAcceptedId] = useState<string>();
  const exact = useJob(acceptedId).data;
  const latest = publication.data?.latestAttempt;
  const displayedAttempt =
    acceptedId && latest?.jobId !== acceptedId
      ? {
          jobId: acceptedId,
          phase: exact?.phase ?? 'Pending',
          isTerminal: exact?.isTerminal ?? false,
          outcome: exact?.jobType === 'materialization' ? exact.publicationOutcome : null,
        }
      : latest;
  const settledId = displayedAttempt?.isTerminal ? displayedAttempt.jobId : undefined;
  useEffect(() => {
    if (!acceptedId || !latest) return;
    const historyCaughtUp = latest.jobId === acceptedId && latest.isTerminal;
    const newerAttempt =
      exact?.createdAt && Date.parse(latest.createdAt) > Date.parse(exact.createdAt);
    if (historyCaughtUp || newerAttempt) setAcceptedId(undefined);
  }, [
    acceptedId,
    exact?.createdAt,
    latest,
  ]);
  useEffect(() => {
    if (settledId) {
      void cache.invalidateQueries({
        queryKey: libraryManagementKeys.detail(libraryId),
      });
      void cache.invalidateQueries({
        queryKey: libraryManagementKeys.previews(),
      });
      void cache.invalidateQueries({
        queryKey: libraryPublicationReviewKey(libraryId),
      });
    }
  }, [
    cache,
    libraryId,
    settledId,
  ]);
  const jobs = useJobs();
  const exporter = useExportLibrary();
  const [exportFormat, setExportFormat] =
    useState<NonNullable<ExportLibraryRequest['format']>>('original');
  const [queuedExport, setQueuedExport] = useState<{
    jobId: string;
    label: string;
  } | null>(null);
  const exportJob = (jobs.data ?? []).find((job) => job.id === queuedExport?.jobId);
  const refreshing = displayedAttempt?.isTerminal === false;
  const remove = useDeleteLibrary();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const reviewing = params.get('review') === 'publication';
  const setReviewing = (open: boolean) => {
    const next = new URLSearchParams(params);
    if (open) next.set('review', 'publication');
    else next.delete('review');
    setParams(next, {
      replace: true,
    });
  };
  const active = tabs.find((t) => t === params.get('tab')) ?? 'overview';
  const inspect = params.get('inspect');
  const returnToReview = reviewReturnPath(params.get('reviewReturn'));
  const librariesPath =
    inspect && returnToReview ? libraryInspectionPath(inspect, returnToReview) : '/libraries';
  const [dirty, setDirty] = useState(false);
  const [discardSettings, setDiscardSettings] = useState(false);
  const [settings, setSettings] = useState(false);
  const [deleting, setDeleting] = useState(false);
  // The settings modal is not URL-bound, so a discarded Back/Forward within this library must close it and drop its draft.
  const discardEpoch = useDiscardEpoch();
  const [handledDiscard, setHandledDiscard] = useState(discardEpoch);
  if (handledDiscard !== discardEpoch) {
    setHandledDiscard(discardEpoch);
    setSettings(false);
    setDiscardSettings(false);
  }
  const go = (tab: string) => {
    const next = new URLSearchParams(params);
    next.set('tab', tab);
    setParams(next);
  };
  if (query.isPending)
    return (
      <Skeleton
        h={360}
        radius="lg"
      />
    );
  if (query.isError || !query.data)
    return (
      <Alert
        color="red"
        title="Library unavailable"
      >
        <Text>This library could not be loaded.</Text>
        <Button
          component={Link}
          to={librariesPath}
          variant="subtle"
        >
          Back to libraries
        </Button>
      </Alert>
    );
  const library = query.data;
  const status = publicationStatus(library);
  const publishBlockedReason = refreshing
    ? 'A publication is already running.'
    : library.configurationState !== 'Valid'
      ? 'Repair the saved library rules before publishing.'
      : dirty
        ? 'Save your changes before publishing.'
        : undefined;
  const publishLabel = !library.lastMaterializedAt
    ? 'Publish library'
    : needsLibraryRefresh(library)
      ? 'Publish changes'
      : 'Republish library';
  const rows = attachments.data ?? [];
  // The server lists original first. A refreshed library can stop offering the chosen format; the export then
  // falls back to that first choice.
  const exportChoice =
    library.exportFormats.find((choice) => choice.format === exportFormat) ??
    library.exportFormats[0];
  return (
    <div className={classes.page}>
      <UnsavedChanges
        dirty={dirty}
        description="Your edits have not been saved. Stay here to finish, or discard them."
      />
      <Group gap="md">
        <Button
          component={Link}
          to={librariesPath}
          variant="subtle"
          color="gray"
          leftSection={<IconArrowLeft size={15} />}
          px={0}
        >
          {inspect && returnToReview ? 'Choose another library' : 'All libraries'}
        </Button>
        {returnToReview && (
          <Button
            component={Link}
            to={returnToReview}
            variant="subtle"
            size="compact-sm"
          >
            Back to review
          </Button>
        )}
      </Group>
      <div className={classes.hero}>
        <Group
          justify="space-between"
          align="flex-start"
        >
          <Stack gap="sm">
            <Group>
              {library.isDefault && (
                <Badge
                  color="gray"
                  size="sm"
                >
                  Default
                </Badge>
              )}
            </Group>
            <Group gap="xs">
              <Title
                order={1}
                className={classes.heading}
              >
                {library.name}
              </Title>
              <CurationHelp concept="Libraries" />
            </Group>
          </Stack>
          <Group>
            {library.exportFormats.length > 1 && (
              <NativeSelect
                aria-label="Library export format"
                value={exportChoice.format}
                disabled={exporter.isPending}
                onChange={(event) => {
                  const value = event.currentTarget.value;
                  const choice = library.exportFormats.find((offered) => offered.format === value);
                  if (choice) setExportFormat(choice.format);
                }}
                data={library.exportFormats.map((choice) => ({
                  value: choice.format,
                  label: archiveFormatLabel(choice),
                }))}
              />
            )}
            <Button
              {...workspaceActionProps}
              variant="light"
              leftSection={<IconDownload size={17} />}
              loading={exporter.isPending}
              disabled={
                !libraryId ||
                library.configurationState !== 'Valid' ||
                awaitsFirstPublication(library)
              }
              onClick={() =>
                exporter.mutate(
                  {
                    libraryId,
                    format: exportChoice.format,
                  },
                  {
                    onSuccess: (result) =>
                      setQueuedExport({
                        jobId: result.jobId,
                        label: archiveFormatLabel(exportChoice),
                      }),
                  },
                )
              }
            >
              Export library ZIP
            </Button>
            <Button
              {...workspaceActionProps}
              variant={needsLibraryRefresh(library) ? 'filled' : 'light'}
              leftSection={<IconRefresh size={17} />}
              loading={refreshing}
              disabled={!!publishBlockedReason}
              onClick={() => setReviewing(true)}
            >
              {refreshing ? 'Publishing library…' : publishLabel}
            </Button>
            <Button
              variant="light"
              {...workspaceActionProps}
              leftSection={<IconEye size={17} />}
              onClick={() => go('preview')}
            >
              Preview audience
            </Button>
            <Menu position="bottom-end">
              <Menu.Target>
                <Button
                  variant="subtle"
                  color="gray"
                  aria-label="Library actions"
                  px="xs"
                >
                  <IconDots size={20} />
                </Button>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Item
                  disabled={dirty}
                  leftSection={<IconSettings size={16} />}
                  onClick={() => setSettings(true)}
                >
                  Library details
                </Menu.Item>
                <Menu.Divider />
                <Menu.Item
                  color="red"
                  disabled={library.isDefault}
                  leftSection={<IconTrash size={16} />}
                  onClick={() => setDeleting(true)}
                >
                  Delete library
                </Menu.Item>
              </Menu.Dropdown>
            </Menu>
          </Group>
        </Group>
      </div>
      {exporter.isError && (
        <Alert
          color="red"
          mb="lg"
          title="Export unavailable"
        >
          {exporter.error.message}
        </Alert>
      )}
      {queuedExport && (
        <Alert
          mb="lg"
          title={exportJob ? 'Export progress' : 'Export queued'}
        >
          <Stack gap="xs">
            <Text size="sm">{queuedExport.label}</Text>
            {exportJob && (
              <Text size="sm">
                {exportJob.phase} · {exportJob.progressPercent}%
              </Text>
            )}
            <Button
              component={Link}
              to={`/jobs/${queuedExport.jobId}`}
              variant="subtle"
              w="fit-content"
            >
              View export progress
            </Button>
          </Stack>
        </Alert>
      )}
      {library.configurationState !== 'Valid' && (
        <Alert
          color="red"
          title="This audience needs attention"
          mb="lg"
        >
          {library.configurationError ?? 'Review and save the library rules to restore access.'}
        </Alert>
      )}
      {library.configurationState === 'Valid' && !refreshing && status && (
        <Alert
          color="yellow"
          mb="lg"
          title={status}
        >
          {!library.lastMaterializedAt
            ? 'Review the games in this draft, then publish it to make them available to its audience.'
            : 'Your audience keeps the published library until these changes are published.'}
        </Alert>
      )}
      <LibraryPublicationProgress attempt={displayedAttempt} />
      {reviewing && (
        <LibraryPublishDialog
          libraryId={libraryId}
          action={publishLabel}
          blockedReason={publishBlockedReason}
          close={() => setReviewing(false)}
          accepted={(jobId) => {
            setAcceptedId(jobId);
            setReviewing(false);
          }}
        />
      )}
      <Tabs
        value={active}
        onChange={(v) => {
          if (v) go(v);
        }}
        className={classes.tabs}
        classNames={{
          list: classes.tabsList,
          tab: classes.tab,
        }}
      >
        <Tabs.List>
          {tabs.map((tab) => (
            <Tabs.Tab
              key={tab}
              value={tab}
            >
              {tab === 'access' ? 'People' : tab.charAt(0).toUpperCase() + tab.slice(1)}
            </Tabs.Tab>
          ))}
        </Tabs.List>
      </Tabs>
      {active === 'overview' && (
        <Stack gap="xl">
          <SimpleGrid
            cols={{
              base: 1,
              sm: 3,
            }}
            spacing="lg"
          >
            <div className={classes.overviewStat}>
              <Text
                size="sm"
                c="dimmed"
              >
                Published library
              </Text>
              <Text
                className={classes.stat}
                mt="sm"
              >
                {library.publishedGameCount != null
                  ? `${library.publishedGameCount.toLocaleString()} ${library.publishedGameCount === 1 ? 'game' : 'games'}`
                  : 'Not published'}
              </Text>
              <Text
                c="dimmed"
                size="sm"
                mt="sm"
              >
                {library.lastMaterializedAt
                  ? `${library.itemCount.toLocaleString()} ${library.itemCount === 1 ? 'release' : 'releases'}`
                  : 'Publish the draft to see game and release counts.'}
              </Text>
            </div>
            <div className={classes.overviewStat}>
              <Text
                size="sm"
                c="dimmed"
              >
                Attached collections
              </Text>
              <Text
                className={classes.stat}
                mt="sm"
              >
                {attachments.isSuccess ? rows.length : '—'}
              </Text>
              <Text
                c="dimmed"
                size="sm"
                mt="sm"
              >
                {attachments.isError
                  ? 'Could not load collections'
                  : `${rows.filter((r) => r.isFeatured).length} featured in discovery`}
              </Text>
            </div>
            <div className={classes.overviewStat}>
              <Text
                size="sm"
                c="dimmed"
              >
                Content ratings
              </Text>
              <Text
                className={classes.stat}
                mt="sm"
              >
                {!library.configuration
                  ? 'Review rules'
                  : library.configuration.contentRatingPolicy?.maxMinimumAge == null
                    ? 'No ceiling'
                    : `Up to ${library.configuration.contentRatingPolicy.maxMinimumAge}`}
              </Text>
              <Text
                c="dimmed"
                size="sm"
                mt="sm"
              >
                {library.configuration?.contentRatingPolicy?.maxMinimumAge == null
                  ? 'No rating-age ceiling; other rules still apply'
                  : 'Maximum content-rating age'}
              </Text>
              <Button
                variant="subtle"
                {...workspaceActionProps}
                size="compact-sm"
                mt="md"
                onClick={() => go('games')}
              >
                Edit content ratings
              </Button>
            </div>
          </SimpleGrid>
          <div className={classes.overviewSection}>
            <Group
              justify="space-between"
              mb="xl"
            >
              <div>
                <Title
                  order={2}
                  className={classes.sectionHeading}
                >
                  Collections
                </Title>
              </div>
              <Button
                variant="subtle"
                {...workspaceActionProps}
                rightSection={<IconArrowRight size={17} />}
                onClick={() => go('collections')}
              >
                Arrange collections
              </Button>
            </Group>
            {rows.length > 0 ? (
              <Stack>
                {rows.slice(0, 4).map((row) => (
                  <Group
                    key={row.collectionId}
                    justify="space-between"
                  >
                    <Text
                      component={Link}
                      to={`/collections/${row.collectionId}`}
                      fw={550}
                    >
                      {row.name}
                    </Text>
                    <Text
                      size="sm"
                      c="dimmed"
                    >
                      {row.visibleCount} {row.visibleCount === 1 ? 'title' : 'titles'} visible
                      {row.isFeatured ? ' · Featured' : ''}
                    </Text>
                  </Group>
                ))}
              </Stack>
            ) : (
              <Text c="dimmed">No collections attached</Text>
            )}
          </div>
          <Group justify="space-between">
            <Text
              size="sm"
              c="dimmed"
            >
              {library.lastMaterializedAt
                ? `Published ${new Date(library.lastMaterializedAt).toLocaleString()}`
                : 'This draft has never been published.'}
            </Text>
            {(refreshing || status) && (
              <Badge color={library.configurationState !== 'Valid' ? 'red' : 'yellow'}>
                {refreshing ? 'Publishing' : status}
              </Badge>
            )}
          </Group>
        </Stack>
      )}
      {active === 'games' && (
        <LibraryGames
          key={`${libraryId}:${discardEpoch}`}
          library={library}
          onDirtyChange={setDirty}
        />
      )}
      {active === 'collections' && <LibraryCollections libraryId={libraryId} />}
      {active === 'access' && <LibraryPeople library={library} />}
      {active === 'preview' && (
        <Stack gap="lg">
          <LibraryDiagnostics
            libraryId={libraryId}
            published={!!library.lastMaterializedAt}
          />
          {!awaitsFirstPublication(library) && <LibraryPreview libraryId={libraryId} />}
        </Stack>
      )}
      <Modal
        opened={settings}
        onClose={() => {
          if (dirty) setDiscardSettings(true);
          else setSettings(false);
        }}
        title="Library details"
        radius="lg"
        size="lg"
      >
        <LibraryRules
          key={`${libraryId}-${settings}`}
          library={library}
          onDirtyChange={setDirty}
          section="settings"
        />
      </Modal>
      <Modal
        opened={discardSettings}
        onClose={() => setDiscardSettings(false)}
        title="Leave unsaved changes?"
        radius="lg"
        centered
      >
        <Stack>
          <Text>Your edits have not been saved. Stay here to finish, or discard them.</Text>
          <Group justify="flex-end">
            <Button
              variant="default"
              onClick={() => setDiscardSettings(false)}
            >
              Keep editing
            </Button>
            <Button
              color="red"
              onClick={() => {
                setDirty(false);
                setSettings(false);
                setDiscardSettings(false);
              }}
            >
              Discard changes
            </Button>
          </Group>
        </Stack>
      </Modal>
      <Modal
        opened={deleting}
        onClose={() => setDeleting(false)}
        title={`Delete ${library.name}?`}
        radius="lg"
      >
        <Stack>
          <Text>
            People assigned to this library will lose access. Shared collections and stored games
            are retained.
          </Text>
          {remove.isError && <Alert color="red">{remove.error.message}</Alert>}
          <Group justify="flex-end">
            <Button
              variant="default"
              onClick={() => setDeleting(false)}
            >
              Cancel
            </Button>
            <Button
              color="red"
              loading={remove.isPending}
              onClick={async () => {
                try {
                  await remove.mutateAsync(libraryId);
                  navigate('/libraries');
                } catch {
                  /* Visible error. */
                }
              }}
            >
              Delete library
            </Button>
          </Group>
        </Stack>
      </Modal>
    </div>
  );
}
