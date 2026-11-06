import {
  Accordion,
  Alert,
  Badge,
  Button,
  Group,
  Skeleton,
  Stack,
  Tabs,
  Text,
  VisuallyHidden,
} from '@mantine/core';
import type {
  EnrichmentAssessmentDto,
  EnrichmentRequirementDto,
  ProviderGameDto,
} from '@romd/admin-api-client';
import { IconArrowRight } from '@tabler/icons-react';
import { useQuery } from '@tanstack/react-query';
import {
  type ReactNode,
  type Ref,
  type RefObject,
  useCallback,
  useEffect,
  useImperativeHandle,
  useRef,
  useState,
} from 'react';
import { Link, useLocation, useSearchParams } from 'react-router';
import { useEnrichmentAssessment } from '../../hooks/api/useEnrichmentWorkspace';
import { providerMatchesQuery } from '../../hooks/api/useProviderMatches';
import type { TitleDetailView as TitleDetail } from '../../hooks/api/useTitleDetail';
import { useTitleDetail } from '../../hooks/api/useTitleDetail';
import { usePermissions } from '../../hooks/usePermissions';
import { useDiscardEpoch, useDraftGuard } from '../Administration/DraftGuard';
import type { ArtworkRole } from '../artworkPresentation';
import { EnrichmentLauncher } from '../Enrichment/EnrichmentLauncher';
import workspace from '../Workspace/Workspace.module.css';
import { workspaceActionProps } from '../Workspace/workspaceActions';
import { ArtworkPanel } from './ArtworkPanel';
import { DecisionConfirmation } from './DecisionConfirmation';
import {
  isOutstanding,
  outstandingCount,
  requirementCopy,
  requirementProviderId,
  requirementsAddedLabel,
} from './enrichmentPresentation';
import { MetadataPanel } from './MetadataPanel';
import { ProviderMatches } from './ProviderMatches';
import { CatalogPresentationPreview } from './preview/CatalogPresentationPreview';
import { RequirementChecklist } from './RequirementChecklist';
import classes from './TitleWorkbench.module.css';
import { TitleWorkbenchHeader } from './TitleWorkbenchHeader';
import {
  addedRequirementCount,
  artworkRole,
  currentRequirement,
  isInView,
  isReviewAction,
  processingOperation,
  reviewEditor,
  type TitleEditorOutcome,
  type WorkbenchDensity,
  type WorkbenchView,
} from './titleWorkbenchModel';
import { type ConfirmedAction, useRequirementDecisions } from './useRequirementDecisions';
import { type TitleEditor, titleEditors, useTitleEditorParams } from './useTitleEditorParams';

export interface TitleWorkbenchSection {
  value: string;
  label: ReactNode;
  icon?: ReactNode;
  content: ReactNode;
}

export interface TitleWorkbenchNext {
  onClick: () => void;
  loading: boolean;
  disabled: boolean;
}

/** What the workspace's keyboard shortcuts may ask of the workbench. */
export interface TitleWorkbenchHandle {
  /**
   * Performs the review action of the focused checklist row, else of the current requirement, exactly as its button
   * would. Does nothing when that row has no review action or its editor is already open on it; never saves.
   */
  reviewRequirement: () => void;
}

interface EditorRequest {
  editor: TitleEditor;
  id: number;
  /** The navigation that opened the editor; only it may open a modal editor such as the field inspector. */
  locationKey: string;
}

/** Announcements wait a moment so identical text is announced again, and clear once read. */
const ANNOUNCE_DELAY = 100;
const ANNOUNCEMENT_LIFETIME = 5000;
/** How long an editor opened from a row may take to render its first control. */
const AUTOFOCUS_WAIT = 2000;

/**
 * One title's enrichment work in two frames: the workspace (beside the queue) and the title page (under the hero).
 * The checklist renders every current requirement; editors are driven by the `editor` search param.
 */
