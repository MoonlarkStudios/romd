import { Checkbox, Group, Text, Tooltip } from '@mantine/core';
import type { CatalogTitle } from '@romd/admin-api-client';
import { IconPhoto, IconPhotoOff } from '@tabler/icons-react';
import { Link } from 'react-router';
import { ArtworkFrame } from '../../components/ArtworkFrame';
import { artworkRoleLabel, artworkRoles } from '../../components/artworkPresentation';
import { usePlatforms } from '../../hooks/api/usePlatforms';
import classes from './Catalog.module.css';
import { CatalogTrackingAction } from './CatalogTrackingAction';

export interface CatalogRowProps {
  title: CatalogTitle; selectable?: boolean; selected?: boolean; onToggleSelected?: (id: string) => void;
}
export function CatalogAvailability({ title }: { title: CatalogTitle }) {
  const available = title.hasLocalPayload || Number(title.localPayloadVersionCount ?? 0) > 0;
  return <Tooltip label={available ? 'Local files are present; this does not imply a complete release.' : 'No local payload is recorded for this title.'}>
    <Text size="xs" c={available ? 'teal' : 'dimmed'}>{available ? 'Files available' : 'No local files'}</Text>
  </Tooltip>;
}
const metadataLabels: Record<string, string> = { None: 'Not enriched', Completed: 'Enriched', Failed: 'Failed', Pending: 'Pending', NotFound: 'No match' };
export function CatalogRow({ title, selectable = false, selected = false, onToggleSelected }: CatalogRowProps) {
  const { data: platforms } = usePlatforms();
  const platform = platforms?.find((item) => item.key === title.systemKey);
  const poster = title.artwork?.find((item) => item.role === 'Poster');
  const availableRoles = artworkRoles.filter(role => title.artwork?.some(item => item.role === role && item.url));
  const coverage = artworkRoles.map(role => `${artworkRoleLabel(role).toLowerCase()} ${availableRoles.includes(role) ? 'available' : 'missing'}`).join(', ');
  const status = title.enrichmentStatus ?? 'None';
  return <tr data-selected={selected || undefined}>
    {selectable && <td className={classes.selection}><Checkbox checked={selected} onChange={() => onToggleSelected?.(title.id)} aria-label={`Select ${title.name}`} /></td>}
    <td className={classes.titleCell}><Link className={classes.titleLink} to={`/titles/${title.id}`}>
      <div className={classes.poster}>{poster?.url ? <ArtworkFrame loading="lazy" role="Poster" src={poster.url} title={title.name} fit={poster.fit === 'Cover' ? 'Cover' : 'Contain'} /> : <div className={classes.emptyPoster} aria-label="No poster"><IconPhotoOff size={16} /></div>}</div>
      <div><div className={classes.name}>{title.name}</div><Text className={classes.mobileMeta} size="xs" c="dimmed">{platform?.compactLabel || platform?.name || title.systemKey}{title.releaseDate ? ` · ${title.releaseDate.slice(0, 4)}` : ''}</Text></div>
    </Link></td>
    <td className={classes.platform}>{platform?.compactLabel || platform?.name || title.systemKey}</td>
    <td className={classes.year}>{title.releaseDate?.slice(0, 4) || '—'}</td>
    <td className={classes.availability}><CatalogAvailability title={title} /></td>
    <td className={classes.status}><Text size="xs" c={status === 'Failed' ? 'red' : status === 'Completed' ? 'teal' : 'dimmed'}>{metadataLabels[status] ?? status}</Text></td>
    <td className={classes.artwork}><Tooltip label={coverage}><Group gap={5} aria-label={`Artwork: ${coverage}`} c={availableRoles.length === artworkRoles.length ? 'teal' : 'dimmed'}>{availableRoles.length ? <IconPhoto size={16} /> : <IconPhotoOff size={16} />}<Text size="xs">{availableRoles.length}/{artworkRoles.length}</Text></Group></Tooltip></td>
    {selectable && <td className={classes.actions}><CatalogTrackingAction title={title} /></td>}
  </tr>;
}
