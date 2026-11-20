import { Anchor, Badge, Button, Group, Stack, Text, Title } from '@mantine/core';
import type { DiscSet, TitleSourceReference } from '@romd/admin-api-client';
import { IconPin, IconPinnedOff } from '@tabler/icons-react';
import { Link } from 'react-router';
import { InspectorPanel } from '../../components/Workspace/InspectorPanel';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import type { TitleDetailView, TitleReleaseView } from '../../hooks/api/useTitleDetail';
import { SourceStatusBadge } from '../Systems/SourceStatusBadge';
import { FileRequirements } from './FileRequirements';
import { ReleaseDownloadButton } from './ReleaseDownloadButton';
import classes from './ReleasesPanel.module.css';

export type ReleasePreference = Pick<
  TitleDetailView,
  'isTracked' | 'pinnedCatalogReleaseId' | 'preferredCatalogReleaseId' | 'releaseRegionPriority'
>;

export function ReleaseInspector({
  id,
  release,
  titleId,
  references,
  sourceName,
  preference,
  canManageTitles,
  savingPreference,
  onSetPin,
  discSets,
  onReviewDiscSet,
  onClose,
}: {
  id: string;
  release: TitleReleaseView;
  titleId: string;
  references: TitleSourceReference[];
  sourceName?: string;
  preference?: ReleasePreference;
  canManageTitles: boolean;
  savingPreference: boolean;
  onSetPin: (catalogReleaseId: string | null) => void;
  discSets?: DiscSet[];
  onReviewDiscSet: (id: string) => void;
  onClose: () => void;
}) {
  const files = release.files ?? [];
  const canonicalId = release.catalogReleaseId;
  const preferred = !!canonicalId && canonicalId === preference?.preferredCatalogReleaseId;
  const pinned = !!canonicalId && canonicalId === preference?.pinnedCatalogReleaseId;
  const priorities = [
    ...new Set(preference?.releaseRegionPriority?.map((value) => value.toUpperCase())),
  ];
  const sources = release.sources?.length
    ? release.sources
    : [
        {
          datId: release.datId,
          datGameId: release.id,
          datName: sourceName ?? 'Catalog source',
          gameName: release.name,
        },
      ];
  const memberships =
    discSets?.flatMap((set) => {
      const definition = set.correction ?? set.proposal;
      const member =
        canonicalId && definition.members.find(({ releaseId }) => releaseId === canonicalId);
      return member
        ? [
            {
              set,
              definition,
              ordinal: member.ordinal,
            },
          ]
        : [];
    }) ?? [];

  return (
    <InspectorPanel
      id={id}
      title={release.name}
      onClose={onClose}
      footer={
        <Stack gap="xs">
          <ReleaseDownloadButton
            files={files}
            formats={release.archiveFormats ?? []}
            name={release.name}
            releaseId={canonicalId}
          />
          {canManageTitles && (
            <Stack
              gap={4}
              align="flex-start"
            >
              <Button
                {...workspaceActionProps}
                variant="light"
                size="compact-sm"
                leftSection={pinned ? <IconPinnedOff size={16} /> : <IconPin size={16} />}
                disabled={!canonicalId}
                loading={savingPreference}
                onClick={() => {
                  if (canonicalId) onSetPin(pinned ? null : canonicalId);
                }}
              >
                {pinned ? 'Use automatic preference' : 'Use as preferred'}
              </Button>
              {!canonicalId ? (
                <Text
                  size="xs"
                  c="dimmed"
                >
                  This release has no catalog ID to pin.
                </Text>
              ) : !preference?.isTracked && !pinned ? (
                <Text
                  size="xs"
                  c="dimmed"
                >
                  This also tracks the title.
                </Text>
              ) : null}
            </Stack>
          )}
        </Stack>
      }
    >
      <Stack gap="md">
        <Stack gap="xs">
          <Text
            size="sm"
            c="dimmed"
          >
            {[
              release.region || 'Unspecified region',
              release.revisionLabel,
              release.language,
              release.year,
              release.manufacturer,
            ]
              .filter(Boolean)
              .join(' · ')}
          </Text>
          {preferred && (
            <div>
              <Badge
                variant="light"
                size="sm"
              >
                Preferred · {pinned ? 'Pinned' : 'Automatic'}
              </Badge>
              <Text
                size="xs"
                c="dimmed"
                mt={4}
              >
                {pinned
                  ? 'Pinned releases override automatic preference.'
                  : `Automatic preference considers dump quality, region priority${priorities.length ? ` (${priorities.join(' → ')})` : ''}, then the highest numeric revision.`}
              </Text>
            </div>
          )}
          {release.description && release.description !== release.name && (
            <Text size="sm">{release.description}</Text>
          )}
        </Stack>
        <section
          id="release-files"
          className={classes.inspectorSection}
        >
          <Title
            order={3}
            className="romd-card-title"
          >
            Files ({files.length})
          </Title>
          <FileRequirements files={files} />
        </section>
        {memberships.length > 0 && (
          <section className={classes.inspectorSection}>
            <Title
              order={3}
              className="romd-card-title"
            >
              Disc sets
            </Title>
            <Stack gap="sm">
              {memberships.map(({ set, definition, ordinal }) => (
                <Stack
                  key={set.id}
                  gap={4}
                  align="flex-start"
                >
                  <Group gap="xs">
                    <Text size="sm">{definition.name}</Text>
                    <Badge
                      size="xs"
                      variant="light"
                    >
                      {set.decision}
                    </Badge>
                  </Group>
                  <Text
                    size="xs"
                    c="dimmed"
                  >
                    Disc {ordinal}
                    {definition.expectedDiscCount != null
                      ? ` of ${definition.expectedDiscCount}`
                      : ''}
                  </Text>
                  <Button
                    {...workspaceActionProps}
                    size="compact-sm"
                    variant="subtle"
                    onClick={() => onReviewDiscSet(set.id)}
                    aria-label={`Review disc set ${definition.name}`}
                  >
                    Review disc set
                  </Button>
                </Stack>
              ))}
            </Stack>
          </section>
        )}
        <section className={classes.inspectorSection}>
          <Title
            order={3}
            className="romd-card-title"
          >
            Catalog sources ({sources.length})
          </Title>
          <ul className={classes.sources}>
            {sources.map((source) => {
              const reference = references.find((item) => item.datId === source.datId);
              return (
                <li key={source.datGameId}>
                  <Group gap="xs">
                    {reference?.systemKey ? (
                      <Anchor
                        component={Link}
                        size="sm"
                        to={`/systems/${reference.systemKey}?dat=${source.datId}&sourceTitle=${titleId}`}
                      >
                        {source.datName}
                      </Anchor>
                    ) : (
                      <Text size="sm">{source.datName}</Text>
                    )}
                    {reference && <SourceStatusBadge status={reference.status} />}
                  </Group>
                  <Text
                    size="xs"
                    c="dimmed"
                  >
                    {source.gameName}
                  </Text>
                </li>
              );
            })}
          </ul>
        </section>
      </Stack>
    </InspectorPanel>
  );
}
