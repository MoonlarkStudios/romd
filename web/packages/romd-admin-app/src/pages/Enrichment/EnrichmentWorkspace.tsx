import {
  Alert,
  Anchor,
  Badge,
  Button,
  Drawer,
  Group,
  getDefaultZIndex,
  Kbd,
  Stack,
  Tabs,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import type { EnrichmentAssessment, EnrichmentRequirement } from '@romd/admin-api-client';
import {
  IconArrowLeft,
  IconArrowRight,
  IconCheck,
  IconList,
  IconSearch,
  IconSparkles,
} from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { Link, Navigate, useLocation, useParams, useSearchParams } from 'react-router';
import { useDiscardEpoch, useDraftGuard } from '../../components/Administration/DraftGuard';
import { EnrichmentLauncher } from '../../components/Enrichment/EnrichmentLauncher';
import { RunPanel } from '../../components/Enrichment/RunPanel';
import { KeyboardShortcutsHelp } from '../../components/KeyboardShortcutsHelp/KeyboardShortcutsHelp';
import {
  noLongerInViewLabel,
  outstandingCount,
  outstandingRequirements,
  requirementCopy,
  requirementSubjectLabel,
} from '../../components/Title/enrichmentPresentation';
import { TitleWorkbench, type TitleWorkbenchHandle } from '../../components/Title/TitleWorkbench';
import type { WorkbenchView } from '../../components/Title/titleWorkbenchModel';
import {
  normalizeLegacyWorkspaceEditorParams,
  withoutTitleEditorParams,
} from '../../components/Title/useTitleEditorParams';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { invalidateTitleSurfaces } from '../../hooks/api/titleCache';
import {
  useEnrichmentAssessment,
  useEnrichmentWorkspace,
  useEnrichmentWorkspaceCounts,
} from '../../hooks/api/useEnrichmentWorkspace';
import { type KeyboardShortcut, useKeyboardShortcuts } from '../../hooks/useKeyboardShortcuts';
import { usePermissions } from '../../hooks/usePermissions';
import { EnrichmentTitleQueue } from './EnrichmentTitleQueue';
import classes from './EnrichmentWorkspace.module.css';

const views = [
  {
    value: 'NeedsReview',
    label: 'Needs review',
    count: 'needsReview',
    empty: 'Nothing needs your review',
    description: 'Your catalog has no outstanding editorial decisions.',
  },
  {
    value: 'Automatic',
    label: 'Can enrich automatically',
    count: 'automatic',
    empty: 'No automatic work waiting',
    description:
      'Tracked titles have no missing values that can currently be filled automatically.',
  },
  {
    value: 'Processing',
    label: 'Processing',
    count: 'processing',
    empty: 'No enrichment running',
    description: 'Start enrichment whenever you want to check your tracked titles.',
  },
  {
    value: 'Blocked',
    label: 'Blocked',
    count: 'blocked',
    empty: 'No operational blockers',
    description: 'No titles are waiting on a provider or configuration issue.',
  },
  {
    value: 'All',
    label: 'All tracked titles',
    count: 'total',
    empty: 'No tracked titles',
    description: 'Track titles in your catalog to include them in enrichment.',
  },
] as const;
/** The queue's reason line: the first outstanding requirement in the view's state. */
function requirementInView(requirements: EnrichmentRequirement[], view: WorkbenchView) {
  return outstandingRequirements(requirements).find(
    (requirement) => view === 'All' || requirement.state === view,
  );
}
function reasonInView(requirements: EnrichmentRequirement[], view: WorkbenchView) {
  const requirement = requirementInView(requirements, view);
  return requirement && requirementCopy(requirement);
}
/** Where Enter already means something: controls it activates, sliders, and anything inside an open editor. */
const enterOwners =
  'button, a[href], summary, [role="button"], [role="link"], [role="tab"], [role="radio"], [role="checkbox"], [role="switch"], [role="menuitem"], [role="option"], [role="slider"], [role="tabpanel"]';

/** Old task and editor URLs retain title and activity context, but never own queue membership. */
export function EnrichmentWorkspace() {
  const { taskId, titleId } = useParams<{
    taskId: string;
    titleId: string;
  }>();
  const [params] = useSearchParams();
  const legacyEditorParams = normalizeLegacyWorkspaceEditorParams(params);
  if (taskId || titleId || legacyEditorParams) {
    const next = legacyEditorParams ?? new URLSearchParams(params);
    if (taskId) next.set('activity', taskId);
    if (titleId) next.set('title', titleId);
    return (
      <Navigate
        replace
        to={`/enrichment?${next}`}
      />
    );
  }
  return <Workbench />;
}

function Workbench() {
  const [params, setParams] = useSearchParams();
  const view = views.find((item) => item.value === params.get('view')) ?? views[0];
  const search = params.get('q') ?? '';
  const [searchInput, setSearchInput] = useState(search);
  const mobile = useMediaQuery('(max-width: 53.125em)');
  const [queueOpened, setQueueOpened] = useState(false);
  const [launching, setLaunching] = useState(false);
  const [shortcutsOpened, setShortcutsOpened] = useState(false);
  const workbench = useRef<TitleWorkbenchHandle>(null);
  const query = useEnrichmentWorkspace(view.value, search);
  const rows = Array.from(
    new Map(
      (query.data?.pages.flatMap((page) => page.items) ?? []).map((item) => [
        item.titleId,
        item,
      ]),
    ).values(),
  );
  const countsQuery = useEnrichmentWorkspaceCounts(search);
  const counts = countsQuery.data;
  const explicitTitle = params.get('title') ?? undefined;
  const firstRow = rows[0]?.titleId;
  // Without `title`, the first row is chosen once per view and search and stays chosen until the URL records it, so a
  // queue refresh that reorders rows never switches titles, least of all under a draft.
  const listContext = `${view.value}\u0000${search}`;
  const [pinned, setPinned] = useState<{
    context: string;
    titleId: string;
  }>();
  if (explicitTitle && pinned) setPinned(undefined);
  if (!explicitTitle && firstRow && pinned?.context !== listContext)
    setPinned({
      context: listContext,
      titleId: firstRow,
    });
  const selectedId = explicitTitle ?? (pinned?.context === listContext ? pinned.titleId : firstRow);
  const assessment = useEnrichmentAssessment(selectedId);
  const selected = assessment.data;
  const mobileNavigationRef = useRef<HTMLDivElement>(null);
  const titleChooserRef = useRef<HTMLButtonElement>(null);
  const previousMobileTitle = useRef<string | undefined>(undefined);
  useEffect(() => {
    if (!mobile || !selected?.titleId) return;
    if (previousMobileTitle.current && previousMobileTitle.current !== selected.titleId) {
      mobileNavigationRef.current?.scrollIntoView({
        block: 'start',
      });
      titleChooserRef.current?.focus({
        preventScroll: true,
      });
    }
    previousMobileTitle.current = selected.titleId;
  }, [
    mobile,
    selected?.titleId,
  ]);
  const { hasDraft, discardThen } = useDraftGuard();
  const discardEpoch = useDiscardEpoch();
  const location = useLocation();
  // Choosing a title (queue, chooser or Next) focuses its heading; loading the page or changing views does not.
  const [chosenTitle, setChosenTitle] = useState<string>();
  const pendingChoice = useRef<string | undefined>(undefined);
  const committedLocation = useRef(location.key);
  const cache = useQueryClient();
  const previous = useRef<
    | {
        id: string;
        fingerprint: string;
      }
    | undefined
  >(undefined);
  const fingerprint = JSON.stringify(selected);
  const [advancing, setAdvancing] = useState(false);
  const [navigationError, setNavigationError] = useState<string | null>(null);
  const navigationRequest = useRef(0);
  const advancePending = useRef(false);
  // Adding the implicitly selected title to the URL is not a new review context.
  // Only actual navigation should invalidate an in-flight Next request.
  const navigationParams = new URLSearchParams(params);
  if (selectedId) navigationParams.set('title', selectedId);
  const navigationContext = navigationParams.toString();
  const previousNavigationContext = useRef(navigationContext);
  useEffect(() => {
    previousNavigationContext.current = navigationContext;
    advancePending.current = false;
    setAdvancing(false);
    return () => {
      if (previousNavigationContext.current === navigationContext) navigationRequest.current += 1;
    };
  }, [
    navigationContext,
  ]);
  const activity = params.get('activity');
  const choose = (id: string) => {
    const next = withoutTitleEditorParams(params);
    next.set('title', id);
    // The chooser closes first so a discard dialog is never hidden behind it; the rest waits for the navigation.
    setQueueOpened(false);
    pendingChoice.current = id;
    setParams(next);
  };
  // Keep editing cancels the choice without leaving a focus target or a cleared error behind.
  useEffect(() => {
    if (committedLocation.current === location.key) return;
    committedLocation.current = location.key;
    const choice = pendingChoice.current;
    pendingChoice.current = undefined;
    if (!choice || choice !== explicitTitle) return;
    setChosenTitle(choice);
    setNavigationError(null);
  }, [
    location.key,
    explicitTitle,
  ]);
  /** The run panel follows `activity`; choosing a title from it keeps the run in view. */
  const setActivity = (jobId: string | null) => {
    const next = new URLSearchParams(params);
    if (jobId) next.set('activity', jobId);
    else next.delete('activity');
    setParams(next);
  };
  // Recording the implicit choice is not the curator's navigation, so it waits until no draft could block it.
  useEffect(() => {
    if (hasDraft || explicitTitle || !selectedId) return;
    const next = new URLSearchParams(params);
    next.set('title', selectedId);
    setParams(next, {
      replace: true,
    });
  }, [
    params,
    selectedId,
    setParams,
    hasDraft,
    explicitTitle,
  ]);
  useEffect(() => {
    setSearchInput(search);
  }, [
    search,
  ]);
  useEffect(() => {
    if (!selected) return;
    if (previous.current?.id === selected.titleId && previous.current.fingerprint !== fingerprint) {
      void invalidateTitleSurfaces(cache, selected.titleId);
    }
    previous.current = {
      id: selected.titleId,
      fingerprint,
    };
  }, [
    selected,
    fingerprint,
    cache,
  ]);
  const currentIndex = rows.findIndex((row) => row.titleId === selectedId);
  const rememberedOrder = useRef<
    | {
        titleId: string;
        followingIds: string[];
      }
    | undefined
  >(undefined);
  useEffect(() => {
    if (selectedId && currentIndex >= 0)
      rememberedOrder.current = {
        titleId: selectedId,
        followingIds: rows.slice(currentIndex + 1).map((row) => row.titleId),
      };
  }, [
    selectedId,
    currentIndex,
    rows,
  ]);
  const nextLoaded =
    currentIndex >= 0
      ? rows[currentIndex + 1]
      : rememberedOrder.current?.titleId === selectedId
        ? rows.find((row) => rememberedOrder.current?.followingIds.includes(row.titleId))
        : rows[0];
  // Keep editing never starts the page fetch; Discard lets exactly this advance through.
  const advance = () =>
    discardThen(async () => {
      if (nextLoaded) {
        choose(nextLoaded.titleId);
        return;
      }
      if (!query.hasNextPage || query.isFetching || advancePending.current) return;
      advancePending.current = true;
      const request = ++navigationRequest.current;
      setAdvancing(true);
      setNavigationError(null);
      try {
        const known = new Set(rows.map((row) => row.titleId));
        const result = await query.fetchNextPage();
        if (!result || request !== navigationRequest.current) return;
        if (result.isError) throw result.error;
        const next = result.data?.pages
          .flatMap((page) => page.items)
          .find((item) => !known.has(item.titleId));
        if (next) choose(next.titleId);
      } catch {
        if (request === navigationRequest.current)
          setNavigationError('Could not load the next title. Try again.');
      } finally {
        if (request === navigationRequest.current) {
          advancePending.current = false;
          setAdvancing(false);
        }
      }
    });
  const nextDisabled = !nextLoaded && (!query.hasNextPage || query.isFetching);
  const navigation = (
    <Button
      variant="subtle"
      rightSection={<IconArrowRight size={16} />}
      loading={advancing}
      disabled={nextDisabled}
      onClick={() => void advance()}
    >
      Next title
    </Button>
  );
  // A selected title that leaves the view keeps its row, in place, until the curator navigates; Next still follows the server.
  const departed =
    selected && currentIndex < 0 && rememberedOrder.current?.titleId === selected.titleId
      ? selected
      : undefined;
  const followingIndex = departed
    ? rows.findIndex((row) => rememberedOrder.current?.followingIds.includes(row.titleId))
    : -1;
  const queueRows = departed
    ? [
        ...rows.slice(0, followingIndex < 0 ? rows.length : followingIndex),
        departed,
        ...rows.slice(followingIndex < 0 ? rows.length : followingIndex),
      ]
    : rows;
  // Keyboard selection walks the queue as displayed and goes through the same choice, and blocker, as a click.
  const moveSelection = (offset: 1 | -1) => {
    const index = queueRows.findIndex((row) => row.titleId === selectedId);
    const target = queueRows[index < 0 ? 0 : index + offset];
    if (target && target.titleId !== selectedId) choose(target.titleId);
  };
  const shortcuts: KeyboardShortcut[] = [
    {
      key: 'j',
      description: 'Select the next title in the queue',
      action: () => moveSelection(1),
    },
    {
      key: 'k',
      description: 'Select the previous title in the queue',
      action: () => moveSelection(-1),
    },
    {
      key: 'Enter',
      description: 'Open the current requirement',
      preventDefault: false,
      action: () => {
        if (!document.activeElement?.closest(enterOwners)) workbench.current?.reviewRequirement();
      },
    },
    {
      key: 'n',
      description: 'Next title',
      action: () => void advance(),
    },
    {
      key: '?',
      description: 'Show keyboard shortcuts',
      action: () => setShortcutsOpened(true),
    },
  ];
  const shortcutsRoot = useKeyboardShortcuts(shortcuts, {
    scope: 'container',
  });
  const queue = (
    <EnrichmentTitleQueue
      items={queueRows.map((item) => ({
        id: item.titleId,
        name: item.titleName,
        platform: item.platformCompactLabel || item.platformName,
        platformTitle: item.platformName,
        thumbnail: item.thumbnailUrl,
        outstandingCount: outstandingCount(item.requirements),
        reason:
          item === departed ? noLongerInViewLabel : reasonInView(item.requirements, view.value),
        reviewing:
          item !== departed &&
          item.needsReview &&
          (view.value === 'NeedsReview' || view.value === 'All'),
        departed: item === departed,
        flags: item,
      }))}
      selectedId={selectedId}
      pending={query.isPending}
      hasMore={query.hasNextPage}
      loadingMore={query.isFetchingNextPage}
      refreshing={query.isFetching && !query.isFetchingNextPage}
      onSelect={choose}
      onMore={() => void query.fetchNextPage()}
    />
  );
  // The root takes focus from clicks on its background, so shortcuts work anywhere inside it.
  return (
    <Stack
      ref={shortcutsRoot}
      tabIndex={-1}
      className={workspace.page}
      gap="md"
    >
      <Group
        justify="space-between"
        className={workspace.hero}
      >
        <Group gap="md">
          <Button
            component={Link}
            to="/catalog"
            variant="subtle"
            color="gray"
            p={0}
            leftSection={<IconArrowLeft size={15} />}
            w="fit-content"
          >
            Catalog
          </Button>
          <Title
            order={1}
            className={workspace.heading}
          >
            Enrichment
          </Title>
        </Group>
        <Group>
          <Anchor
            component="button"
            type="button"
            size="sm"
            c="dimmed"
            aria-label="Show keyboard shortcuts"
            onClick={() => setShortcutsOpened(true)}
          >
            Keyboard shortcuts <Kbd size="xs">?</Kbd>
          </Anchor>
          <Anchor
            component={Link}
            to="/jobs"
            size="sm"
          >
            Activity history
          </Anchor>
          <Button
            {...workspaceActionProps}
            leftSection={<IconSparkles size={17} />}
            onClick={() => setLaunching(true)}
          >
            Enrich titles
          </Button>
        </Group>
      </Group>
      {activity && (
        <RunPanel
          jobId={activity}
          onDismiss={() => setActivity(null)}
          onOpenTitle={choose}
          onRunChange={setActivity}
        />
      )}
      <Tabs
        value={view.value}
        onChange={(value) => {
          if (!value) return;
          const next = withoutTitleEditorParams(params);
          next.set('view', value);
          next.delete('title');
          setParams(next);
        }}
        classNames={{
          list: workspace.tabsList,
          tab: `${workspace.tab} ${classes.viewTab}`,
          tabSection: classes.viewTabSection,
        }}
      >
        <Tabs.List aria-label="Enrichment views">
          {views.map((item) => (
            <Tabs.Tab
              key={item.value}
              value={item.value}
              rightSection={
                counts ? (
                  <Badge
                    className={classes.count}
                    size="sm"
                    variant="light"
                    color={item.value === 'NeedsReview' ? 'orange' : 'gray'}
                  >
                    {counts[item.count]}
                  </Badge>
                ) : undefined
              }
            >
              {item.label}
            </Tabs.Tab>
          ))}
        </Tabs.List>
      </Tabs>
      <Group
        justify="space-between"
        align="center"
      >
        <form
          onSubmit={(event) => {
            event.preventDefault();
            const next = withoutTitleEditorParams(params);
            if (searchInput.trim()) next.set('q', searchInput.trim());
            else next.delete('q');
            next.delete('title');
            setParams(next);
          }}
        >
          <Group gap="xs">
            <TextInput
              aria-label="Find a title in enrichment"
              placeholder="Find a title…"
              leftSection={<IconSearch size={16} />}
              value={searchInput}
              maxLength={200}
              onChange={(event) => setSearchInput(event.currentTarget.value)}
            />
            <Button
              type="submit"
              variant="default"
            >
              Search
            </Button>
            {search && (
              <Button
                variant="subtle"
                onClick={() => {
                  const next = withoutTitleEditorParams(params);
                  next.delete('q');
                  next.delete('title');
                  setParams(next);
                }}
              >
                Clear
              </Button>
            )}
          </Group>
        </form>
        <Stack
          gap={0}
          align="flex-end"
        >
          <Text
            size="xs"
            c="dimmed"
          >
            {counts
              ? `${counts.total} tracked titles${search ? ' matching this search' : ''}`
              : countsQuery.isError
                ? 'Queue counts unavailable'
                : 'Checking tracked titles…'}{' '}
            · Titles can appear in more than one view
          </Text>
          {countsQuery.isError && (
            <Group gap="xs">
              <Text
                size="xs"
                c="dimmed"
                role="status"
              >
                {counts ? 'Counts may be out of date.' : 'You can keep reviewing titles.'}
              </Text>
              <Button
                size="compact-xs"
                variant="subtle"
                onClick={() => void countsQuery.refetch()}
              >
                Retry counts
              </Button>
            </Group>
          )}
        </Stack>
      </Group>
      {query.isError && (
        <Alert
          color="red"
          title="Could not load enrichment"
        >
          <Text size="sm">{query.error.message}</Text>
          <Button
            variant="subtle"
            onClick={() => void query.refetch()}
          >
            Retry
          </Button>
        </Alert>
      )}
      {view.value === 'Blocked' && rows.length > 0 && <BlockerSummary titles={rows} />}
      {view.value === 'Automatic' && !!counts?.automatic && (
        <Group justify="flex-end">
          <Button
            {...workspaceActionProps}
            leftSection={<IconSparkles size={17} />}
            onClick={() => setLaunching(true)}
          >
            Enrich all {counts.automatic.toLocaleString()}
          </Button>
        </Group>
      )}
      {query.isSuccess && !rows.length && !selectedId && (
        <Stack
          className={classes.empty}
          align="center"
          gap="xs"
        >
          <IconCheck
            size={24}
            aria-hidden="true"
          />
          <Title
            order={2}
            className={workspace.sectionHeading}
          >
            {search ? 'No matching titles in this view' : view.empty}
          </Title>
          <Text
            c="dimmed"
            size="sm"
            ta="center"
          >
            {search ? 'Try another search or switch views.' : view.description}
          </Text>
          {view.value === 'NeedsReview' && !!counts?.automatic && (
            <Button
              {...workspaceActionProps}
              variant="light"
              onClick={() => {
                const next = withoutTitleEditorParams(params);
                next.set('view', 'Automatic');
                next.delete('title');
                setParams(next);
              }}
            >
              See automatic work
            </Button>
          )}
        </Stack>
      )}
      {(rows.length > 0 || selectedId || query.isPending) && (
        <div
          className={classes.layout}
          data-queue={mobile ? 'drawer' : 'rail'}
        >
          {mobile ? (
            <>
              <Group
                ref={mobileNavigationRef}
                className={classes.mobileNavigation}
                justify="space-between"
                wrap="nowrap"
              >
                <Button
                  ref={titleChooserRef}
                  variant="default"
                  leftSection={<IconList size={17} />}
                  aria-label={`Choose title, current title: ${selected?.titleName ?? 'loading'}`}
                  aria-haspopup="dialog"
                  aria-expanded={queueOpened}
                  onClick={() => setQueueOpened(true)}
                  className={classes.titleChooser}
                >
                  <Text
                    truncate
                    inherit
                  >
                    {selected?.titleName ?? 'Choose title'}
                  </Text>
                </Button>
                {navigation}
              </Group>
              <Drawer
                zIndex={getDefaultZIndex('modal') + 1}
                opened={queueOpened}
                onClose={() => setQueueOpened(false)}
                title="Choose a title"
                size="sm"
                returnFocus
                closeButtonProps={{
                  'aria-label': 'Close title chooser',
                }}
              >
                {queue}
              </Drawer>
            </>
          ) : (
            queue
          )}
          <Stack className={classes.review}>
            {navigationError && <Alert color="red">{navigationError}</Alert>}
            {selectedId && (
              <TitleWorkbench
                ref={workbench}
                key={`${view.value}:${selectedId}:${discardEpoch}`}
                titleId={selectedId}
                frame="workspace"
                density="compact"
                view={view.value}
                next={
                  mobile
                    ? undefined
                    : {
                        onClick: () => void advance(),
                        loading: advancing,
                        disabled: nextDisabled,
                      }
                }
                focusHeading={!mobile && chosenTitle === selectedId}
              />
            )}
          </Stack>
        </div>
      )}
      <EnrichmentLauncher
        opened={launching}
        onClose={() => setLaunching(false)}
        initialScope="Automatic"
        filters={
          search
            ? {
                query: search,
                tracked: 'tracked',
              }
            : undefined
        }
        initialMode="Enrich"
      />
      <KeyboardShortcutsHelp
        shortcuts={shortcuts}
        opened={shortcutsOpened}
        onClose={() => setShortcutsOpened(false)}
      />
    </Stack>
  );
}

function BlockerSummary({ titles }: { titles: EnrichmentAssessment[] }) {
  const { canConfigureIntegrations } = usePermissions();
  const groups = new Map<
    string,
    {
      label: string;
      titles: Set<string>;
    }
  >();
  for (const title of titles) {
    for (const row of outstandingRequirements(title.requirements).filter(
      (item) => item.state === 'Blocked',
    )) {
      const key = `${row.kind}:${row.subject}:${row.cause ?? ''}`;
      const group = groups.get(key) ?? {
        label: `${requirementSubjectLabel(row)}: ${requirementCopy(row)}`,
        titles: new Set<string>(),
      };
      group.titles.add(title.titleId);
      groups.set(key, group);
    }
  }
  return (
    <Alert
      color="orange"
      title="Operational blockers in loaded titles"
    >
      <Stack gap="xs">
        {[
          ...groups.entries(),
        ].map(([key, group]) => (
          <Text
            key={key}
            size="sm"
          >
            {group.label} · {group.titles.size} {group.titles.size === 1 ? 'title' : 'titles'}
          </Text>
        ))}
        {canConfigureIntegrations && (
          <Anchor
            component={Link}
            to="/integrations"
            size="sm"
          >
            Review provider settings
          </Anchor>
        )}
      </Stack>
    </Alert>
  );
}