export function TitleWorkbench({
  titleId,
  frame,
  density,
  view = 'All',
  next,
  extraSections = [],
  focusHeading = false,
  ref,
}: {
  titleId: string;
  frame: 'workspace' | 'page';
  density: WorkbenchDensity;
  /** The workspace view that decides the current requirement and "No longer in this view". */
  view?: WorkbenchView;
  /** Next title in the workspace; it becomes the primary action once nothing is outstanding. */
  next?: TitleWorkbenchNext;
  /** Page sections that share the editor tab list, such as Releases and Ratings. */
  extraSections?: TitleWorkbenchSection[];
  /** Focus the title heading once it renders, after the curator chose this title. */
  focusHeading?: boolean;
  ref?: Ref<TitleWorkbenchHandle>;
}) {
  const { canManageTitles } = usePermissions();
  const title = useTitleDetail(titleId);
  const assessmentQuery = useEnrichmentAssessment(canManageTitles ? titleId : undefined);
  const assessment = assessmentQuery.data;
  const { requirement: linkedKey, editor: linkedEditor, openEditor } = useTitleEditorParams();
  const location = useLocation();
  const { hasDraft } = useDraftGuard();
  const current = assessment && currentRequirement(assessment.requirements, view, linkedKey);
  const rows = useRef(new Map<string, HTMLLIElement>());
  const headingRef = useRef<HTMLHeadingElement>(null);
  const summaryRef = useRef<HTMLParagraphElement>(null);
  const nextRef = useRef<HTMLButtonElement>(null);
  const origin = useRef<string | undefined>(undefined);
  const pendingReview = useRef<
    | {
        key: string;
        editor: TitleEditor;
      }
    | undefined
  >(undefined);
  const committedLocation = useRef(location.key);
  const [editorRequest, setEditorRequest] = useState<EditorRequest>();
  /** What an editor save would resolve, and whether it was outstanding when the curator took it up. */
  const target = useRef<
    | {
        key: string;
        outstanding: boolean;
      }
    | undefined
  >(undefined);
  const resolving = useRef<string | undefined>(undefined);
  const drafting = useRef(hasDraft);
  const [resolution, setResolution] = useState<'idle' | 'refreshed' | 'settled'>('idle');
  const registerRow = useCallback((key: string, element: HTMLLIElement | null) => {
    if (element) rows.current.set(key, element);
    else rows.current.delete(key);
  }, []);
  // A row's review opens its editor, remembers the row and focuses the editor only once that navigation commits;
  // Keep editing leaves nothing behind.
  useEffect(() => {
    if (committedLocation.current === location.key) return;
    committedLocation.current = location.key;
    const intent = pendingReview.current;
    pendingReview.current = undefined;
    if (!intent || intent.key !== linkedKey) return;
    origin.current = intent.key;
    setEditorRequest((request) => ({
      editor: intent.editor,
      id: (request?.id ?? 0) + 1,
      locationKey: location.key,
    }));
  }, [
    location.key,
    linkedKey,
  ]);
  // The linked requirement (a row's review or a deep link) is what a save resolves; without one, a draft the
  // curator starts is taken to resolve the current requirement.
  useEffect(() => {
    if (!assessment || !linkedKey || target.current?.key === linkedKey) return;
    const row = assessment.requirements.find((item) => item.key === linkedKey);
    target.current = {
      key: linkedKey,
      outstanding: !!row && isOutstanding(row),
    };
  }, [
    assessment,
    linkedKey,
  ]);
  useEffect(() => {
    const started = hasDraft && !drafting.current;
    drafting.current = hasDraft;
    if (started && !linkedKey && current)
      target.current = {
        key: current.key,
        outstanding: true,
      };
  }, [
    hasDraft,
    linkedKey,
    current,
  ]);
  // After the refresh, focus follows only a real resolution: the target went from outstanding to satisfied. It moves to
  // the next open row, else an enabled Next title, else the gap summary, unless another draft is open. One settle
  // render lets the resolved editor's own draft unregister first.
  useEffect(() => {
    if (resolution === 'refreshed') {
      setResolution('settled');
      return;
    }
    if (resolution !== 'settled') return;
    setResolution('idle');
    const key = resolving.current;
    resolving.current = undefined;
    const row = assessment?.requirements.find((item) => item.key === key);
    if (!key || (row && isOutstanding(row))) return;
    if (target.current?.key === key)
      target.current = {
        key,
        outstanding: false,
      };
    if (hasDraft) return;
    const nextTitle = nextRef.current?.disabled ? undefined : nextRef.current;
    (
      (current ? rows.current.get(current.key) : undefined) ??
      nextTitle ??
      summaryRef.current
    )?.focus();
  }, [
    resolution,
    current,
    hasDraft,
    assessment,
  ]);
  const resolve = (key: string) => {
    resolving.current = key;
    setResolution('refreshed');
  };
  // A reversal re-renders its row without the button that started it; focus stays on that row, never another one, and
  // only while the curator has not moved on.
  const keepRowFocus = (key: string) => {
    const row = rows.current.get(key);
    const active = document.activeElement;
    if (row && (!active || active === document.body || row.contains(active))) row.focus();
  };
  // The heading appears once both the title and its assessment load, in either order; focus it only once it exists.
  const headingFocused = useRef(false);
  useEffect(() => {
    if (
      !focusHeading ||
      headingFocused.current ||
      !assessment ||
      !title.data ||
      !headingRef.current
    )
      return;
    headingFocused.current = true;
    headingRef.current.focus();
  }, [
    focusHeading,
    assessment,
    title.data,
  ]);
  const outcome: TitleEditorOutcome = {
    density,
    onResolved: canManageTitles
      ? () => {
          origin.current = undefined;
          const key = target.current?.outstanding ? target.current.key : undefined;
          void assessmentQuery.refetch().then(() => {
            if (key) resolve(key);
          });
        }
      : undefined,
    onDismissed: () => {
      const key = origin.current;
      origin.current = undefined;
      if (key) rows.current.get(key)?.focus();
    },
  };
  const review = (row: EnrichmentRequirementDto, action: string) => {
    const editor = reviewEditor(row, action);
    pendingReview.current = {
      key: row.key,
      editor,
    };
    openEditor(editor, {
      requirement: row.key,
      role: artworkRole(row),
    });
  };
  useImperativeHandle(ref, () => ({
    reviewRequirement: () => {
      const focused = [
        ...rows.current,
      ].find(([, element]) => element.contains(document.activeElement))?.[0];
      const row = assessment?.requirements.find((item) => item.key === (focused ?? current?.key));
      const action = row?.actions.find(isReviewAction);
      if (!row || !action || (row.key === linkedKey && reviewEditor(row, action) === linkedEditor))
        return;
      // Start from the row, as a click on its button would, so closing the editor returns focus there.
      if (!focused) rows.current.get(row.key)?.focus();
      review(row, action);
    },
  }));

  if (title.isPending) return <Skeleton h={400} />;
  if (!title.data)
    return (
      <Alert
        color="red"
        title="Could not load this title"
      >
        <Button
          variant="subtle"
          onClick={() => void title.refetch()}
        >
          Retry
        </Button>
      </Alert>
    );
  const enrichment = (
    <>
      {canManageTitles && assessmentQuery.isPending && (
        <Skeleton h={frame === 'workspace' ? 400 : 120} />
      )}
      {assessmentQuery.isError && !assessment && (
        <Alert
          color="red"
          title="Could not load enrichment"
        >
          <Text size="sm">{assessmentQuery.error.message}</Text>
          <Button
            variant="subtle"
            onClick={() => void assessmentQuery.refetch()}
          >
            Retry
          </Button>
        </Alert>
      )}
      {assessment && (
        <WorkbenchDecisions
          assessment={assessment}
          frame={frame}
          view={view}
          title={title.data}
          current={current}
          next={next}
          registerRow={registerRow}
          headingRef={headingRef}
          summaryRef={summaryRef}
          nextRef={nextRef}
          onReview={review}
          onRefresh={() => void assessmentQuery.refetch()}
          onResolved={resolve}
          onReversed={keepRowFocus}
        />
      )}
    </>
  );
  const editors = (initial: Partial<EditorDefaults>) => (
    <WorkbenchEditors
      title={title.data}
      titleId={titleId}
      frame={frame}
      outcome={outcome}
      initialEditor={initial.editor ?? 'metadata'}
      initialRole={initial.role}
      initialProviderId={initial.providerId}
      extraSections={extraSections}
      request={editorRequest}
      enrichment={enrichment}
      outstanding={assessment ? outstandingCount(assessment.requirements) : 0}
    />
  );
  return (
    <div
      className={classes.workbench}
      data-density={density}
    >
      {title.isError && (
        <Alert
          color="red"
          title="Could not refresh this title"
        >
          <Text size="sm">
            Your draft is still here. Retry to check for the latest saved values.
          </Text>
          <Button
            variant="subtle"
            onClick={() => void title.refetch()}
          >
            Retry title refresh
          </Button>
        </Alert>
      )}
      {frame === 'workspace' && enrichment}
      {frame === 'page' ? editors({}) : assessment && editors(editorDefaults(current))}
    </div>
  );
}

