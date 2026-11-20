import type {
  MetadataLayer,
  TitleEnrichmentStateResponse,
  UpdateTitleMetadataRequest,
} from '@romd/admin-api-client';
import type { TitleDetailView as TitleDetail } from '../../hooks/api/useTitleDetail';

type MetadataFieldKey =
  | 'name'
  | 'description'
  | 'genre'
  | 'releaseDate'
  | 'publisher'
  | 'developer'
  | 'players'
  | 'rating';

type MetadataFieldKind = 'text' | 'textarea' | 'date' | 'genre' | 'players' | 'rating';

export interface MetadataFieldDefinition {
  key: MetadataFieldKey;
  overrideKey?: string;
  label: string;
  kind: MetadataFieldKind;
}

export const METADATA_FIELDS: MetadataFieldDefinition[] = [
  {
    key: 'name',
    label: 'Title name',
    kind: 'text',
  },
  {
    key: 'genre',
    overrideKey: 'Genre',
    label: 'Genre',
    kind: 'genre',
  },
  {
    key: 'releaseDate',
    overrideKey: 'ReleaseDate',
    label: 'Release Date',
    kind: 'date',
  },
  {
    key: 'publisher',
    overrideKey: 'Publisher',
    label: 'Publisher',
    kind: 'text',
  },
  {
    key: 'developer',
    overrideKey: 'Developer',
    label: 'Developer',
    kind: 'text',
  },
  {
    key: 'players',
    overrideKey: 'Players',
    label: 'Players',
    kind: 'players',
  },
  {
    key: 'rating',
    overrideKey: 'Rating',
    label: 'Rating',
    kind: 'rating',
  },
  {
    key: 'description',
    overrideKey: 'Description',
    label: 'Description',
    kind: 'textarea',
  },
];

export interface Option {
  value: string;
  label: string;
}

export const BASE_GENRE_TAXONOMY: Option[] = [
  {
    value: 'Action',
    label: 'Action',
  },
  {
    value: 'Adventure',
    label: 'Adventure',
  },
  {
    value: 'Arcade',
    label: 'Arcade',
  },
  {
    value: 'Fighting',
    label: 'Fighting',
  },
  {
    value: 'Hack and slash/Beat em up',
    label: 'Hack and slash / Beat em up',
  },
  {
    value: 'Music',
    label: 'Music',
  },
  {
    value: 'Pinball',
    label: 'Pinball',
  },
  {
    value: 'Platform',
    label: 'Platform',
  },
  {
    value: 'Platformer',
    label: 'Platformer',
  },
  {
    value: 'Point-and-click',
    label: 'Point-and-click',
  },
  {
    value: 'Puzzle',
    label: 'Puzzle',
  },
  {
    value: 'Quiz/Trivia',
    label: 'Quiz / Trivia',
  },
  {
    value: 'Racing',
    label: 'Racing',
  },
  {
    value: 'Real Time Strategy (RTS)',
    label: 'Real Time Strategy (RTS)',
  },
  {
    value: 'Role-playing (RPG)',
    label: 'Role-playing (RPG)',
  },
  {
    value: 'RPG',
    label: 'RPG',
  },
  {
    value: 'Shooter',
    label: 'Shooter',
  },
  {
    value: 'Simulator',
    label: 'Simulator',
  },
  {
    value: 'Sport',
    label: 'Sport',
  },
  {
    value: 'Strategy',
    label: 'Strategy',
  },
  {
    value: 'Tactical',
    label: 'Tactical',
  },
  {
    value: 'Turn-based strategy (TBS)',
    label: 'Turn-based strategy (TBS)',
  },
  {
    value: 'Visual Novel',
    label: 'Visual Novel',
  },
];

function formatCount(value: number | null | undefined): string {
  return (value ?? 0).toLocaleString();
}

function addOption(options: Map<string, Option>, option: Option): void {
  const value = option.value.trim();
  if (!value) return;

  options.set(value, {
    value,
    label: option.label,
  });
}

function addMissingOption(options: Map<string, Option>, value: string | null): void {
  const trimmed = value?.trim();
  if (!trimmed || options.has(trimmed)) return;

  options.set(trimmed, {
    value: trimmed,
    label: trimmed,
  });
}

export function buildTaxonomyOptions(
  baseOptions: Option[],
  facets:
    | {
        value: string;
        count: number;
      }[]
    | undefined,
  activeValues: (string | null)[],
): Option[] {
  const options = new Map<string, Option>();

  for (const option of baseOptions) {
    addOption(options, option);
  }

  for (const facet of facets ?? []) {
    addOption(options, {
      value: facet.value,
      label: `${facet.value} (${formatCount(facet.count)})`,
    });
  }

  for (const value of activeValues) {
    addMissingOption(options, value);
  }

  return Array.from(options.values());
}

export type FieldEdit =
  | {
      kind: 'edited';
      value: string;
    }
  | {
      kind: 'automatic';
    }
  | {
      kind: 'source';
      sourceId: string;
    };

