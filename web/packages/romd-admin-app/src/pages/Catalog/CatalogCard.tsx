import { Checkbox, Group, Text } from '@mantine/core';
import { Link, useLocation } from 'react-router';
import { ArtworkFrame } from '../../components/ArtworkFrame';
import { usePlatforms } from '../../hooks/api/usePlatforms';
import classes from './Catalog.module.css';
import { CatalogEnrichmentStatus } from './CatalogEnrichmentStatus';
import { CatalogAvailability, type CatalogRowProps } from './CatalogRow';
import { CatalogTrackingAction } from './CatalogTrackingAction';

export function CatalogCard({
  title,
  selectable = false,
  selected = false,
  selectionDisabled = false,
  onToggleSelected,
}: CatalogRowProps) {
  const location = useLocation();
  const { data: platforms } = usePlatforms();
  const platform = platforms?.find((item) => item.key === title.systemKey);
  const poster = title.artwork?.find((item) => item.role === 'Poster');
  return (
    <div
      className={classes.card}
      data-selected={selected || undefined}
    >
      <Link
        to={`/titles/${title.id}`}
        state={{
          catalogReturnTo: `${location.pathname}${location.search}`,
        }}
        className={classes.cardImage}
        aria-label={title.name}
      >
        <ArtworkFrame
          artworkRole="Poster"
          loading="lazy"
          src={poster?.url}
          title={title.name}
          fit={poster?.fit === 'Cover' ? 'Cover' : 'Contain'}
        />
      </Link>
      <Link
        to={`/titles/${title.id}`}
        state={{
          catalogReturnTo: `${location.pathname}${location.search}`,
        }}
        className={classes.titleLink}
      >
        <Text
          className={classes.cardCaption}
          size="sm"
          fw={550}
          lineClamp={2}
        >
          {title.name}
        </Text>
      </Link>
      <Text
        size="xs"
        c="dimmed"
        truncate
      >
        {platform?.compactLabel || platform?.name || title.systemKey}
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
    </div>
  );
}
