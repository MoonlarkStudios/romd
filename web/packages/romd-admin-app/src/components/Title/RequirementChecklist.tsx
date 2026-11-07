import { Accordion, Badge, Button, Group, Text, Title } from '@mantine/core';
import type {
  EnrichmentAssessment,
  EnrichmentRequirement,
  ProviderGame,
} from '@romd/admin-api-client';
import { IconCheck } from '@tabler/icons-react';
import { type ReactNode, useId } from 'react';
import { Link } from 'react-router';
import { usePermissions } from '../../hooks/usePermissions';
import { workspaceActionProps } from '../Workspace/workspaceActions';
import {
  configureProviderHint,
  enrichActionLabel,
  isOutstanding,
  matchedGameCopy,
  outstandingLabel,
  requirementCopy,
  requirementSubjectLabel,
  returnToAutomaticLabel,
  reviewActionLabel,
  revisitOmissionLabel,
  satisfiedActionLabel,
  titleStatusLabels,
} from './enrichmentPresentation';
import { titleStatusColors } from './TitleStatusChips';
import classes from './TitleWorkbench.module.css';
import { type ChecklistGroup, checklistGroups, isReviewAction } from './titleWorkbenchModel';
import { type ConfirmedAction, confirmedActions } from './useRequirementDecisions';

export interface RequirementActions {
  review: (row: EnrichmentRequirement, action: string) => void;
  enrich: () => void;
  confirm: (action: ConfirmedAction, row: EnrichmentRequirement) => void;
  confirmLabel: (action: ConfirmedAction, row: EnrichmentRequirement) => string;
  revisit: (row: EnrichmentRequirement) => void;
  returnToAutomatic: (row: EnrichmentRequirement) => void;
}

/**
 * The complete current requirement list, grouped by kind. Rows are focus targets; only the current row leads with a
 * filled action, and satisfied rows recede: subdued copy that wraps rather than truncates, with small, reachable actions.
 */
export function RequirementChecklist({
  assessment,
  currentKey,
  headingOrder,
  matchedGames,
  pendingSubject,
  registerRow,
  actions,
  collapseCompleted = false,
}: {
  assessment: EnrichmentAssessment;
  collapseCompleted?: boolean;
  currentKey?: string;
  headingOrder: 2 | 3;
  matchedGames: ReadonlyMap<string, ProviderGame>;
  pendingSubject?: string;
  registerRow: (key: string, element: HTMLLIElement | null) => void;
  actions: RequirementActions;
}) {
  const renderGroups = (requirements: EnrichmentRequirement[]) => (
    <div className={classes.groups}>
      {checklistGroups(requirements).map((group) => (
        <RequirementGroup
          key={group.kind}
          group={group}
          headingOrder={headingOrder}
        >
          {group.rows.map((row) => (
            <RequirementRow
              key={row.key}
              row={row}
              current={row.key === currentKey}
              hasActiveWork={assessment.hasActiveWork}
              game={matchedGames.get(row.subject)}
              pending={pendingSubject === row.subject}
              registerRow={registerRow}
              actions={actions}
            />
          ))}
        </RequirementGroup>
      ))}
    </div>
  );
  const completed = assessment.requirements.filter(
    (row) => row.state === 'Satisfied' && row.kind !== 'Operation',
  );
  return (
    <div className={classes.requirements}>
      {renderGroups(
        collapseCompleted ? assessment.requirements.filter(isOutstanding) : assessment.requirements,
      )}
      {collapseCompleted && completed.length > 0 && (
        <Accordion variant="default">
          <Accordion.Item value="completed">
            <Accordion.Control>Completed ({completed.length})</Accordion.Control>
            <Accordion.Panel>{renderGroups(completed)}</Accordion.Panel>
          </Accordion.Item>
        </Accordion>
      )}
    </div>
  );
}

function RequirementGroup({
  group,
  headingOrder,
  children,
}: {
  group: ChecklistGroup;
  headingOrder: 2 | 3;
  children: ReactNode;
}) {
  const headingId = useId();
  return (
    <section
      className={classes.group}
      data-kind={group.kind}
      aria-labelledby={headingId}
    >
      <Group
        gap="xs"
        align="baseline"
      >
        <Title
          id={headingId}
          order={headingOrder}
          className={`romd-card-title ${classes.groupHeading}`}
        >
          {group.kind}
        </Title>
        {group.outstanding > 0 && (
          <Text
            size="sm"
            c="dimmed"
          >
            {outstandingLabel(group.outstanding)}
          </Text>
        )}
      </Group>
      <ul className={classes.rows}>{children}</ul>
    </section>
  );
}