interface EditorDefaults {
  editor: TitleEditor;
  role?: ArtworkRole;
  providerId?: string;
}

/**
 * The workspace selects the current requirement's editor; the page always starts on Metadata. Neither opens a modal
 * editor such as the field inspector for the current requirement alone.
 */
function editorDefaults(current: EnrichmentRequirementDto | undefined): EditorDefaults {
  return {
    editor: reviewEditor(current),
    role: artworkRole(current),
    providerId: current?.actions.includes('ReviewProvider')
      ? requirementProviderId(current)
      : undefined,
  };
}

function WorkbenchDecisions({
  assessment,
  frame,
  view,
  title,
  current,
  next,
  registerRow,
  headingRef,
  summaryRef,
  nextRef,
  onReview,
  onRefresh,
  onResolved,
  onReversed,
}: {
  assessment: EnrichmentAssessmentDto;
  frame: 'workspace' | 'page';
  view: WorkbenchView;
  title: TitleDetail;
  current?: EnrichmentRequirementDto;
  next?: TitleWorkbenchNext;
  registerRow: (key: string, element: HTMLLIElement | null) => void;
  headingRef: RefObject<HTMLHeadingElement | null>;
  summaryRef: RefObject<HTMLParagraphElement | null>;
  nextRef: RefObject<HTMLButtonElement | null>;
  onReview: (row: EnrichmentRequirementDto, action: string) => void;
  onRefresh: () => void;
  /** A confirmed decision on this open row succeeded; its mutation has already refreshed the assessment. */
  onResolved: (key: string) => void;
  /** A reversal on this satisfied row succeeded. */
  onReversed: (key: string) => void;
}) {
  const decisions = useRequirementDecisions(assessment);
  const [confirming, setConfirming] = useState<{
    action: ConfirmedAction;
    row: EnrichmentRequirementDto;
  } | null>(null);
  const [launching, setLaunching] = useState(false);
  const matches = useQuery({
    ...providerMatchesQuery(assessment.titleId),
    enabled: false,
  });
  const matchedGames = new Map(
    (matches.data ?? []).flatMap((match) =>
      match.game
        ? [
            [
              match.providerId,
              match.game,
            ] as [
              string,
              ProviderGameDto,
            ],
          ]
        : [],
    ),
  );
  const announcement = useAddedRequirementsAnnouncement(assessment);
  const inView = isInView(assessment, view);
  const seenInView = useRef(false);
  useEffect(() => {
    if (inView) seenInView.current = true;
  }, [
    inView,
  ]);
  const operation = processingOperation(assessment.requirements);
  const nothingOutstanding = outstandingCount(assessment.requirements) === 0;
  return (
    <>
      <TitleWorkbenchHeader
        frame={frame}
        assessment={assessment}
        titleName={title.name}
        isTracked={title.isTracked}
        noLongerInView={seenInView.current && !inView}
        headingRef={headingRef}
        summaryRef={summaryRef}
        navigation={
          next && (
            <Button
              ref={nextRef}
              {...workspaceActionProps}
              variant={nothingOutstanding ? 'filled' : 'subtle'}
              rightSection={<IconArrowRight size={16} />}
              loading={next.loading}
              disabled={next.disabled}
              onClick={next.onClick}
            >
              Next title
            </Button>
          )
        }
      />
      {operation && (
        <Alert
          color="blue"
          variant="light"
          role="status"
        >
          {requirementCopy(operation)}
        </Alert>
      )}
      {decisions.error && (
        <Alert color="red">
          <Text size="sm">{decisions.error.message}</Text>
          <Button
            variant="subtle"
            onClick={() => {
              decisions.reset();
              onRefresh();
            }}
          >
            Refresh decision
          </Button>
        </Alert>
      )}
      <RequirementChecklist
        assessment={assessment}
        collapseCompleted={frame === 'page'}
        currentKey={current?.key}
        headingOrder={frame === 'workspace' ? 3 : 2}
        matchedGames={matchedGames}
        pendingSubject={decisions.pendingSubject}
        registerRow={registerRow}
        actions={{
          review: onReview,
          enrich: () => setLaunching(true),
          confirm: (action, row) => {
            decisions.confirmations[action].prepare?.();
            setConfirming({
              action,
              row,
            });
          },
          confirmLabel: (action, row) => decisions.confirmations[action].copy(row).confirmLabel,
          revisit: (row) => {
            void decisions.revisit(row).then(
              () => onReversed(row.key),
              () => undefined,
            );
          },
          returnToAutomatic: (row) => {
            void decisions.returnToAutomatic(row).then(
              () => onReversed(row.key),
              () => undefined,
            );
          },
        }}
      />
      <VisuallyHidden
        role="status"
        aria-live="polite"
      >
        {announcement}
      </VisuallyHidden>
      <DecisionConfirmation
        copy={
          confirming ? decisions.confirmations[confirming.action].copy(confirming.row) : undefined
        }
        onCancel={() => setConfirming(null)}
        onConfirm={() =>
          confirming
            ? decisions.confirmations[confirming.action].apply(confirming.row)
            : Promise.resolve()
        }
        onConfirmed={() => {
          if (confirming) onResolved(confirming.row.key);
          setConfirming(null);
        }}
        returnFocusAfterConfirm={false}
      />
      <EnrichmentLauncher
        opened={launching}
        onClose={() => setLaunching(false)}
        titleId={assessment.titleId}
        titleName={title.name}
        initialMode="Enrich"
      />
    </>
  );
}

