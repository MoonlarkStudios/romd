import { Checkbox, Group, Text } from '@mantine/core';
import { memo } from 'react';
import { ArtworkFrame } from '../../components/ArtworkFrame';
import { titleSectionPath } from '../../components/Title/titleSections';
import classes from './Catalog.module.css';
import { CatalogEnrichmentStatus } from './CatalogEnrichmentStatus';
import { CatalogAvailability, CatalogPreviewButton, type CatalogRowProps } from './CatalogRow';
import { CatalogTrackingAction } from './CatalogTrackingAction';

export const CatalogCard = memo(function CatalogCard({
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
  return (
    <div
      className={classes.card}
      data-selected={selected || undefined}
      aria-current={previewed || undefined}
      data-inspector-row={showPreview ? title.id : undefined}
    >
      <div className={classes.cardImage}>
        <ArtworkFrame
          artworkRole="Poster"
          loading="lazy"
          src={poster?.url}
          title={title.name}
          fit={poster?.fit === 'Cover' ? 'Cover' : 'Contain'}
        />
      </div>
      <a
        href={titleSectionPath(title.id)}
        data-catalog-title-link
        className={classes.titleLink}
        data-stretch={!showPreview || undefined}
      >
        <Text
          className={classes.cardCaption}
          size="sm"
          fw={550}
          lineClamp={2}
        >
          {title.name}
        </Text>
      </a>
      <Text
        size="xs"
        c="dimmed"
        truncate
      >
        {systemName}
        {title.releaseDate ? ` · ${title.releaseDate.slice(0, 4)}` : ''}
      </Text>
      <Group
        justify="space-between"
        align="start"
      >
        <CatalogAvailability title={title} />
        <CatalogEnrichmentStatus title={title} />
      </Group>
      {selectable && (
        <Group
          justify="space-between"
          mt="xs"
          className={classes.cardActions}
        >
          <Checkbox
            disabled={selectionDisabled}
            checked={selected}
            onChange={() => onToggleSelected?.(title.id)}
            aria-label={`Select ${title.name}`}
          />
          <CatalogTrackingAction title={title} />
        </Group>
      )}
      {showPreview && (
        <CatalogPreviewButton
          name={title.name}
          active={previewed}
        />
      )}
    </div>
  );
});