export type FieldEdits = Partial<Record<MetadataFieldKey, FieldEdit>>;

type MetadataWrite =
  | {
      kind: 'metadata';
      body: UpdateTitleMetadataRequest;
    }
  | {
      kind: 'sources';
      body: Record<string, string>;
    };

export interface Draft {
  edits: FieldEdits;
  revision: string;
  remaining?: MetadataWrite[];
  applied?: number;
}

export const emptyLayers: MetadataLayer[] = [];

export function displayValue(value: string | number | null | undefined): string {
  return String(value ?? '');
}

export function layerValue(
  layer: MetadataLayer | undefined,
  field: MetadataFieldDefinition,
): string {
  return field.key === 'name' ? '' : displayValue(layer?.[field.key]);
}

export function sameSource(left: string | null | undefined, right: string): boolean {
  return left?.toLowerCase() === right.toLowerCase();
}

export function findLayer(layers: MetadataLayer[], sourceId: string) {
  return layers.find((layer) => sameSource(layer.sourceId, sourceId));
}

export function sourceLabel(sourceId: string): string {
  if (sameSource(sourceId, 'igdb')) return 'IGDB';
  if (sameSource(sourceId, 'steamgriddb')) return 'SteamGridDB';
  return sourceId;
}

export function fieldSource(
  values: Record<string, string> | null | undefined,
  field: MetadataFieldDefinition,
): string | undefined {
  return values?.[field.overrideKey ?? field.key] ?? values?.[field.key];
}

export function sourceOptions(layers: MetadataLayer[], field: MetadataFieldDefinition) {
  return layers.filter((layer) => !sameSource(layer.sourceId, 'user') && layerValue(layer, field));
}

function titleFieldPatch(
  field: MetadataFieldDefinition,
  value: string | null,
): UpdateTitleMetadataRequest {
  if (field.kind === 'rating' || field.kind === 'players') {
    return {
      [field.key]: value === null ? null : Number(value),
    };
  }
  return {
    [field.key]: value,
  };
}

export function buildWrites(
  edits: FieldEdits,
  state?: TitleEnrichmentStateResponse,
): MetadataWrite[] {
  const clearBeforeEdit: Record<string, string> = {};
  const metadata: UpdateTitleMetadataRequest = {};
  const setSources: Record<string, string> = {};
  const user = findLayer(state?.layers ?? emptyLayers, 'user');
  for (const field of METADATA_FIELDS) {
    const edit = edits[field.key];
    if (!edit) continue;
    const current = fieldSource(state?.fieldSourceOverrides, field);
    if (edit.kind === 'edited') {
      if (field.overrideKey && current && !sameSource(current, 'user')) {
        clearBeforeEdit[field.overrideKey] = '';
      }
      Object.assign(metadata, titleFieldPatch(field, edit.value.trim()));
    } else if (field.overrideKey && edit.kind === 'automatic') {
      if (layerValue(user, field)) Object.assign(metadata, titleFieldPatch(field, null));
      if (current) setSources[field.overrideKey] = '';
    } else if (field.overrideKey && edit.kind === 'source') {
      setSources[field.overrideKey] = edit.sourceId;
    }
  }

  const writes: MetadataWrite[] = [];
  if (Object.keys(clearBeforeEdit).length > 0)
    writes.push({
      kind: 'sources',
      body: clearBeforeEdit,
    });
  if (Object.keys(metadata).length > 0)
    writes.push({
      kind: 'metadata',
      body: metadata,
    });
  if (Object.keys(setSources).length > 0)
    writes.push({
      kind: 'sources',
      body: setSources,
    });
  return writes;
}

export function previewValue(
  title: TitleDetail,
  field: MetadataFieldDefinition,
  edit: FieldEdit | undefined,
  layers: MetadataLayer[],
  isPinned: boolean,
  provenance: string | undefined,
): string {
  if (edit?.kind === 'edited') return edit.value;
  if (edit?.kind === 'source') return layerValue(findLayer(layers, edit.sourceId), field);
  // The current effective value only previews automatic resolution when no manual choice masks it.
  // System defaults and global priorities are resolved on the server.
  if (edit?.kind === 'automatic' && (isPinned || sameSource(provenance, 'user'))) return '';
  return displayValue(title[field.key]);
}

export function markerText(
  field: MetadataFieldDefinition,
  edit: FieldEdit | undefined,
  provenance: string | undefined,
  override: string | undefined,
): string {
  if (edit?.kind === 'edited') return 'Edited';
  if (edit?.kind === 'automatic') return 'Automatic';
  if (edit?.kind === 'source') return `${sourceLabel(edit.sourceId)} · Locked`;
  if (sameSource(provenance, 'user')) return 'Edited';
  if (override && !sameSource(override, 'user')) return `${sourceLabel(override)} · Locked`;
  return provenance ? sourceLabel(provenance) : field.overrideKey ? 'Automatic' : 'Title record';
}