/** Announces rows that appear for the same title after a refresh; focus never moves for them. */
function useAddedRequirementsAnnouncement(assessment: EnrichmentAssessmentDto): string {
  const previous = useRef<
    | {
        titleId: string;
        keys: string[];
      }
    | undefined
  >(undefined);
  const [announcement, announce] = useLiveAnnouncement();
  useEffect(() => {
    const keys = assessment.requirements.map((row) => row.key);
    if (previous.current?.titleId === assessment.titleId) {
      const added = addedRequirementCount(previous.current.keys, assessment.requirements);
      if (added > 0) announce(requirementsAddedLabel(added));
    }
    previous.current = {
      titleId: assessment.titleId,
      keys,
    };
  }, [
    assessment,
    announce,
  ]);
  return announcement;
}

/** A polite live region's text: cleared first so a repeat is announced again, then cleared after it has been read. */
function useLiveAnnouncement(): [
  string,
  (text: string) => void,
] {
  const [message, setMessage] = useState('');
  const timers = useRef<ReturnType<typeof setTimeout>[]>([]);
  useEffect(
    () => () => {
      for (const timer of timers.current) clearTimeout(timer);
    },
    [],
  );
  const announce = useCallback((text: string) => {
    for (const timer of timers.current) clearTimeout(timer);
    setMessage('');
    timers.current = [
      setTimeout(() => setMessage(text), ANNOUNCE_DELAY),
      setTimeout(() => setMessage(''), ANNOUNCE_DELAY + ANNOUNCEMENT_LIFETIME),
    ];
  }, []);
  return [
    message,
    announce,
  ];
}

