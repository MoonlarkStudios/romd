import {
  getAdminCatalogSnapshot,
  listAdminCatalogSnapshotCompanies,
  listAdminCatalogSnapshotLanguages,
  listAdminCatalogSnapshotRatingBoards,
  listAdminCatalogSnapshotRatings,
  listAdminCatalogSnapshotRegions,
  listAdminCatalogSnapshotSystems,
  type ReferenceNamedDto,
  type ReferenceRatingBoardDto,
  type ReferenceRatingDto,
  type ReferenceSystemDto,
  type ReferenceTaxonomyDto,
} from '@romd/admin-api-client';
import { referenceContentHash } from './referenceCatalogCanonical';

/** Existing complete offline v1 representation, assembled privately from one publication. */
export interface AssembledReferenceCatalog {
  schemaVersion: 1;
  builtInVersion: number;
  revision: string;
  systems: ReferenceSystemDto[];
  companies: ReferenceNamedDto[];
  regions: ReferenceTaxonomyDto[];
  languages: ReferenceTaxonomyDto[];
  ratingBoards: ReferenceRatingBoardDto[];
  ratings: ReferenceRatingDto[];
}

const families = [
  'systems',
  'companies',
  'regions',
  'languages',
  'ratingBoards',
  'ratings',
] as const;
type Family = (typeof families)[number];
interface Manifest {
  schemaVersion: 1;
  builtInVersion: number;
  revision: string;
  catalogRevision: string;
  contentHash: string;
  counts: Record<Family, number>;
}
interface SnapshotResponse {
  data?: unknown;
  error?: unknown;
  response?: Response;
}
const digestPattern = /^[a-f0-9]{64}$/;
const record = (value: unknown): value is Record<string, unknown> =>
  value !== null && typeof value === 'object' && !Array.isArray(value);
const text = (value: unknown): value is string =>
  typeof value === 'string' && value.trim().length > 0;
const nullableText = (value: unknown): value is string | null =>
  value === null || typeof value === 'string';
const integer = (value: unknown): value is number =>
  Number.isSafeInteger(value) && Number(value) >= 0;
const fields = (value: Record<string, unknown>, expected: readonly string[]) =>
  Object.keys(value).length === expected.length &&
  expected.every((key) => Object.hasOwn(value, key));
const named = (value: unknown): value is ReferenceNamedDto =>
  record(value) &&
  fields(value, [
    'key',
    'name',
    'description',
    'retired',
  ]) &&
  text(value.key) &&
  text(value.name) &&
  nullableText(value.description) &&
  typeof value.retired === 'boolean';
const board = (value: unknown): value is ReferenceRatingBoardDto => named(value);
const asset = (value: unknown) =>
  value === null ||
  (record(value) &&
    fields(value, [
      'url',
      'sha256',
      'contentType',
      'monochrome',
    ]) &&
    text(value.url) &&
    typeof value.sha256 === 'string' &&
    digestPattern.test(value.sha256) &&
    text(value.contentType) &&
    typeof value.monochrome === 'boolean');
const stringList = (value: unknown): value is string[] => Array.isArray(value) && value.every(text);
const unique = (values: readonly string[]) => new Set(values).size === values.length;
function system(value: unknown): value is ReferenceSystemDto {
  if (
    !record(value) ||
    !fields(value, [
      'key',
      'name',
      'compactLabel',
      'description',
      'icon',
      'manufacturerKeys',
      'retired',
      'manufacturers',
    ]) ||
    !text(value.key) ||
    !text(value.name) ||
    !text(value.compactLabel) ||
    !nullableText(value.description) ||
    !asset(value.icon) ||
    typeof value.retired !== 'boolean'
  )
    return false;
  if (
    value.manufacturerKeys !== null &&
    (!stringList(value.manufacturerKeys) ||
      value.manufacturerKeys.length > 16 ||
      !unique(value.manufacturerKeys))
  )
    return false;
  if (
    value.manufacturers !== null &&
    (!Array.isArray(value.manufacturers) ||
      value.manufacturers.length > 16 ||
      !value.manufacturers.every(
        (item) =>
          record(item) &&
          fields(item, [
            'key',
            'name',
          ]) &&
          text(item.key) &&
          text(item.name),
      ))
  )
    return false;
  return true;
}
const taxonomy = (value: unknown): value is ReferenceTaxonomyDto =>
  record(value) &&
  fields(value, [
    'key',
    'name',
    'description',
    'sortOrder',
    'languageCode',
    'retired',
  ]) &&
  text(value.key) &&
  text(value.name) &&
  nullableText(value.description) &&
  Number.isSafeInteger(value.sortOrder) &&
  nullableText(value.languageCode) &&
  typeof value.retired === 'boolean';