function RequirementRow({
  row,
  current,
  hasActiveWork,
  game,
  pending,
  registerRow,
  actions,
}: {
  row: EnrichmentRequirement;
  current: boolean;
  hasActiveWork: boolean;
  game?: ProviderGame;
  pending: boolean;
  registerRow: (key: string, element: HTMLLIElement | null) => void;
  actions: RequirementActions;
}) {
  const subject = requirementSubjectLabel(row);
  const satisfied = row.state === 'Satisfied';
  const copy =
    satisfied && row.satisfiedBy === 'Trusted' && game
      ? matchedGameCopy(game)
      : requirementCopy(row);
  return (
    <li
      ref={(element) => registerRow(row.key, element)}
      tabIndex={-1}
      aria-label={`${subject}: ${copy}`}
      className={classes.row}
      data-current={current || undefined}
      data-satisfied={satisfied || undefined}
    >
      <div className={classes.rowLine}>
        <div className={classes.rowText}>
          {row.state === 'Satisfied' ? (
            <IconCheck
              size={16}
              className={classes.stateIcon}
              aria-hidden="true"
            />
          ) : (
            <Badge
              size="xs"
              variant="light"
              color={titleStatusColors[row.state]}
              style={{
                flexShrink: 0,
              }}
            >
              {titleStatusLabels[row.state]}
            </Badge>
          )}
          <Text size="sm">
            <Text
              span
              inherit
              fw={600}
            >
              {subject}
            </Text>{' '}
            ·{' '}
            <Text
              span
              inherit
              c={satisfied ? 'dimmed' : undefined}
            >
              {copy}
            </Text>
          </Text>
        </div>
        <div className={classes.rowActions}>
          {satisfied ? (
            <SatisfiedActions
              row={row}
              pending={pending}
              actions={actions}
            />
          ) : (
            <OpenActions
              row={row}
              current={current}
              hasActiveWork={hasActiveWork}
              actions={actions}
            />
          )}
        </div>
      </div>
    </li>
  );
}

function OpenActions({
  row,
  current,
  hasActiveWork,
  actions,
}: {
  row: EnrichmentRequirement;
  current: boolean;
  hasActiveWork: boolean;
  actions: RequirementActions;
}) {
  const { canConfigureIntegrations } = usePermissions();
  const secondary = current ? 'default' : 'subtle';
  return (
    <>
      {row.actions.filter(isReviewAction).map((action, index) => (
        <Button
          key={action}
          {...workspaceActionProps}
          size="compact-sm"
          variant={current && index === 0 ? 'filled' : 'subtle'}
          onClick={() => actions.review(row, action)}
        >
          {reviewActionLabel(action, row)}
        </Button>
      ))}
      {(row.actions.includes('Enrich') || row.actions.includes('Retry')) && (
        <Button
          {...workspaceActionProps}
          size="compact-sm"
          variant={current ? 'light' : 'subtle'}
          disabled={hasActiveWork}
          onClick={actions.enrich}
        >
          {enrichActionLabel(row)}
        </Button>
      )}
      {confirmedActions
        .filter((action) => row.actions.includes(action))
        .map((action) => (
          <Button
            key={action}
            {...workspaceActionProps}
            size="compact-sm"
            variant={secondary}
            onClick={() => actions.confirm(action, row)}
          >
            {actions.confirmLabel(action, row)}
          </Button>
        ))}
      {row.actions.includes('ConfigureProvider') &&
        (canConfigureIntegrations ? (
          <Button
            component={Link}
            to="/integrations"
            size="compact-sm"
            variant={secondary}
          >
            Provider settings
          </Button>
        ) : (
          <Text
            size="sm"
            c="dimmed"
          >
            {configureProviderHint(row)}
          </Text>
        ))}
    </>
  );
}

function SatisfiedActions({
  row,
  pending,
  actions,
}: {
  row: EnrichmentRequirement;
  pending: boolean;
  actions: RequirementActions;
}) {
  const review = row.actions.find(isReviewAction);
  return (
    <>
      {review && (
        <Button
          {...workspaceActionProps}
          size="compact-xs"
          variant="subtle"
          onClick={() => actions.review(row, review)}
        >
          {satisfiedActionLabel(review, row)}
        </Button>
      )}
      {row.satisfiedBy === 'AcceptedOmission' && (
        <Button
          {...workspaceActionProps}
          size="compact-xs"
          variant="subtle"
          loading={pending}
          onClick={() => actions.revisit(row)}
        >
          {revisitOmissionLabel(row.subject)}
        </Button>
      )}
      {row.satisfiedBy === 'LeftEmpty' && (
        <Button
          {...workspaceActionProps}
          size="compact-xs"
          variant="subtle"
          loading={pending}
          onClick={() => actions.returnToAutomatic(row)}
        >
          {returnToAutomaticLabel(row.subject)}
        </Button>
      )}
    </>
  );
}