/** Keeps editors mounted per title while the assessment refreshes, preserving context and drafts. */
function WorkbenchEditors({
  title,
  titleId,
  frame,
  outcome,
  initialEditor,
  initialRole,
  initialProviderId,
  extraSections,
  request,
  enrichment,
  outstanding,
}: {
  title: TitleDetail;
  titleId: string;
  frame: 'workspace' | 'page';
  outcome: TitleEditorOutcome;
  initialEditor: TitleEditor;
  initialRole?: ArtworkRole;
  initialProviderId?: string;
  extraSections: TitleWorkbenchSection[];
  request?: EditorRequest;
  enrichment: ReactNode;
  outstanding: number;
}) {
  const { canManageTitles } = usePermissions();
  const [params, setParams] = useSearchParams();
  const {
    editor: linkedEditor,
    requirement,
    role: linkedRole,
    openEditor,
  } = useTitleEditorParams();
  const location = useLocation();
  const discardEpoch = useDiscardEpoch();
  const [defaultEditor] = useState(initialEditor);
  const [defaultProvider] = useState(initialProviderId);
  const [defaultRole] = useState(initialRole);
  const [firstLocation] = useState(location.key);
  // The field inspector opens only for a requirement deep-linked on first load or a row's review action.
  const inspectorRequested =
    location.key === firstLocation ||
    (request?.editor === 'metadata' && request.locationKey === location.key);
  const field =
    inspectorRequested && requirement?.startsWith('Metadata:')
      ? requirement.slice('Metadata:'.length)
      : undefined;
  // A linked requirement names its own provider (IGDB for metadata, the role's provider for artwork), never an earlier one.
  const [linkedKind = '', linkedSubject = ''] = requirement?.split(':') ?? [];
  const providerId = requirement
    ? requirementProviderId({
        kind: linkedKind,
        subject: linkedSubject,
      })
    : defaultProvider;
  const role = linkedRole ?? defaultRole;
  const [refreshOpened, setRefreshOpened] = useState(false);
  const [preview, setPreview] = useState<string | null>(null);
  const available = canManageTitles
    ? titleEditors
    : titleEditors.filter((editor) => editor !== 'match');
  const requestedEditor = linkedEditor ?? defaultEditor;
  const editor = available.find((item) => item === requestedEditor) ?? 'metadata';
  const active =
    frame === 'page' &&
    canManageTitles &&
    (params.get('editor') === 'enrichment' || editor === 'match')
      ? 'enrichment'
      : (extraSections.find((section) => section.value === params.get('editor'))?.value ?? editor);
  const select = (value: string | null) => {
    const next = titleEditors.find((item) => item === value);
    if (next) {
      openEditor(next);
      return;
    }
    if (!value) return;
    const updated = new URLSearchParams(params);
    updated.set('editor', value);
    setParams(updated, {
      state: location.state,
    });
  };
  const panels = useRef<HTMLDivElement>(null);
  // Status shortcuts and deep links also reveal the selected tab in a narrow tab strip.
  useEffect(() => {
    if (!active) return;
    const list = panels.current?.querySelector<HTMLElement>('[role="tablist"]');
    const selected = list?.querySelector<HTMLElement>('[aria-selected="true"]');
    if (!list || !selected) return;
    const strip = list.getBoundingClientRect();
    const item = selected.getBoundingClientRect();
    if (item.left < strip.left) list.scrollLeft += item.left - strip.left;
    else if (item.right > strip.right) list.scrollLeft += item.right - strip.right;
  }, [
    active,
  ]);
  useEditorAutofocus(
    panels,
    request,
    active === 'enrichment' && linkedEditor === 'match' ? 'match' : active,
  );
  return (
    <>
      <EnrichmentLauncher
        opened={refreshOpened}
        onClose={() => setRefreshOpened(false)}
        titleId={titleId}
        titleName={title.name}
        initialMode="Enrich"
      />
      <div ref={panels}>
        <Tabs
          value={active}
          onChange={select}
          keepMounted={false}
          classNames={{
            list: workspace.tabsList,
            tab: workspace.tab,
          }}
        >
          <Tabs.List
            aria-label={frame === 'workspace' ? 'Title enrichment editors' : 'Title sections'}
          >
            {canManageTitles && frame === 'workspace' && <Tabs.Tab value="match">Match</Tabs.Tab>}
            <Tabs.Tab value="metadata">Metadata</Tabs.Tab>
            <Tabs.Tab value="artwork">Artwork</Tabs.Tab>
            {extraSections.map((section) => (
              <Tabs.Tab
                key={section.value}
                value={section.value}
                leftSection={section.icon}
              >
                {section.label}
              </Tabs.Tab>
            ))}
            {canManageTitles && frame === 'page' && (
              <Tabs.Tab value="enrichment">
                Enrichment
                {outstanding > 0 && (
                  <Badge
                    size="sm"
                    variant="light"
                    ml="xs"
                  >
                    {outstanding}
                  </Badge>
                )}
              </Tabs.Tab>
            )}
          </Tabs.List>
          {canManageTitles && frame === 'workspace' && (
            <Tabs.Panel
              value="match"
              pt="md"
            >
              <ProviderMatches
                key={titleId}
                titleId={titleId}
                titleName={title.name}
                initialProviderId={providerId}
                providerRequestKey={`${location.key}:${discardEpoch}`}
                inline
                {...outcome}
              />
            </Tabs.Panel>
          )}
          {canManageTitles && frame === 'page' && (
            <Tabs.Panel
              value="enrichment"
              pt="md"
            >
              <Stack gap="lg">
                <Group
                  justify="space-between"
                  align="flex-start"
                >
                  <Text
                    component="h2"
                    className="romd-section-heading"
                    m={0}
                  >
                    Enrichment
                  </Text>
                  <Group gap="xs">
                    <Button
                      {...workspaceActionProps}
                      variant="subtle"
                      onClick={() => setRefreshOpened(true)}
                    >
                      Refresh provider data
                    </Button>
                    <Button
                      component={Link}
                      to={`/enrichment?title=${titleId}`}
                      {...workspaceActionProps}
                      variant="light"
                    >
                      Open enrichment workspace
                    </Button>
                  </Group>
                </Group>
                {enrichment}
                <ProviderMatches
                  key={titleId}
                  titleId={titleId}
                  titleName={title.name}
                  initialProviderId={providerId}
                  providerRequestKey={`${location.key}:${discardEpoch}`}
                  inline
                  {...outcome}
                />
              </Stack>
            </Tabs.Panel>
          )}
          <Tabs.Panel
            value="metadata"
            pt="md"
          >
            <MetadataPanel
              title={title}
              titleId={titleId}
              initialField={field}
              fieldRequestKey={location.key}
              {...outcome}
            />
          </Tabs.Panel>
          <Tabs.Panel
            value="artwork"
            pt="md"
          >
            <ArtworkPanel
              key={`${titleId}:${role ?? 'Backdrop'}:${discardEpoch}`}
              title={title}
              titleId={titleId}
              initialRole={role}
              {...outcome}
            />
          </Tabs.Panel>
          {extraSections.map((section) => (
            <Tabs.Panel
              key={section.value}
              value={section.value}
              pt="md"
            >
              {section.content}
            </Tabs.Panel>
          ))}
        </Tabs>
      </div>
      {frame === 'workspace' && active !== 'artwork' && (
        <Accordion
          variant="default"
          value={preview}
          onChange={setPreview}
        >
          <Accordion.Item value="preview">
            <Accordion.Control>
              <Text size="sm">Preview catalog presentation</Text>
            </Accordion.Control>
            <Accordion.Panel>
              {preview === 'preview' && <CatalogPresentationPreview title={title} />}
            </Accordion.Panel>
          </Accordion.Item>
        </Accordion>
      )}
    </>
  );
}

/** Opening an editor from a checklist row focuses the editor's first control once it renders. */
function useEditorAutofocus(
  panels: RefObject<HTMLDivElement | null>,
  request: EditorRequest | undefined,
  active: string,
) {
  const handled = useRef<number | undefined>(undefined);
  useEffect(() => {
    const container = panels.current;
    if (!request || !container || request.editor !== active || handled.current === request.id)
      return;
    const focusFirst = () => {
      const target = container.querySelector<HTMLElement>('[role="tabpanel"] [data-autofocus]');
      if (!target) return false;
      handled.current = request.id;
      target.focus();
      return true;
    };
    if (focusFirst()) return;
    const observer = new MutationObserver(() => {
      if (focusFirst()) observer.disconnect();
    });
    observer.observe(container, {
      childList: true,
      subtree: true,
    });
    // An editor with no first control (the field inspector is a modal) must not take focus later.
    const giveUp = setTimeout(() => {
      handled.current = request.id;
      observer.disconnect();
    }, AUTOFOCUS_WAIT);
    return () => {
      clearTimeout(giveUp);
      observer.disconnect();
    };
  }, [
    panels,
    request,
    active,
  ]);
}
