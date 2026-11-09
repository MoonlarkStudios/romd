import { Stack, Tabs, Text, Title } from '@mantine/core';
import type { Language, Region } from '@romd/admin-api-client';
import { IconLanguage, IconWorld } from '@tabler/icons-react';
import { useMemo, useState } from 'react';
import { useSearchParams } from 'react-router';
import classes from '../../components/Workspace/Workspace.module.css';
import {
  fetchLanguageAliasPage,
  fetchRegionAliasPage,
  previewLanguageMergeImpact,
  previewRegionMergeImpact,
  taxonomyKeys,
  useAddLanguageAlias,
  useAddRegionAlias,
  useLanguages,
  useMergeLanguages,
  useMergeRegions,
  useRegions,
  useRemoveLanguageAlias,
  useRemoveRegionAlias,
} from '../../hooks/api/useTaxonomy';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { usePermissions } from '../../hooks/usePermissions';
import { type TaxonomyEntry, TaxonomyPanel } from './TaxonomyPanel';

const SEARCH_DEBOUNCE_MS = 300;

const regionEntry = (r: Region): TaxonomyEntry => ({
  key: r.key,
  name: r.name,
  secondary: null,
  isAutoCreated: r.isAutoCreated,
  canMerge: r.canMerge,
  sortOrder: r.sortOrder,
  aliases: r.aliases,
});

const languageEntry = (l: Language): TaxonomyEntry => ({
  key: l.key,
  name: l.name,
  secondary: l.code,
  isAutoCreated: l.isAutoCreated,
  canMerge: l.canMerge,
  sortOrder: l.sortOrder,
  aliases: l.aliases,
});

export function Taxonomy() {
  const { canEditReferenceData: canEdit } = usePermissions();
  const [params, setParams] = useSearchParams();
  const tab =
    [
      'regions',
      'languages',
    ].find((value) => value === params.get('tab')) ?? 'regions';

  const [regionSearch, setRegionSearch] = useState('');
  const [languageSearch, setLanguageSearch] = useState('');
  const regionQuery = useDebouncedValue(regionSearch.trim(), SEARCH_DEBOUNCE_MS);
  const languageQuery = useDebouncedValue(languageSearch.trim(), SEARCH_DEBOUNCE_MS);

  // The complete lists are the merge targets; the matches are what the search shows.
  const regions = useRegions();
  const regionMatches = useRegions(regionQuery);
  const languages = useLanguages();
  const languageMatches = useLanguages(languageQuery);

  const addRegionAlias = useAddRegionAlias();
  const removeRegionAlias = useRemoveRegionAlias();
  const mergeRegions = useMergeRegions();
  const addLanguageAlias = useAddLanguageAlias();
  const removeLanguageAlias = useRemoveLanguageAlias();
  const mergeLanguages = useMergeLanguages();

  const regionEntries = useMemo(
    () => (regionMatches.data ?? []).map(regionEntry),
    [
      regionMatches.data,
    ],
  );
  const regionCandidates = useMemo(
    () => (regions.data ?? []).map(regionEntry),
    [
      regions.data,
    ],
  );

  const languageEntries = useMemo(
    () => (languageMatches.data ?? []).map(languageEntry),
    [
      languageMatches.data,
    ],
  );
  const languageCandidates = useMemo(
    () => (languages.data ?? []).map(languageEntry),
    [
      languages.data,
    ],
  );

  const regionsBusy =
    addRegionAlias.isPending || removeRegionAlias.isPending || mergeRegions.isPending;
  const languagesBusy =
    addLanguageAlias.isPending || removeLanguageAlias.isPending || mergeLanguages.isPending;

  return (
    <Stack
      className={classes.page}
      gap="lg"
    >
      <div className={classes.hero}>
        <Title
          order={1}
          className={classes.heading}
        >
          Reference data
        </Title>
        <Text
          size="sm"
          c="dimmed"
        >
          Manage regions, languages, and local aliases. Built-in definitions ship with ROMD.
          {!canEdit && ' Editing requires an administrator.'}
        </Text>
      </div>

      <Tabs
        value={tab}
        onChange={(value) =>
          setParams({
            tab: value ?? 'regions',
          })
        }
        classNames={{
          root: classes.tabs,
          list: classes.tabsList,
          tab: classes.tab,
        }}
        keepMounted={false}
      >
        <Tabs.List mb="md">
          <Tabs.Tab
            value="regions"
            leftSection={<IconWorld size={16} />}
          >
            Regions
          </Tabs.Tab>
          <Tabs.Tab
            value="languages"
            leftSection={<IconLanguage size={16} />}
          >
            Languages
          </Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="regions">
          <TaxonomyPanel
            noun="region"
            entries={regionEntries}
            candidates={regionCandidates}
            search={regionSearch}
            onSearchChange={setRegionSearch}
            isLoading={regionMatches.isLoading}
            isError={regionMatches.isError}
            errorMessage={regionMatches.error?.message}
            onRetry={() => void regionMatches.refetch()}
            canEdit={canEdit}
            busy={regionsBusy}
            aliasesKey={taxonomyKeys.regionAliases}
            onLoadAliases={fetchRegionAliasPage}
            onAddAlias={(regionKey, alias) =>
              addRegionAlias.mutateAsync({
                regionKey,
                alias,
              })
            }
            onRemoveAlias={(regionKey, aliasId) =>
              removeRegionAlias.mutateAsync({
                regionKey,
                aliasId,
              })
            }
            onMerge={(sourceKey, targetKey) =>
              mergeRegions.mutateAsync({
                sourceKey,
                targetKey,
              })
            }
            onPreviewMerge={previewRegionMergeImpact}
          />
        </Tabs.Panel>

        <Tabs.Panel value="languages">
          <TaxonomyPanel
            noun="language"
            entries={languageEntries}
            candidates={languageCandidates}
            search={languageSearch}
            onSearchChange={setLanguageSearch}
            isLoading={languageMatches.isLoading}
            isError={languageMatches.isError}
            errorMessage={languageMatches.error?.message}
            onRetry={() => void languageMatches.refetch()}
            canEdit={canEdit}
            busy={languagesBusy}
            aliasesKey={taxonomyKeys.languageAliases}
            onLoadAliases={fetchLanguageAliasPage}
            onAddAlias={(languageKey, alias) =>
              addLanguageAlias.mutateAsync({
                languageKey,
                alias,
              })
            }
            onRemoveAlias={(languageKey, aliasId) =>
              removeLanguageAlias.mutateAsync({
                languageKey,
                aliasId,
              })
            }
            onMerge={(sourceKey, targetKey) =>
              mergeLanguages.mutateAsync({
                sourceKey,
                targetKey,
              })
            }
            onPreviewMerge={previewLanguageMergeImpact}
          />
        </Tabs.Panel>
      </Tabs>
    </Stack>
  );
}
