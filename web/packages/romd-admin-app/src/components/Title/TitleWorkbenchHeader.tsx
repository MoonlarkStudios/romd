import { Anchor, Group, Text, Title } from '@mantine/core';
import type { EnrichmentAssessment } from '@romd/admin-api-client';
import type { ReactNode, Ref } from 'react';
import { Link } from 'react-router';
import workspace from '../Workspace/Workspace.module.css';
import { gapSummary, noLongerInViewLabel, trackingLabel } from './enrichmentPresentation';
import { TitleStatusChips } from './TitleStatusChips';
import classes from './TitleWorkbench.module.css';

/**
 * Identity and status for the selected title. The workspace frame owns the heading and chips; on the title page
 * the hero already shows both, so this renders only what the hero does not: the gap summary. What Settled means is
 * the Settled chip's description, so "Nothing outstanding" appears once. The workspace header is a size container.
 */
export function TitleWorkbenchHeader({
  frame,
  assessment,
  titleName,
  isTracked,
  noLongerInView,
  navigation,
  headingRef,
  summaryRef,
}: {
  frame: 'workspace' | 'page';
  assessment: EnrichmentAssessment;
  titleName: string;
  isTracked?: boolean;
  noLongerInView: boolean;
  navigation?: ReactNode;
  headingRef: Ref<HTMLHeadingElement>;
  summaryRef: Ref<HTMLParagraphElement>;
}) {
  const summary = (
    <Text
      ref={summaryRef}
      tabIndex={-1}
      size="sm"
      className={classes.heading}
    >
      {gapSummary(assessment)}
    </Text>
  );
  if (frame === 'page') return summary;
  const context = [
    assessment.platformName,
    isTracked === undefined ? undefined : trackingLabel(isTracked),
  ]
    .filter(Boolean)
    .join(' · ');
  return (
    <div className={classes.header}>
      <div className={classes.headerBar}>
        <div className={classes.identity}>
          <Group
            gap="sm"
            align="center"
          >
            <Title
              ref={headingRef}
              tabIndex={-1}
              order={2}
              className={`${workspace.sectionHeading} ${classes.heading}`}
            >
              {titleName}
            </Title>
            <TitleStatusChips
              flags={assessment}
              describeSettled
            />
          </Group>
          <Text
            size="sm"
            c="dimmed"
          >
            {context}
          </Text>
          {summary}
          <Anchor
            component={Link}
            to={`/titles/${assessment.titleId}`}
            size="sm"
            w="fit-content"
          >
            Title details
          </Anchor>
        </div>
        {navigation && <div className={classes.navigation}>{navigation}</div>}
      </div>
      {noLongerInView && (
        <Text
          size="sm"
          c="dimmed"
          role="status"
        >
          {noLongerInViewLabel}
        </Text>
      )}
    </div>
  );
}
