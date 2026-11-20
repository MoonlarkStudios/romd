import { Alert, Badge, Button, Group, Skeleton, Stack, Text } from '@mantine/core';
import {
  IconPencil,
  IconPhoto,
  IconShieldCheck,
  IconSparkles,
  IconStack,
} from '@tabler/icons-react';
import { useState } from 'react';
import { Link, useLocation, useSearchParams } from 'react-router';
import { useEnrichmentAssessment } from '../../hooks/api/useEnrichmentWorkspace';
import type { TitleDetailView } from '../../hooks/api/useTitleDetail';
import { usePermissions } from '../../hooks/usePermissions';
import { useDiscardEpoch } from '../Administration/DraftGuard';
import { artworkRoles } from '../artworkPresentation';
import { EnrichmentLauncher } from '../Enrichment/EnrichmentLauncher';
import { SectionTabs } from '../Workspace/SectionTabs';
import { workspaceActionProps } from '../Workspace/workspaceActions';
import { ArtworkPanel } from './ArtworkPanel';
import {
  outstandingCount,
  outstandingLabel,
  requirementProviderId,
} from './enrichmentPresentation';
import { MetadataPanel } from './MetadataPanel';
import { RequirementReview } from './RequirementReview';
import { TitleStatusChips } from './TitleStatusChips';
import classes from './TitleWorkbench.module.css';
import {
  type TitleSection,
  titleSectionLabels,
  titleSectionPath,
  titleSections,
  titleSourcesAnchor,
} from './titleSections';

const icons = {
  metadata: IconPencil,
  artwork: IconPhoto,
  releases: IconStack,
  ratings: IconShieldCheck,
};

export function TitleWorkbench({
  title,
  section,
  children,
}: {
  title: TitleDetailView;
  section: TitleSection;
  children?: React.ReactNode;
}) {
  const titleId = title.id;
  const { canManageTitles } = usePermissions();
  const assessment = useEnrichmentAssessment(canManageTitles ? titleId : undefined);
  const [params] = useSearchParams();
  const { key, state, hash } = useLocation();
  const epoch = useDiscardEpoch();
  const [launching, setLaunching] = useState(false);
  const requirement = params.get('requirement') ?? undefined;
  const row = assessment.data?.requirements.find((item) => item.key === requirement);
  const role = artworkRoles.find((candidate) => candidate === params.get('role'));
  const requestedField =
    params.get('field') ||
    (requirement?.startsWith('Metadata:') ? requirement.slice(9) : undefined);
  const showMatch = params.get('match') === '1';
  const outstanding = assessment.data ? outstandingCount(assessment.data.requirements) : 0;

  const status = (
    <Stack gap="sm">
      {assessment.data ? (
        <RequirementReview
          key={titleId}
          assessment={assessment.data}
          currentKey={requirement}
          heading={false}
          navigationState={state}
          onRefresh={() => void assessment.refetch()}
        />
      ) : assessment.isError ? (
        <Alert
          color="yellow"
          title="Enrichment status unavailable"
        >
          <Button
            variant="subtle"
            size="compact-sm"
            onClick={() => void assessment.refetch()}
          >
            Retry
          </Button>
        </Alert>
      ) : (
        <Skeleton
          h={72}
          aria-label="Loading enrichment status"
        />
      )}
      <Group gap="xs">
        {section !== 'metadata' && (
          <Button
            {...workspaceActionProps}
            component={Link}
            to={`${titleSectionPath(titleId)}#${titleSourcesAnchor}`}
            state={state}
            size="compact-sm"
            variant="light"
          >
            Manage sources
          </Button>
        )}
        <Button
          {...workspaceActionProps}
          size="compact-sm"
          variant="subtle"
          leftSection={<IconSparkles size={15} />}
          onClick={() => setLaunching(true)}
        >
          Enrich title
        </Button>
      </Group>
    </Stack>
  );

  return (
    <Stack gap="md">
      <SectionTabs
        label="Title sections"
        current={section}
        state={state}
        items={titleSections.map((id) => {
          const Icon = icons[id];
          const count =
            id === 'releases'
              ? title.releases.length
              : id === 'ratings'
                ? (title.contentRatings?.length ?? 0)
                : undefined;
          return {
            id,
            label: titleSectionLabels[id],
            icon: (
              <Icon
                size={16}
                aria-hidden="true"
              />
            ),
            count,
            to:
              id === section
                ? {
                    search: params.toString() ? `?${params}` : '',
                    hash,
                  }
                : titleSectionPath(titleId, id),
          };
        })}
      />
      {canManageTitles && (
        <aside
          id="title-status"
          aria-label="Title status"
          className={classes.status}
        >
          <details
            className={classes.summary}
            key={`${section}:${requirement ?? ''}:${hash}`}
            open={!!requirement || hash === '#title-status' || undefined}
          >
            <summary>
              <Group
                component="span"
                gap="xs"
                display="inline-flex"
              >
                <Text
                  component="span"
                  fw={600}
                  size="sm"
                >
                  Enrichment status
                </Text>
                {assessment.data && <TitleStatusChips flags={assessment.data} />}
                {outstanding > 0 && (
                  <Badge
                    variant="light"
                    color="gray"
                    size="sm"
                  >
                    {outstandingLabel(outstanding)}
                  </Badge>
                )}
              </Group>
            </summary>
            <div className={classes.statusBody}>{status}</div>
          </details>
        </aside>
      )}
      <section
        className={classes.section}
        aria-label={titleSectionLabels[section]}
      >
        {section === 'metadata' ? (
          <MetadataPanel
            title={title}
            titleId={titleId}
            initialField={requestedField}
            fieldRequestKey={key}
            initialProviderId={showMatch && row ? requirementProviderId(row) : undefined}
            providerRequestKey={showMatch ? key : undefined}
          />
        ) : section === 'artwork' ? (
          <ArtworkPanel
            key={`${titleId}:${role ?? ''}:${epoch}`}
            title={title}
            titleId={titleId}
            initialRole={role}
          />
        ) : (
          children
        )}
      </section>
      {canManageTitles && (
        <EnrichmentLauncher
          opened={launching}
          onClose={() => setLaunching(false)}
          titleId={titleId}
          titleName={title.name}
          initialMode="Enrich"
        />
      )}
    </Stack>
  );
}
