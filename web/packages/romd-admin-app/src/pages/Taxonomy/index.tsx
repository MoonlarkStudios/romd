import { Stack, Tabs, Text, Title } from '@mantine/core';
import { IconLanguage, IconWorld } from '@tabler/icons-react';
import { useMemo } from 'react';
import { useSearchParams } from 'react-router';
import classes from '../../components/Workspace/Workspace.module.css';
import {
  useAddLanguageAlias,
  useAddRegionAlias,
  useLanguages,
  useMergeLanguages,
  useMergeRegions,
  useRegions,
  useRemoveLanguageAlias,
  useRemoveRegionAlias,
} from '../../hooks/api/useTaxonomy';
import { usePermissions } from '../../hooks/usePermissions';
import { type TaxonomyEntry, TaxonomyPanel } from './TaxonomyPanel';

export function Taxonomy() {
  const { canEditReferenceData: canEdit } = usePermissions();
  const [params, setParams] = useSearchParams();
  const tab = ['regions', 'languages'].find(value => value === params.get('tab')) ?? 'regions';

  const regions = useRegions();
  const languages = useLanguages();

  const addRegionAlias = useAddRegionAlias();
  const removeRegionAlias = useRemoveRegionAlias();
  const mergeRegions = useMergeRegions();
  const addLanguageAlias = useAddLanguageAlias();
  const removeLanguageAlias = useRemoveLanguageAlias();
  const mergeLanguages = useMergeLanguages();

  const regionEntries = useMemo<TaxonomyEntry[]>(
    () =>
      (regions.data ?? []).map((r) => ({
        id: r.id,
        name: r.name,
        secondary: null,
        isAutoCreated: r.isAutoCreated,
        canMerge: r.canMerge,
        sortOrder: r.sortOrder,
        aliases: r.aliases,
      })),
    [
      regions.data,
    ],
  );

  const languageEntries = useMemo<TaxonomyEntry[]>(
    () =>
      (languages.data ?? []).map((l) => ({
        id: l.id,
        name: l.name,
        secondary: l.code,
        isAutoCreated: l.isAutoCreated,
        canMerge: l.canMerge,
        sortOrder: l.sortOrder,
        aliases: l.aliases,
      })),
    [
      languages.data,
    ],
  );

  const regionsBusy =
    addRegionAlias.isPending || removeRegionAlias.isPending || mergeRegions.isPending;
  const languagesBusy =
    addLanguageAlias.isPending || removeLanguageAlias.isPending || mergeLanguages.isPending;

  return (
    <Stack className={classes.page} gap="lg">
      <div className={classes.hero}>
        <Title order={1} className={classes.heading}>Reference data</Title>
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
        onChange={value => setParams({ tab: value ?? 'regions' })}
        classNames={{ root: classes.tabs, list: classes.tabsList, tab: classes.tab }}
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
            isLoading={regions.isLoading}
            isError={regions.isError}
            onRetry={() => void regions.refetch()}
            canEdit={canEdit}
            busy={regionsBusy}
            onAddAlias={(regionId, alias) =>
              addRegionAlias.mutateAsync({
                regionId,
                alias,
              })
            }
            onRemoveAlias={(regionId, aliasId) =>
              removeRegionAlias.mutateAsync({
                regionId,
                aliasId,
              })
            }
            onMerge={(sourceId, targetId) =>
              mergeRegions.mutateAsync({
                sourceId,
                targetId,
              })
            }
          />
        </Tabs.Panel>

        <Tabs.Panel value="languages">
          <TaxonomyPanel
            noun="language"
            entries={languageEntries}
            isLoading={languages.isLoading}
            isError={languages.isError}
            onRetry={() => void languages.refetch()}
            canEdit={canEdit}
            busy={languagesBusy}
            onAddAlias={(languageId, alias) =>
              addLanguageAlias.mutateAsync({
                languageId,
                alias,
              })
            }
            onRemoveAlias={(languageId, aliasId) =>
              removeLanguageAlias.mutateAsync({
                languageId,
                aliasId,
              })
            }
            onMerge={(sourceId, targetId) =>
              mergeLanguages.mutateAsync({
                sourceId,
                targetId,
              })
            }
          />
        </Tabs.Panel>
      </Tabs>
    </Stack>
  );
}
