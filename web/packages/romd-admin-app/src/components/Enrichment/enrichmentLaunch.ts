import type { ArtworkCompletionFilterDto, ArtworkCompletionRequest } from '@romd/admin-api-client';

export type EnrichmentLaunchScope = 'Selected' | 'Filtered' | 'Automatic' | 'AllTracked';
export type EnrichmentLaunchMode = 'Enrich' | 'Artwork';

export interface LaunchScopeOption {
  value: EnrichmentLaunchScope;
  label: string;
}

interface LaunchContext {
  mode: EnrichmentLaunchMode;
  selectedCount: number;
  hasFilters: boolean;
}

/** Only the scopes this entry point and mode can launch, from the narrowest to every tracked title. */
export function launchScopes({
  mode,
  selectedCount,
  hasFilters,
}: LaunchContext): LaunchScopeOption[] {
  const options: LaunchScopeOption[] = [];
  if (selectedCount > 0)
    options.push({
      value: 'Selected',
      label: `Selected titles (${selectedCount.toLocaleString()})`,
    });
  if (hasFilters)
    options.push({
      value: 'Filtered',
      label: 'Titles matching the current filters',
    });
  if (mode === 'Enrich') {
    options.push({
      value: 'Automatic',
      label: hasFilters
        ? 'Titles matching the filters that can be enriched automatically'
        : 'Titles that can be enriched automatically',
    });
  }
  options.push({
    value: 'AllTracked',
    label: 'All tracked titles',
  });
  return options;
}

/** A requested scope the options no longer offer falls back to the current filters, then every tracked title. */
export function effectiveScope(
  requested: EnrichmentLaunchScope,
  options: LaunchScopeOption[],
): EnrichmentLaunchScope {
  const offered = (scope: EnrichmentLaunchScope) =>
    options.some((option) => option.value === scope);
  if (offered(requested)) return requested;
  return offered('Filtered') ? 'Filtered' : 'AllTracked';
}

/** The API rejects an empty or blank enrichment list, so no flags means no key. */
function launchFilters({
  enrichment,
  ...filters
}: ArtworkCompletionFilterDto): ArtworkCompletionFilterDto {
  const flags = enrichment?.filter((flag) => flag.trim()) ?? [];
  return flags.length
    ? {
        ...filters,
        enrichment: flags,
      }
    : filters;
}

interface LaunchRequestInput {
  requestId: string;
  scope: EnrichmentLaunchScope;
  mode: EnrichmentLaunchMode;
  titleIds: string[];
  filters?: ArtworkCompletionFilterDto;
}

/** Each scope carries only its own inputs: ids for Selected, filters for Filtered and Automatic. */
export function launchRequest({
  requestId,
  scope,
  mode,
  titleIds,
  filters,
}: LaunchRequestInput): ArtworkCompletionRequest {
  return {
    requestId,
    scope,
    mode,
    ...(scope === 'Selected'
      ? {
          titleIds,
        }
      : {}),
    ...((scope === 'Filtered' || scope === 'Automatic') && filters
      ? {
          filters: launchFilters(filters),
        }
      : {}),
  };
}
