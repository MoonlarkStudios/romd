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
import { IconSearch, IconSparkles } from '@tabler/icons-react';
import { type KeyboardEvent, useLayoutEffect, useRef, useState } from 'react';
import { Link, useLocation, useSearchParams } from 'react-router';
import { EnrichmentLauncher } from '../../components/Enrichment/EnrichmentLauncher';
import { KeyboardShortcutsHelp } from '../../components/KeyboardShortcutsHelp/KeyboardShortcutsHelp';
import {
  noLongerInViewLabel,
  outstandingLabel,
  outstandingRequirements,
  requirementCopy,
  requirementSubjectLabel,
} from '../../components/Title/enrichmentPresentation';
import { RequirementReview } from '../../components/Title/RequirementReview';
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
import { useEnrichmentWorkspace } from '../../hooks/api/useEnrichmentWorkspace';
import { type KeyboardShortcut, useKeyboardShortcuts } from '../../hooks/useKeyboardShortcuts';
import { usePermissions } from '../../hooks/usePermissions';
import classes from './Catalog.module.css';
import { CatalogInspector, catalogInspectorId } from './CatalogInspector';
import { CatalogLinkedTitle } from './CatalogLinkedTitle';
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
  const search = params.get('q') ?? '';
  const titleId = params.get('title');
  const requirement = params.get('requirement') ?? undefined;
  const returnPath = `${location.pathname}${location.search}`;
  const size = useInspectorSize();
  const query = useEnrichmentWorkspace(state, search);
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
  const [pagingError, setPagingError] = useState(false);
  const paging = useRef(false);
  const queuePosition = useRef({
    key: location.key,
    locked: false,
  });
  const viewContext = `${state}:${search}`;
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
  const linkedRequirement = active && currentRequirement(active.requirements, state, requirement);
  useLayoutEffect(() => {
    queuePosition.current = {
      key: location.key,
      locked: !size.wide || decisionOpen || launching,
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
    wide: size.wide,
    allowUnlisted: true,
    panelId: catalogInspectorId,
    canChange: () => !queuePosition.current.locked,
    clearParams: [
      'requirement',
      'role',
    ],
  });
  const index = visible.findIndex((row) => row.titleId === preview.selectedId);
  const changeFilters = (values: Record<string, string>) => {
    const next = new URLSearchParams(params);
    for (const [name, value] of Object.entries(values)) {
      if (value) next.set(name, value);
      else next.delete(name);
    }
    next.delete('title');
    next.delete('requirement');
    next.delete('role');
    setParams(next, {
      preventScrollReset: true,
    });
  };
  const advance = async () => {
    if (queuePosition.current.locked || paging.current) return;
    if (index < visible.length - 1) {
      preview.move(1);
      focusReviewRow(list.current, visible[index + 1].titleId);
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
      if (next) {
        preview.select(next.titleId);
        requestAnimationFrame(() => focusReviewRow(list.current, next.titleId));
      }
    } catch {
      if (queuePosition.current.key === location.key) setPagingError(true);
    } finally {
      paging.current = false;
    }
  };
  const review = () => {
    if (decisionOpen) return;
    const inspector = document.getElementById(catalogInspectorId);
    (
      inspector?.querySelector<HTMLElement>('[data-review-primary]') ??
      inspector?.querySelector<HTMLElement>('[data-review-link]')
    )?.click();
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
    <div ref={size.ref}>
      <div ref={shortcutRoot}>
        <InspectorLayout
          wide={size.wide}
          animateList={visible.length < 500}
          inspector={
            preview.selectedId && (
              <CatalogInspector
                key={preview.selectedId}
                titleId={preview.selectedId}
                returnPath={returnPath}
                close={preview.close}
                onUnknown={preview.close}
                previous={index > 0 ? () => preview.move(-1) : undefined}
                next={
                  index < visible.length - 1 || query.hasNextPage ? () => void advance() : undefined
                }
                fallbackAssessment={active}
                renderRequirements={(assessment, refresh) => (
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
                    onOpenEditor={(event) => rememberCatalogPosition(returnPath, event)}
                    onDecisionOpen={setDecisionOpen}
                    onDecisionSaved={() =>
                      requestAnimationFrame(() => focusReviewRow(list.current, assessment.titleId))
                    }
                  />
                )}
              />
            )
          }
        >
          <Stack gap="sm">
            {size.measured && !size.wide && titleId && (
              <CatalogLinkedTitle
                titleId={titleId}
                knownName={active?.titleName}
                returnPath={returnPath}
                href={
                  linkedRequirement ? titleRequirementPath(titleId, linkedRequirement) : undefined
                }
              />
            )}
            <div className={classes.toolbar}>
              <NativeSelect
                label="Queue state"
                value={state}
                data={states}
                onChange={(event) =>
                  changeFilters({
                    state: event.currentTarget.value,
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
                  leftSection={<IconSearch size={16} />}
                  placeholder="Search the queue…"
                  maxLength={200}
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
            </div>
            <Group justify="space-between">
              <Text
                c="dimmed"
                size="xs"
                role="status"
              >
                {query.isPending
                  ? 'Loading titles…'
                  : `${rows.length}${query.hasNextPage ? '+' : ''} ${rows.length === 1 && !query.hasNextPage ? 'title' : 'titles'}`}
              </Text>
              <Group gap="xs">
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
                  onClick={() => setLaunching(true)}
                >
                  Enrich titles
                </Button>
              </Group>
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
            {pagingError && (
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
                  aria-label="Titles in the review queue"
                  onKeyDown={onQueueKeyDown}
                >
                  <thead>
                    <tr>
                      <th scope="col">Title</th>
                      <th
                        scope="col"
                        className={classes.reviewStatus}
                      >
                        Status
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {visible.map((item) => {
                      const row = currentRequirement(item.requirements, state);
                      const to =
                        !size.wide && row
                          ? titleRequirementPath(item.titleId, row)
                          : titleSectionPath(item.titleId);
                      return (
                        <tr
                          key={item.titleId}
                          {...preview.rowProps(item.titleId)}
                        >
                          <td>
                            <div className={classes.titleContent}>
                              {item.thumbnailUrl && (
                                <img
                                  loading="lazy"
                                  decoding="async"
                                  className={classes.reviewPoster}
                                  src={item.thumbnailUrl}
                                  alt=""
                                />
                              )}
                              <div>
                                <Link
                                  className={`${classes.titleLink} ${classes.name}`}
                                  data-stretch={!size.wide || undefined}
                                  to={to}
                                  state={{
                                    catalogReturnTo: returnPath,
                                  }}
                                  onClick={(event) => rememberCatalogPosition(returnPath, event)}
                                >
                                  {item.titleName}
                                </Link>
                                <Text
                                  size="xs"
                                  c="dimmed"
                                >
                                  {item.platformName}
                                </Text>
                                <Text
                                  size="xs"
                                  c="dimmed"
                                >
                                  {item === departed?.row
                                    ? noLongerInViewLabel
                                    : row && requirementCopy(row)}
                                </Text>
                              </div>
                            </div>
                            {size.wide && (
                              <CatalogPreviewButton
                                name={item.titleName}
                                active={preview.selectedId === item.titleId}
                                onClick={(event) =>
                                  preview.selectedId === item.titleId
                                    ? preview.close()
                                    : preview.select(item.titleId, event.currentTarget)
                                }
                              />
                            )}
                          </td>
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
          initialScope="Automatic"
          initialMode="Enrich"
          filters={
            search
              ? {
                  query: search,
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