const rating = (value: unknown): value is ReferenceRatingDto =>
  record(value) &&
  fields(value, [
    'board',
    'code',
    'name',
    'description',
    'icon',
    'designation',
    'minimumAge',
    'retired',
  ]) &&
  text(value.board) &&
  text(value.code) &&
  text(value.name) &&
  nullableText(value.description) &&
  asset(value.icon) &&
  nullableText(value.designation) &&
  (value.minimumAge === null || integer(value.minimumAge)) &&
  typeof value.retired === 'boolean';

function manifest(value: unknown, etag: string | null | undefined): Manifest {
  if (
    !record(value) ||
    !fields(value, [
      'schemaVersion',
      'builtInVersion',
      'revision',
      'catalogRevision',
      'contentHash',
      'counts',
    ]) ||
    value.schemaVersion !== 1 ||
    !integer(value.builtInVersion) ||
    typeof value.revision !== 'string' ||
    !digestPattern.test(value.revision) ||
    typeof value.catalogRevision !== 'string' ||
    !digestPattern.test(value.catalogRevision) ||
    typeof value.contentHash !== 'string' ||
    !digestPattern.test(value.contentHash) ||
    !record(value.counts) ||
    !fields(value.counts, families) ||
    !families.every((key) =>
      integer(value.counts && record(value.counts) ? value.counts[key] : undefined),
    ) ||
    etag !== `"${value.revision}"`
  )
    throw new Error('Invalid reference catalog descriptor.');
  const count = (key: Family): number => {
    const entry = record(value.counts) ? value.counts[key] : undefined;
    if (!integer(entry)) throw new Error('Invalid reference catalog count.');
    return entry;
  };
  return {
    schemaVersion: 1,
    builtInVersion: value.builtInVersion,
    revision: value.revision,
    catalogRevision: value.catalogRevision,
    contentHash: value.contentHash,
    counts: {
      systems: count('systems'),
      companies: count('companies'),
      regions: count('regions'),
      languages: count('languages'),
      ratingBoards: count('ratingBoards'),
      ratings: count('ratings'),
    },
  };
}
class SnapshotChanged extends Error {}
function responseData(result: SnapshotResponse): unknown {
  if (
    result.response?.status === 409 &&
    record(result.error) &&
    result.error.errorCode === 'ReferenceCatalog.SnapshotChanged'
  )
    throw new SnapshotChanged('The reference catalog changed while loading.');
  if (result.error || result.data === undefined || result.response?.status !== 200)
    throw new Error('The reference catalog could not be loaded.');
  return result.data;
}
async function family<T>(
  load: (cursor: string | undefined) => Promise<SnapshotResponse>,
  valid: (value: unknown) => value is T,
  identity: (value: T) => string,
  count: number,
  signal: AbortSignal,
): Promise<T[]> {
  const items: T[] = [];
  const keys = new Set<string>();
  const cursors = new Set<string>();
  let cursor: string | undefined;
  for (;;) {
    signal.throwIfAborted();
    const result = await load(cursor);
    signal.throwIfAborted();
    const page = responseData(result);
    if (
      !record(page) ||
      !fields(page, [
        'items',
        'nextCursor',
        'hasNextPage',
      ]) ||
      !Array.isArray(page.items) ||
      page.items.length > 100 ||
      !page.items.every(valid) ||
      typeof page.hasNextPage !== 'boolean' ||
      (page.nextCursor !== null && !text(page.nextCursor)) ||
      page.hasNextPage !== (typeof page.nextCursor === 'string') ||
      (page.hasNextPage && page.items.length === 0) ||
      items.length + page.items.length > count
    )
      throw new Error('Invalid reference catalog page.');
    for (const item of page.items) {
      const key = identity(item);
      if (keys.has(key)) throw new Error('Duplicate reference catalog identity.');
      keys.add(key);
      // Preserve the publisher's objects and array order, including explicit null/default fields.
      items.push(item);
    }
    if (!page.hasNextPage) {
      if (items.length !== count) throw new Error('Reference catalog count mismatch.');
      return items;
    }
    if (typeof page.nextCursor !== 'string' || cursors.has(page.nextCursor))
      throw new Error('The reference catalog repeated a page cursor.');
    cursor = page.nextCursor;
    cursors.add(cursor);
  }
}
function validateRelationships(catalog: AssembledReferenceCatalog) {
  const boards = new Set(catalog.ratingBoards.map((item) => item.key));
  const companies = new Map(
    catalog.companies.map((item) => [
      item.key,
      item.name,
    ]),
  );
  if (catalog.ratings.some((item) => !boards.has(item.board)))
    throw new Error('Unknown reference rating board.');
  for (const item of catalog.systems) {
    const keys = item.manufacturerKeys ?? [];
    const summaries = item.manufacturers ?? [];
    if (
      !unique(summaries.map((company) => company.key)) ||
      keys.length !== summaries.length ||
      keys.some(
        (key, index) =>
          summaries[index]?.key !== key || companies.get(key) !== summaries[index]?.name,
      )
    )
      throw new Error('Invalid reference manufacturers.');
  }
}

