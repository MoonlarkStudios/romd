import {
  Alert,
  Button,
  CopyButton,
  Group,
  Skeleton,
  Stack,
  Text,
  Tooltip,
  UnstyledButton,
} from '@mantine/core';
import type { EnrichmentAssessment } from '@romd/admin-api-client';
import {
  IconCheck,
  IconCopy,
  IconFile,
  IconPhoto,
  IconShieldCheck,
  IconStack,
} from '@tabler/icons-react';
import { type ReactNode, useEffect } from 'react';
import { Link } from 'react-router';
import { titleRequirementPath, titleSectionPath } from '../../components/Title/titleSections';
import { InspectorPanel } from '../../components/Workspace/InspectorPanel';
import { LinkedCountList } from '../../components/Workspace/LinkedCountList';
import { RequirementList } from '../../components/Workspace/RequirementList';
import { TitleIdentity } from '../../components/Workspace/TitleIdentity';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useEnrichmentAssessment } from '../../hooks/api/useEnrichmentWorkspace';
import { usePlatform } from '../../hooks/api/usePlatforms';
import { TitleNotFoundError, useTitleDetail } from '../../hooks/api/useTitleDetail';
import { usePermissions } from '../../hooks/usePermissions';
import { CatalogTrackingAction } from './CatalogTrackingAction';
import { rememberCatalogPosition } from './catalogNavigation';

export const catalogInspectorId = 'catalog-title-inspector';

export function CatalogInspector({
  titleId,
  returnPath,
  close,
  onUnknown,
  previous,
  next,
  fallbackAssessment,
  renderRequirements,
}: {
  titleId: string;
  returnPath: string;
  close: () => void;
  onUnknown?: () => void;
  previous?: () => void;
  next?: () => void;
  fallbackAssessment?: EnrichmentAssessment;
  renderRequirements?: (assessment: EnrichmentAssessment, refresh: () => void) => ReactNode;
}) {
  const { canManageTitles } = usePermissions();
  const detail = useTitleDetail(titleId, false);
  const title = detail.data;
  const platform = usePlatform(title?.systemKey);
  const assessment = useEnrichmentAssessment(canManageTitles ? titleId : undefined);
  const status = assessment.data ?? fallbackAssessment;
  const missing = detail.error instanceof TitleNotFoundError;
  useEffect(() => {
    if (missing) onUnknown?.();
  }, [
    missing,
    onUnknown,
  ]);
  if (missing) return null;

  const withFiles = title?.releases.find((release) => release.files?.length);
  const filesPath =
    titleSectionPath(titleId, 'releases') +
    (withFiles ? `?release=${encodeURIComponent(withFiles.id)}#release-files` : '');
  const openPath = titleSectionPath(titleId);
  const state = {
    catalogReturnTo: returnPath,
  };
  const titleLinkProps = {
    state,
    onClick: (event: React.MouseEvent<HTMLAnchorElement>) =>
      rememberCatalogPosition(returnPath, event),
  };
  return (
    <InspectorPanel
      id={catalogInspectorId}
      title={title?.name ?? fallbackAssessment?.titleName ?? 'Title preview'}
      onClose={close}
      navigation={{
        onPrevious: previous,
        onNext: next,
      }}
      footer={
        title && (
          <Group
            gap="xs"
            justify="space-between"
          >
            <Button
              {...workspaceActionProps}
              size="compact-sm"
              component={Link}
              to={openPath}
              {...titleLinkProps}
            >
              Open title
            </Button>
            {canManageTitles && (
              <CatalogTrackingAction
                title={title}
                expanded
              />
            )}
          </Group>
        )
      }
    >
      <Stack gap="md">
        {title ? (
          <>
            <TitleIdentity
              title={title}
              platform={platform.data?.name}
              showName={false}
            >
              <CopyButton value={title.id}>
                {({ copied, copy }) => (
                  <Tooltip label={copied ? 'Copied' : 'Copy title ID'}>
                    <UnstyledButton
                      onClick={copy}
                      aria-label="Copy title ID"
                    >
                      <Group
                        gap={5}
                        wrap="nowrap"
                      >
                        <Text
                          size="xs"
                          className="romd-metadata"
                          c="dimmed"
                        >
                          {title.id}
                        </Text>
                        {copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
                      </Group>
                    </UnstyledButton>
                  </Tooltip>
                )}
              </CopyButton>
            </TitleIdentity>
            {title.description && (
              <Text
                size="sm"
                lineClamp={4}
              >
                {title.description}
              </Text>
            )}
          </>
        ) : detail.isError ? (
          <Alert
            color="red"
            title="Could not load title preview"
          >
            <Button
              variant="subtle"
              size="compact-sm"
              onClick={() => void detail.refetch()}
            >
              Retry
            </Button>
          </Alert>
        ) : (
          <Skeleton
            h={120}
            aria-label="Loading title preview"
          />
        )}
        {status ? (
          (renderRequirements?.(status, () => void assessment.refetch()) ?? (
            <RequirementList
              assessment={status}
              href={(row) => titleRequirementPath(titleId, row)}
              state={state}
              onOpen={titleLinkProps.onClick}
            />
          ))
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
              Retry status
            </Button>
          </Alert>
        ) : null}
        {title && (
          <LinkedCountList
            state={state}
            onOpen={titleLinkProps.onClick}
            items={[
              {
                label: 'Releases',
                count: title.releases.length,
                to: titleSectionPath(titleId, 'releases'),
                icon: <IconStack size={16} />,
              },
              {
                label: 'Files',
                count: title.releases.reduce(
                  (count, release) => count + (release.files?.length ?? 0),
                  0,
                ),
                to: filesPath,
                icon: <IconFile size={16} />,
              },
              {
                label: 'Artwork',
                count: title.media.length,
                to: titleSectionPath(titleId, 'artwork'),
                icon: <IconPhoto size={16} />,
              },
              {
                label: 'Age ratings',
                count: title.contentRatings?.length ?? 0,
                to: titleSectionPath(titleId, 'ratings'),
                icon: <IconShieldCheck size={16} />,
              },
            ]}
          />
        )}
      </Stack>
    </InspectorPanel>
  );
}
