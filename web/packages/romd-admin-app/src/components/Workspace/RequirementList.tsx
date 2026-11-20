import { Badge, Stack, Text, Title } from '@mantine/core';
import type { EnrichmentAssessment, EnrichmentRequirement } from '@romd/admin-api-client';
import { IconChevronRight } from '@tabler/icons-react';
import type { MouseEventHandler, ReactNode } from 'react';
import { Link, type To } from 'react-router';
import {
  outstandingLabel,
  outstandingRequirements,
  requirementCopy,
  requirementSubjectLabel,
  titleStatusLabels,
} from '../Title/enrichmentPresentation';
import { TitleStatusChips, titleStatusColors } from '../Title/TitleStatusChips';
import classes from './RequirementList.module.css';

export function RequirementList({
  assessment,
  currentKey,
  href,
  state,
  onOpen,
  renderDecision,
  extra,
  heading = true,
}: {
  assessment: EnrichmentAssessment;
  currentKey?: string;
  href: (requirement: EnrichmentRequirement) => To;
  state?: unknown;
  onOpen?: MouseEventHandler<HTMLAnchorElement>;
  renderDecision?: (requirement: EnrichmentRequirement) => ReactNode;
  extra?: ReactNode;
  heading?: boolean;
}) {
  const outstanding = outstandingRequirements(assessment.requirements);
  // An item reached through a deep link remains locatable after its decision was saved.
  const linked = assessment.requirements.find((item) => item.key === currentKey);
  const shown =
    linked && !outstanding.includes(linked)
      ? [
          ...outstanding,
          linked,
        ]
      : outstanding;

  return (
    <Stack gap="xs">
      {heading && (
        <Title
          order={3}
          className="romd-section-heading"
        >
          Enrichment status
        </Title>
      )}
      <TitleStatusChips
        flags={assessment}
        describeSettled
      />
      <Text
        size="sm"
        c="dimmed"
        aria-live="polite"
      >
        {outstanding.length ? outstandingLabel(outstanding.length) : 'No outstanding requirements'}
      </Text>
      {extra}
      {shown.length > 0 && (
        <ul
          className={classes.list}
          aria-label="Title requirements"
        >
          {shown.map((requirement) => (
            <li
              key={requirement.key}
              className={classes.requirement}
              data-current={requirement.key === currentKey || undefined}
            >
              <Link
                to={href(requirement)}
                state={state}
                onClick={onOpen}
                className={classes.requirementLink}
                data-review-link={requirement.key === currentKey || undefined}
              >
                <span>{requirementSubjectLabel(requirement)}</span>
                <IconChevronRight
                  size={15}
                  aria-hidden="true"
                />
              </Link>
              <Text
                size="xs"
                c="dimmed"
              >
                {requirementCopy(requirement)}
              </Text>
              <Badge
                size="xs"
                variant="light"
                color={
                  requirement.state === 'Satisfied' ? 'green' : titleStatusColors[requirement.state]
                }
              >
                {requirement.state === 'Satisfied'
                  ? 'Satisfied'
                  : titleStatusLabels[requirement.state]}
              </Badge>
              {renderDecision?.(requirement)}
            </li>
          ))}
        </ul>
      )}
    </Stack>
  );
}
