import { Checkbox, Group, Text, Tooltip, UnstyledButton } from '@mantine/core';
import type { CatalogTitle } from '@romd/admin-api-client';
import { IconChevronRight, IconPhoto, IconPhotoOff } from '@tabler/icons-react';
import { type MouseEventHandler, memo } from 'react';
import { ArtworkFrame } from '../../components/ArtworkFrame';
import { artworkRoleLabel, artworkRoles } from '../../components/artworkPresentation';
import { titleSectionPath } from '../../components/Title/titleSections';
import type { useInspectorSelection } from '../../components/Workspace/useInspectorSelection';
import classes from './Catalog.module.css';
import { CatalogEnrichmentStatus } from './CatalogEnrichmentStatus';
import { catalogInspectorId } from './CatalogInspector';
import { CatalogTrackingAction } from './CatalogTrackingAction';

export type CatalogPreview = Pick<
  ReturnType<typeof useInspectorSelection>,
  'selectedId' | 'select' | 'close' | 'onListKeyDown'
>;

export interface CatalogRowProps {
  title: CatalogTitle;
  selectionDisabled?: boolean;
  selectable?: boolean;
  selected?: boolean;
  onToggleSelected?: (id: string) => void;
  showPreview?: boolean;
  previewed?: boolean;
  systemName?: string;
}
export function CatalogAvailability({ title }: { title: CatalogTitle }) {
  const available = title.hasLocalPayload || Number(title.localPayloadVersionCount ?? 0) > 0;
  return (
    <Tooltip
      label={
        available
          ? 'Local files are present; this does not imply a complete release.'
          : 'No local payload is recorded for this title.'
      }
    >
      <Text
        size="xs"
        c={available ? 'dimmed' : 'yellow'}
      >
        {available ? 'Files available' : 'No local files'}
      </Text>
    </Tooltip>
  );
}
export function CatalogPreviewButton({
  name,
  active,
  review = false,
  onClick,
}: {
  name: string;
  active: boolean;
  review?: boolean;
  onClick?: MouseEventHandler<HTMLButtonElement>;
}) {
  return (
    <UnstyledButton
      className={classes.previewButton}
      aria-label={`${review ? 'Review' : 'Preview'} ${name}`}
      aria-pressed={active}
      aria-controls={active ? catalogInspectorId : undefined}
      data-review-trigger={review || undefined}
      data-inspector-trigger
      onClick={onClick}
    >
      {review && (
        <span
          className={classes.reviewTriggerLabel}
          aria-hidden="true"
        >
          Review <IconChevronRight size={16} />
        </span>
      )}
    </UnstyledButton>
  );
}

export const CatalogRow = memo(function CatalogRow({
  title,
  selectable = false,
  selected = false,
  selectionDisabled = false,
  onToggleSelected,
  showPreview = false,
  previewed = false,
  systemName = title.systemKey,
}: CatalogRowProps) {
  const poster = title.artwork?.find((item) => item.role === 'Poster');
  const availableRoles = artworkRoles.filter((role) =>
    title.artwork?.some((item) => item.role === role && item.url),
  );
  const coverage = artworkRoles
    .map(
      (role) =>
        `${artworkRoleLabel(role).toLowerCase()} ${availableRoles.includes(role) ? 'available' : 'missing'}`,
    )
    .join(', ');
  return (
    <tr
      data-selected={selected || undefined}
      aria-current={previewed || undefined}
      data-inspector-row={showPreview ? title.id : undefined}
    >
      {selectable && (
        <td className={classes.selection}>
          <Checkbox
            disabled={selectionDisabled}
            checked={selected}
            onChange={() => onToggleSelected?.(title.id)}
            aria-label={`Select ${title.name}`}
          />
        </td>
      )}
      <td className={classes.titleCell}>
        <div className={classes.titleContent}>
          <div className={classes.poster}>
            {poster?.url ? (
              <ArtworkFrame
                loading="lazy"
                artworkRole="Poster"
                src={poster.url}
                title={title.name}
                fit={poster.fit === 'Cover' ? 'Cover' : 'Contain'}
              />
            ) : (
              <div
                className={classes.emptyPoster}
                role="img"
                aria-label="No poster"
              >
                <IconPhotoOff size={16} />
              </div>
            )}
          </div>
          <div>
            <a
              className={`${classes.titleLink} ${classes.name}`}
              data-stretch={!showPreview || undefined}
              href={titleSectionPath(title.id)}
              data-catalog-title-link
            >
              {title.name}
            </a>
            <Text
              className={classes.mobileMeta}
              size="xs"
              c="dimmed"
            >
              {systemName}
              {title.releaseDate ? ` · ${title.releaseDate.slice(0, 4)}` : ''}
            </Text>
          </div>
        </div>
        {showPreview && (
          <CatalogPreviewButton
            name={title.name}
            active={previewed}
          />
        )}
      </td>
      <td className={classes.platform}>{systemName}</td>
      <td className={classes.year}>{title.releaseDate?.slice(0, 4) || '—'}</td>
      <td className={classes.availability}>
        <CatalogAvailability title={title} />
      </td>
      <td className={classes.status}>
        <CatalogEnrichmentStatus title={title} />
      </td>
      <td className={classes.artwork}>
        <Tooltip
          label={coverage}
          events={{
            hover: true,
            focus: true,
            touch: true,
          }}
        >
          <Group
            role="group"
            tabIndex={0}
            gap={5}
            aria-label={`Artwork: ${coverage}`}
            c={availableRoles.length === artworkRoles.length ? 'dimmed' : 'yellow'}
          >
            {availableRoles.length ? <IconPhoto size={16} /> : <IconPhotoOff size={16} />}
            <Text size="xs">
              {availableRoles.length}/{artworkRoles.length}
            </Text>
          </Group>
        </Tooltip>
      </td>
      {selectable && (
        <td className={classes.actions}>
          <CatalogTrackingAction title={title} />
        </td>
      )}
    </tr>
  );
});