/** A conflict discards every family and restarts once; all other failures retain query cache data. */
export async function loadAdminReferenceCatalog(
  signal: AbortSignal,
): Promise<AssembledReferenceCatalog> {
  for (let attempt = 0; attempt < 2; attempt += 1) {
    try {
      signal.throwIfAborted();
      const root = await getAdminCatalogSnapshot({
        signal,
      });
      signal.throwIfAborted();
      const descriptor = manifest(responseData(root), root.response?.headers.get('ETag'));
      const query = {
        revision: descriptor.catalogRevision,
        limit: 100,
      };
      const key = (item: { key: string }) => item.key;
      const systems = await family(
        (cursor) =>
          listAdminCatalogSnapshotSystems({
            signal,
            query: {
              ...query,
              cursor,
            },
          }),
        system,
        key,
        descriptor.counts.systems,
        signal,
      );
      const companies = await family(
        (cursor) =>
          listAdminCatalogSnapshotCompanies({
            signal,
            query: {
              ...query,
              cursor,
            },
          }),
        named,
        key,
        descriptor.counts.companies,
        signal,
      );
      const regions = await family(
        (cursor) =>
          listAdminCatalogSnapshotRegions({
            signal,
            query: {
              ...query,
              cursor,
            },
          }),
        taxonomy,
        key,
        descriptor.counts.regions,
        signal,
      );
      const languages = await family(
        (cursor) =>
          listAdminCatalogSnapshotLanguages({
            signal,
            query: {
              ...query,
              cursor,
            },
          }),
        taxonomy,
        key,
        descriptor.counts.languages,
        signal,
      );
      const ratingBoards = await family(
        (cursor) =>
          listAdminCatalogSnapshotRatingBoards({
            signal,
            query: {
              ...query,
              cursor,
            },
          }),
        board,
        key,
        descriptor.counts.ratingBoards,
        signal,
      );
      const ratings = await family(
        (cursor) =>
          listAdminCatalogSnapshotRatings({
            signal,
            query: {
              ...query,
              cursor,
            },
          }),
        rating,
        (item) =>
          JSON.stringify([
            item.board,
            item.code,
          ]),
        descriptor.counts.ratings,
        signal,
      );
      const catalog: AssembledReferenceCatalog = {
        schemaVersion: 1,
        builtInVersion: descriptor.builtInVersion,
        revision: descriptor.catalogRevision,
        systems,
        companies,
        regions,
        languages,
        ratingBoards,
        ratings,
      };
      validateRelationships(catalog);
      if ((await referenceContentHash(catalog)) !== descriptor.contentHash)
        throw new Error('Reference catalog content hash mismatch.');
      signal.throwIfAborted();
      return catalog;
    } catch (error) {
      signal.throwIfAborted();
      if (!(error instanceof SnapshotChanged) || attempt === 1) throw error;
    }
  }
  throw new Error('The reference catalog changed repeatedly.');
}
