import {
  Alert,
  Anchor,
  Button,
  Group,
  Kbd,
  NativeSelect,
  Skeleton,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import type { EnrichmentAssessment } from '@romd/admin-api-client';
import { IconArrowLeft, IconArrowRight, IconSearch, IconSparkles } from '@tabler/icons-react';
import { type KeyboardEvent, useLayoutEffect, useRef, useState } from 'react';
import { Link, useLocation, useSearchParams } from 'react-router';
import { EnrichmentLauncher } from '../../components/Enrichment/EnrichmentLauncher';
import { importReceiptReturn } from '../../components/Import/importReceiptLinks';
import { KeyboardShortcutsHelp } from '../../components/KeyboardShortcutsHelp/KeyboardShortcutsHelp';
import {
  noLongerInViewLabel,
  outstandingLabel,
  outstandingRequirements,
  requirementCopy,
  requirementSubjectLabel,
} from '../../components/Title/enrichmentPresentation';
import { RequirementReview } from '../../components/Title/RequirementReview';
import { libraryInspectionPath } from '../../components/Title/reviewHandoff';
import { TitleStatusChips } from '../../components/Title/TitleStatusChips';
import { titleRequirementPath, titleSectionPath } from '../../components/Title/titleSections';
import { currentRequirement, type WorkbenchView } from '../../components/Title/titleWorkbenchModel';
import { InspectorLayout } from '../../components/Workspace/InspectorLayout';
import {
  useInspectorSelection,
  useInspectorSize,
} from '../../components/Workspace/useInspectorSelection';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import {
  useEnrichmentAssessment,
  useEnrichmentWorkspace,
  useEnrichmentWorkspaceCounts,
} from '../../hooks/api/useEnrichmentWorkspace';
import { usePlatforms } from '../../hooks/api/usePlatforms';
import { type KeyboardShortcut, useKeyboardShortcuts } from '../../hooks/useKeyboardShortcuts';
import { usePermissions } from '../../hooks/usePermissions';
import classes from './Catalog.module.css';
import { CatalogInspector, catalogInspectorId } from './CatalogInspector';
import { CatalogLinkedTitle } from './CatalogLinkedTitle';
import { CatalogReviewFocus } from './CatalogReviewFocus';
import { CatalogPreviewButton } from './CatalogRow';
import { rememberCatalogPosition, useRestoreCatalogPosition } from './catalogNavigation';
import { reviewState } from './catalogReviewNavigation';

const states = [
  {
    value: 'NeedsReview',
    label: 'Needs review',
    empty: 'Nothing needs review',
  },
  {
    value: 'Blocked',
    label: 'Blocked',
    empty: 'No operational blockers',
  },
  {
    value: 'Processing',
    label: 'Processing',
    empty: 'No enrichment running',
  },
  {
    value: 'Automatic',
    label: 'Can enrich automatically',
    empty: 'No automatic work waiting',
  },
  {
    value: 'All',
    label: 'All tracked titles',
    empty: 'No tracked titles',
  },
] satisfies {
  value: WorkbenchView;
  label: string;
  empty: string;
}[];

const countForState = {
  NeedsReview: 'needsReview',
  Automatic: 'automatic',
  Processing: 'processing',
  Blocked: 'blocked',
  All: 'total',
} as const;

function focusReviewRow(root: HTMLElement | null, id: string) {
  const row = Array.from(root?.querySelectorAll<HTMLElement>('[data-inspector-row]') ?? []).find(
    (candidate) => candidate.dataset.inspectorRow === id,
  );
  row?.querySelector<HTMLElement>('[data-inspector-trigger]')?.focus();
}

export function CatalogReview() {
  const [params, setParams] = useSearchParams();
  const location = useLocation();
  const state = reviewState(params.get('state'));
  const focusedReview = state === 'NeedsReview';
  const search = params.get('q') ?? '';
  const systemKey = params.get('systemKey') || undefined;
  const titleId = params.get('title');
  const requirement = params.get('requirement') ?? undefined;
  const returnPath = `${location.pathname}${location.search}`;
  const returnToReceipt = importReceiptReturn(params);
  const { canManageUsers } = usePermissions();
  const size = useInspectorSize();
  const query = useEnrichmentWorkspace(state, search, systemKey);
  const platforms = usePlatforms();
  const scoped = !!(systemKey || search);
  const counts = useEnrichmentWorkspaceCounts(search, scoped, systemKey);
  const count = counts.data?.[countForState[state]];
  const rows = [
    ...new Map(
      query.data?.pages
        .flatMap((page) => page.items)
        .map((item) => [
          item.titleId,
          item,
        ]),
    ).values(),
  ];
  const list = useRef<HTMLTableElement>(null);
  const [launching, setLaunching] = useState(false);
  const [shortcutsOpened, setShortcutsOpened] = useState(false);
  const [decisionOpen, setDecisionOpen] = useState(false);
  const [providerBusy, setProviderBusy] = useState(false);
  const [pagingError, setPagingError] = useState(false);
  const paging = useRef(false);
  const focusDecision = useRef(false);
  const lastSelection = useRef<string>(undefined);
  const queuePosition = useRef({
    key: location.key,
    locked: false,
  });
  const viewContext = JSON.stringify([
    state,
    systemKey,
    search,
  ]);
  const remembered = useRef<
    | {
        context: string;
        row: EnrichmentAssessment;
        index: number;
        following: string[];
      }
    | undefined
  >(undefined);
  const liveIndex = rows.findIndex((row) => row.titleId === titleId);
  const active = rows[liveIndex];
  const linked = useEnrichmentAssessment(
    !focusedReview && size.measured && !size.wide && !active ? (titleId ?? undefined) : undefined,
  );
  const linkedAssessment = linked.data ?? active;
  const linkedRequirement =
    linkedAssessment && currentRequirement(linkedAssessment.requirements, state, requirement);
  useLayoutEffect(() => {
    queuePosition.current = {
      key: location.key,
      locked: !size.measured || decisionOpen || providerBusy || launching,
    };
    if (active)
      remembered.current = {
        context: viewContext,
        row: active,
        index: liveIndex,
        following: rows.slice(liveIndex + 1).map((row) => row.titleId),
      };
    else if (!titleId || remembered.current?.context !== viewContext)
      remembered.current = undefined;
  });
  const departed =
    !active &&
    remembered.current?.row.titleId === titleId &&
    remembered.current?.context === viewContext
      ? remembered.current
      : undefined;
  const visible = [
    ...rows,
  ];
  if (departed) {
    const next = rows.findIndex((row) => departed.following.includes(row.titleId));
    visible.splice(next < 0 ? Math.min(departed.index, rows.length) : next, 0, departed.row);
  }
  const preview = useInspectorSelection({
    param: 'title',
    ids: visible.map((item) => item.titleId),
    wide: size.wide || focusedReview,
    allowUnlisted: true,
    panelId: catalogInspectorId,
    canChange: () => !queuePosition.current.locked,
    clearParams: [
      'requirement',
      'role',
    ],
  });
  const index = visible.findIndex((row) => row.titleId === titleId);
  const openReview = (id: string, trigger?: HTMLElement) => {
    if (queuePosition.current.locked) return;
    focusDecision.current = focusedReview;
    preview.select(id, focusedReview ? undefined : trigger);
  };
  const canAdvance = index < visible.length - 1 || query.hasNextPage;
  const loadingNextTitle = index >= visible.length - 1 && query.hasNextPage && query.isFetching;
  const singleDecision = focusedReview && size.measured && !size.wide && !!preview.selectedId;
  useLayoutEffect(() => {
    if (!preview.selectedId && lastSelection.current) {
      focusReviewRow(list.current, lastSelection.current);
    } else if (preview.selectedId && focusDecision.current) {
      const panel = document.getElementById(catalogInspectorId);
      panel?.focus({
        preventScroll: true,
      });
      if (!size.wide)
        window.scrollTo({
          top: 0,
          behavior: 'instant',
        });
    }
    lastSelection.current = preview.selectedId;
    focusDecision.current = false;
  }, [
    preview.selectedId,
    size.wide,
  ]);
  const titleParams = (id: string) => {
    const next = new URLSearchParams(params);
    if (id !== titleId) {
      next.set('title', id);
      next.delete('requirement');
      next.delete('role');
    }
    return next;
  };
  const changeFilters = (values: Record<string, string>, keepTitle = false) => {
    if (providerBusy) return;
    const next = new URLSearchParams(params);
    for (const [name, value] of Object.entries(values)) {
      if (value) next.set(name, value);
      else next.delete(name);
    }
    if (!keepTitle) {
      next.delete('title');
      next.delete('requirement');
      next.delete('role');
    }
    setParams(next, {
      preventScrollReset: true,
    });
  };
  const advance = async (fromDecision = false) => {
    if (queuePosition.current.locked || paging.current) return;
    const select = (id: string) => {
      if (size.wide || focusedReview) {
        focusDecision.current = fromDecision;
        preview.select(id);
        if (!fromDecision) requestAnimationFrame(() => focusReviewRow(list.current, id));
      } else {
        setParams(titleParams(id), {
          replace: true,
          state: location.state,
          preventScrollReset: true,
        });
      }
    };
    if (index < visible.length - 1) {
      select(visible[index + 1].titleId);
      return;
    }
    if (!query.hasNextPage || query.isFetching) return;
    paging.current = true;
    setPagingError(false);
    try {
      const known = new Set(rows.map((row) => row.titleId));
      const updated = await query.fetchNextPage();
      if (queuePosition.current.key !== location.key || queuePosition.current.locked || !updated)
        return;
      if (updated.isError) throw updated.error;
      const next = updated.data?.pages
        .flatMap((page) => page.items)
        .find((row) => !known.has(row.titleId));
      if (next) select(next.titleId);
    } catch {
      if (queuePosition.current.key === location.key) setPagingError(true);
    } finally {
      paging.current = false;
    }
  };
  const review = () => {
    if (queuePosition.current.locked) return;
    const inspector = document.getElementById(catalogInspectorId);
    const action =
      inspector?.querySelector<HTMLElement>('[data-review-primary]') ??
      inspector?.querySelector<HTMLElement>('[data-review-link]');
    if (action) action.click();
    else
      (
        inspector?.querySelector<HTMLElement>('[data-provider-candidate]:not(:disabled)') ??
        inspector
      )?.focus();
  };
  const onQueueKeyDown = (event: KeyboardEvent<HTMLTableElement>) => {
    if (
      !(event.target instanceof HTMLElement) ||
      event.defaultPrevented ||
      !event.target.matches('[data-inspector-trigger]') ||
      event.altKey ||
      event.metaKey ||
      event.ctrlKey
    )
      return;
    const focusedId =
      event.target.closest<HTMLElement>('[data-inspector-row]')?.dataset.inspectorRow;
    if (event.key === 'Enter' && preview.selectedId === focusedId) {
      event.preventDefault();
      review();
    } else if (
      event.key === 'ArrowDown' &&
      focusedId === visible.at(-1)?.titleId &&
      query.hasNextPage
    ) {
      event.preventDefault();
      void advance();
    } else {
      preview.onListKeyDown(event);
    }
  };
  const shortcuts: KeyboardShortcut[] = [
    {
      key: 'j',
      description: 'Preview the next title',
      action: () => void advance(),
    },
    {
      key: 'k',
      description: 'Preview the previous title',
      action: () => {
        preview.move(-1);
        const previous = visible[index - 1];
        if (previous) focusReviewRow(list.current, previous.titleId);
      },
    },
    {
      key: '?',
      description: 'Show keyboard shortcuts',
      action: () => setShortcutsOpened(true),
    },
  ];
  const shortcutRoot = useKeyboardShortcuts(shortcuts, {
    scope: 'container',
    enabled: size.wide,
  });
  useRestoreCatalogPosition(returnPath, !query.isPending && size.measured);

  return (
    <div
      ref={size.ref}
      data-review-single={singleDecision || undefined}
    >
      <div ref={shortcutRoot}>
        {returnToReceipt && (
          <Anchor
            component={Link}
            to={returnToReceipt}
            size="sm"
            display="block"
            w="fit-content"
            mb="xs"
          >
            Back to import results
          </Anchor>
        )}
        {singleDecision ? (
          <Button
            className={classes.reviewBack}
            variant="subtle"
            color="gray"
            leftSection={<IconArrowLeft size={16} />}
            onClick={preview.close}
            disabled={providerBusy}
          >
            Back to review queue
          </Button>
        ) : (
          <div className={classes.reviewToolbar}>
            <NativeSelect
              className={classes.reviewFilter}
              label="Queue state"
              disabled={providerBusy}
              value={state}
              data={states}
              onChange={(event) =>
                changeFilters({
                  state: event.currentTarget.value,
                })
              }
            />
            <NativeSelect
              className={classes.reviewFilter}
              label="System"
              disabled={providerBusy}
              value={systemKey ?? ''}
              data={[
                {
                  value: '',
                  label: 'All systems',
                },
                ...(systemKey && !platforms.data?.some((platform) => platform.key === systemKey)
                  ? [
                      {
                        value: systemKey,
                        label: systemKey,
                      },
                    ]
                  : []),
                ...(platforms.data ?? []).map((platform) => ({
                  value: platform.key,
                  label: platform.name,
                })),
              ]}
              onChange={(event) =>
                changeFilters({
                  systemKey: event.currentTarget.value,
                })
              }
            />
            <form
              className={classes.reviewSearch}
              onSubmit={(event) => {
                event.preventDefault();
                const value = new FormData(event.currentTarget).get('q');
                changeFilters({
                  q: typeof value === 'string' ? value.trim() : '',
                });
              }}
            >
              <TextInput
                name="q"
                key={search}
                defaultValue={search}
                label="Find a title in review"
                disabled={providerBusy}
                leftSection={<IconSearch size={16} />}
                placeholder="Search the queue…"
                maxLength={200}
              />
              <Button
                type="submit"
                variant="default"
                disabled={providerBusy}
              >
                Search
              </Button>
              {search && (
                <Button
                  variant="subtle"
                  disabled={providerBusy}
                  onClick={() =>
                    changeFilters({
                      q: '',
                    })
                  }
                >
                  Clear
                </Button>
              )}
            </form>
            <Group
              gap="xs"
              className={classes.reviewTools}
            >
              {size.wide && (
                <Button
                  variant="subtle"
                  size="compact-sm"
                  onClick={() => setShortcutsOpened(true)}
                >
                  Keyboard shortcuts{' '}
                  <Kbd
                    ml={6}
                    size="xs"
                  >
                    ?
                  </Kbd>
                </Button>
              )}
              <Button
                {...workspaceActionProps}
                variant="light"
                size="compact-sm"
                leftSection={<IconSparkles size={16} />}
                disabled={providerBusy}
                onClick={() => setLaunching(true)}
              >
                Enrich titles
              </Button>
            </Group>
          </div>
        )}
        <InspectorLayout
          wide={size.wide}
          narrow={focusedReview ? 'stack' : 'hide'}
          width={
            focusedReview && size.wide
              ? `${Math.min(Math.round(size.width * 0.56), 736)}px`
              : undefined
          }
          animateList={visible.length < 500}
          inspector={
            preview.selectedId &&
            (focusedReview ? (
              <CatalogReviewFocus
                key={preview.selectedId}
                titleId={preview.selectedId}
                fallback={active ?? departed?.row}
                requirementKey={requirement}
                returnPath={returnPath}
                libraryPath={
                  canManageUsers ? libraryInspectionPath(preview.selectedId, returnPath) : undefined
                }
                onViewBlocked={() =>
                  changeFilters(
                    {
                      state: 'Blocked',
                    },
                    true,
                  )
                }
                close={preview.close}
                previous={index > 0 ? () => preview.move(-1) : undefined}
                next={canAdvance ? () => void advance(true) : undefined}
                skip={canAdvance ? () => void advance(true) : preview.close}
                loadingNextTitle={loadingNextTitle}
                nextTitleError={pagingError}
                selectRequirement={(key) => {
                  const next = new URLSearchParams(params);
                  next.set('requirement', key);
                  next.delete('role');
                  setParams(next, {
                    replace: true,
                    state: location.state,
                    preventScrollReset: true,
                  });
                }}
                onDecisionOpen={setDecisionOpen}
                providerBusy={providerBusy}
                onProviderBusyChange={setProviderBusy}
                onDecisionSaved={() => {
                  const id = preview.selectedId;
                  requestAnimationFrame(() => {
                    if (size.wide && id) focusReviewRow(list.current, id);
                    else document.getElementById(catalogInspectorId)?.focus();
                  });
                }}
              />
            ) : (
              <CatalogInspector
                key={preview.selectedId}
                titleId={preview.selectedId}
                returnPath={returnPath}
                close={preview.close}
                onUnknown={preview.close}
                previous={index > 0 ? () => preview.move(-1) : undefined}
                next={canAdvance ? () => void advance() : undefined}
                fallbackAssessment={active}
                renderRequirements={(assessment, refresh) => (
                  <Stack gap="sm">
                    <RequirementReview
                      assessment={assessment}
                      view={state}
                      currentKey={
                        requirement ?? currentRequirement(assessment.requirements, state)?.key
                      }
                      onRefresh={refresh}
                      navigationState={{
                        catalogReturnTo: returnPath,
                      }}
                      reviewReturnTo={returnPath}
                      onOpenEditor={(event) => rememberCatalogPosition(returnPath, event)}
                      onDecisionOpen={setDecisionOpen}
                      onDecisionSaved={() =>
                        requestAnimationFrame(() =>
                          focusReviewRow(list.current, assessment.titleId),
                        )
                      }
                    />
                    <Group gap="xs">
                      {canManageUsers && (
                        <Button
                          component={Link}
                          to={libraryInspectionPath(assessment.titleId, returnPath)}
                          variant="light"
                          size="compact-sm"
                          style={{
                            alignSelf: 'flex-start',
                          }}
                          onClick={(event) => rememberCatalogPosition(returnPath, event)}
                        >
                          Inspect in a library
                        </Button>
                      )}
                    </Group>
                  </Stack>
                )}
              />
            ))
          }
        >
          <Stack
            gap="sm"
            hidden={singleDecision}
            className={classes.reviewQueue}
          >
            {!focusedReview &&
              size.measured &&
              !size.wide &&
              titleId &&
              (!active && linked.isPending ? (
                <Skeleton
                  h={40}
                  aria-label="Loading linked review"
                />
              ) : (
                <CatalogLinkedTitle
                  titleId={titleId}
                  knownName={linkedAssessment?.titleName}
                  returnPath={returnPath}
                  href={
                    linkedRequirement ? titleRequirementPath(titleId, linkedRequirement) : undefined
                  }
                  actionLabel={linkedRequirement ? 'Open linked review' : undefined}
                />
              ))}
            {!focusedReview && size.measured && !size.wide && titleId && canAdvance && (
              <Button
                variant="subtle"
                size="compact-sm"
                onClick={() => void advance()}
                style={{
                  alignSelf: 'flex-start',
                }}
              >
                Next review
              </Button>
            )}
            <Group
              justify="space-between"
              gap="xs"
              mih={focusedReview ? 32 : undefined}
            >
              <Text
                c="dimmed"
                size="xs"
                role="status"
              >
                {query.isPending
                  ? 'Loading titles…'
                  : count !== undefined
                    ? `${count.toLocaleString()} ${count === 1 ? 'title' : 'titles'} in this view`
                    : `${rows.length}${query.hasNextPage ? '+' : ''} ${rows.length === 1 && !query.hasNextPage ? 'title' : 'titles'}`}
              </Text>
              {focusedReview && !preview.selectedId && rows[0] && (
                <Button
                  {...workspaceActionProps}
                  size="compact-sm"
                  rightSection={<IconArrowRight size={15} />}
                  onClick={() => openReview(rows[0].titleId)}
                >
                  Start review
                </Button>
              )}
            </Group>
            {query.isError && (
              <Alert
                color="red"
                title="Could not load the review queue"
              >
                <Button
                  variant="subtle"
                  onClick={() => void query.refetch()}
                >
                  Retry queue
                </Button>
              </Alert>
            )}
            {pagingError && (!focusedReview || !preview.selectedId) && (
              <Alert color="red">
                Could not load the next title.{' '}
                <Button
                  variant="subtle"
                  onClick={() => void advance()}
                >
                  Retry
                </Button>
              </Alert>
            )}
            {state === 'Blocked' && rows.length > 0 && <BlockerSummary titles={rows} />}
            {query.isPending ? (
              <Skeleton h={180} />
            ) : query.isSuccess && !visible.length ? (
              <div className={classes.reviewEmpty}>
                <Title
                  order={2}
                  className={workspace.sectionHeading}
                >
                  {search
                    ? 'No matching titles'
                    : states.find((item) => item.value === state)?.empty}
                </Title>
                {search && (
                  <Button
                    variant="subtle"
                    onClick={() =>
                      changeFilters({
                        q: '',
                      })
                    }
                  >
                    Clear search
                  </Button>
                )}
              </div>
            ) : (
              visible.length > 0 && (
                <table
                  ref={list}
                  className={classes.table}
                  data-focused-review={focusedReview || undefined}
                  aria-label="Titles in the review queue"
                  onKeyDown={onQueueKeyDown}
                >
                  <thead>
                    <tr>
                      <th scope="col">
                        {focusedReview ? (
                          <div className={classes.reviewColumnLabels}>
                            <span>Title</span>
                            <span className={classes.reviewDecisionLabel}>Decision</span>
                          </div>
                        ) : (
                          'Title'
                        )}
                      </th>
                      {!focusedReview && (
                        <th
                          scope="col"
                          className={classes.reviewStatus}
                        >
                          Status
                        </th>
                      )}
                    </tr>
                  </thead>
                  <tbody>
                    {visible.map((item) => {
                      const row = currentRequirement(item.requirements, state);
                      const issueLabel =
                        row?.kind === 'Identity' && row.cause === 'MatchDecisionRequired'
                          ? `${requirementSubjectLabel(row)} match`
                          : row && requirementCopy(row);
                      const decision = (
                        <Text
                          size="xs"
                          c="dimmed"
                          className={classes.reviewIssue}
                        >
                          {item === departed?.row
                            ? noLongerInViewLabel
                            : focusedReview
                              ? issueLabel
                              : row && requirementCopy(row)}
                        </Text>
                      );
                      const to =
                        !focusedReview && !size.wide && row
                          ? titleRequirementPath(item.titleId, row)
                          : titleSectionPath(item.titleId);
                      const editorReturnPath = size.wide
                        ? returnPath
                        : `${location.pathname}?${titleParams(item.titleId)}`;
                      return (
                        <tr
                          key={item.titleId}
                          {...preview.rowProps(item.titleId)}
                        >
                          <td>
                            <div
                              className={
                                focusedReview ? classes.reviewQueueRow : classes.titleContent
                              }
                            >
                              {item.thumbnailUrl && (
                                <img
                                  loading="lazy"
                                  decoding="async"
                                  className={classes.reviewPoster}
                                  src={item.thumbnailUrl}
                                  alt=""
                                />
                              )}
                              <div className={classes.reviewTitleIdentity}>
                                <Link
                                  className={`${classes.titleLink} ${classes.name}`}
                                  data-stretch={(!focusedReview && !size.wide) || undefined}
                                  to={to}
                                  state={{
                                    catalogReturnTo: editorReturnPath,
                                  }}
                                  onClick={(event) =>
                                    rememberCatalogPosition(editorReturnPath, event)
                                  }
                                >
                                  {item.titleName}
                                </Link>
                                <Text
                                  size="xs"
                                  c="dimmed"
                                >
                                  {item.platformName}
                                </Text>
                                {!focusedReview && decision}
                              </div>
                              {focusedReview && decision}
                            </div>
                            {(size.wide || focusedReview) && (
                              <CatalogPreviewButton
                                name={item.titleName}
                                active={preview.selectedId === item.titleId}
                                review={focusedReview}
                                onClick={(event) => {
                                  if (preview.selectedId === item.titleId) preview.close();
                                  else openReview(item.titleId, event.currentTarget);
                                }}
                              />
                            )}
                          </td>
                          {!focusedReview && (
                            <td className={classes.reviewStatus}>
                              <Stack gap={4}>
                                {item !== departed?.row && (
                                  <TitleStatusChips
                                    flags={item}
                                    label={`${item.titleName} status`}
                                  />
                                )}
                                {item !== departed?.row && (
                                  <Text
                                    size="xs"
                                    c="dimmed"
                                  >
                                    {outstandingLabel(
                                      outstandingRequirements(item.requirements).length,
                                    )}
                                  </Text>
                                )}
                              </Stack>
                            </td>
                          )}
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              )
            )}
            {query.hasNextPage && (
              <Button
                variant="default"
                style={{
                  alignSelf: 'center',
                }}
                loading={query.isFetchingNextPage}
                disabled={query.isFetching && !query.isFetchingNextPage}
                onClick={() => void query.fetchNextPage()}
              >
                Load more titles
              </Button>
            )}
          </Stack>
        </InspectorLayout>
        <KeyboardShortcutsHelp
          shortcuts={[
            {
              key: 'ArrowDown',
              description: 'Preview the next title',
              action: () => {},
            },
            {
              key: 'ArrowUp',
              description: 'Preview the previous title',
              action: () => {},
            },
            {
              key: 'Enter',
              description: 'Review the current requirement',
              action: () => {},
            },
            {
              key: 'Escape',
              description: 'Close the title preview',
              action: () => {},
            },
            ...shortcuts,
          ]}
          opened={shortcutsOpened}
          onClose={() => setShortcutsOpened(false)}
        />
        <EnrichmentLauncher
          opened={launching}
          onClose={() => setLaunching(false)}
          initialScope={scoped ? 'Filtered' : 'Automatic'}
          initialMode="Enrich"
          reviewReturnTo={returnPath}
          scopedBlockedCount={scoped ? counts.data?.blocked : undefined}
          filters={
            scoped
              ? {
                  query: search || undefined,
                  systemKey,
                  tracked: 'tracked',
                }
              : undefined
          }
        />
      </div>
    </div>
  );
}

function BlockerSummary({ titles }: { titles: EnrichmentAssessment[] }) {
  const { canConfigureIntegrations } = usePermissions();
  const blockers = new Map<string, Set<string>>();
  for (const title of titles) {
    for (const item of outstandingRequirements(title.requirements)) {
      if (item.state !== 'Blocked') continue;
      const label = `${requirementSubjectLabel(item)} · ${requirementCopy(item)}`;
      const affected = blockers.get(label) ?? new Set<string>();
      affected.add(title.titleId);
      blockers.set(label, affected);
    }
  }

  return (
    <Alert
      color="orange"
      title="Blockers in loaded titles"
    >
      <Stack gap={4}>
        {Array.from(blockers, ([label, affected]) => (
          <Text
            key={label}
            size="sm"
          >
            {label} · {affected.size} {affected.size === 1 ? 'title' : 'titles'}
          </Text>
        ))}
        {canConfigureIntegrations && (
          <Anchor
            component={Link}
            to="/integrations"
            size="sm"
          >
            Provider settings
          </Anchor>
        )}
      </Stack>
    </Alert>
  );
}
