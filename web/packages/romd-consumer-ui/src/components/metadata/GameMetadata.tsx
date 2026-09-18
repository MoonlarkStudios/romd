import { Tooltip } from '@mantine/core';
import { IconStar, IconUser, IconUsers } from '@tabler/icons-react';
import { useState } from 'react';
import type { ReferenceIcon } from './ReferenceCatalog';

export interface RatingPresentation {
  board: string;
  code: string;
  boardName?: string;
  name?: string;
  description?: string | null;
  icon?: ReferenceIcon | null;
}

export interface GameMetadataProps {
  system: { key: string; name: string; compactLabel: string; icon?: ReferenceIcon | null };
  releaseDate?: string | null;
  genre?: string | null;
  players?: number | null;
  rating?: number | null;
  contentRatings?: readonly RatingPresentation[];
}

export function RatingMark({ board, code, boardName = board, name, description, icon, size = 38 }: RatingPresentation & { size?: number }) {
  const fallback = code.toUpperCase().startsWith(boardName.toUpperCase()) ? code : `${boardName} ${code}`;
  const [failed, setFailed] = useState<string | null>(null);
  const label = description ?? name ?? fallback;
  return icon && failed !== icon.url ? <img className="romd-rating-mark" src={icon.url} alt={label} title={label} height={size} onError={() => setFailed(icon.url)} />
    : <span className="romd-rating-fallback">{name ?? fallback}</span>;
}

export function PlatformMark({ system }: Pick<GameMetadataProps, 'system'>) {
  const { name, icon } = system;
  const [failed, setFailed] = useState<string | null>(null);
  if (!icon || failed === icon.url) return <span>{name}</span>;
  return <Tooltip label={name} events={{ hover: true, focus: true, touch: true }} multiline w={240} withArrow>
    <button type="button" className="romd-platform-mark" aria-label={name}>
      <img src={icon.url} alt="" data-tintable={icon.monochrome || undefined} onError={() => setFailed(icon.url)} />
    </button>
  </Tooltip>;
}

/** Data-only props keep this usable in consumer pages and admin previews. */
export function GameMetadata({ system, releaseDate, genre, players, rating, contentRatings = [] }: GameMetadataProps) {
  const score = rating != null && Number.isFinite(rating) && rating >= 0 && rating <= 100 ? Math.round(rating) : null;
  const count = players != null && Number.isSafeInteger(players) && players > 0 ? players : null;
  const year = releaseDate?.match(/^\d{4}(?:-|$)/)?.[0].slice(0, 4);
  // API order is authoritative; never infer a classification from another board.
  const classification = contentRatings[0];
  return <div className="romd-game-metadata">
    <ul className="romd-game-metadata-badges" aria-label="Game metadata">
      {classification && <li><RatingMark {...classification} /></li>}
      {system.name && <li><span className="romd-platform-badge" title={system.name} aria-label={system.name}>{system.compactLabel}</span></li>}
      {count !== null && <li className="romd-game-metadata-players">{count === 1 ? <IconUser size={18} aria-hidden="true" /> : <IconUsers size={18} aria-hidden="true" />}<span>{count === 1 ? '1 player' : `${count} players`}</span></li>}
      {score !== null && <li className="romd-game-metadata-score" aria-label={`Rating ${score} out of 100`}><IconStar size={18} aria-hidden="true" /><span><strong>{score}</strong><span className="romd-game-metadata-scale">/100</span></span></li>}
    </ul>
    {(year || genre) && <ul className="romd-game-metadata-facts" aria-label="Release and genres">{year && <li>{year}</li>}{genre && <li>{genre}</li>}</ul>}
  </div>;
}
