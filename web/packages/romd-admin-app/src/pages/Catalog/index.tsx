import { Stack, Tabs, Title } from '@mantine/core';
import { useSearchParams } from 'react-router';
import workspace from '../../components/Workspace/Workspace.module.css';
import { TitlesLens } from './TitlesLens';

export function Catalog() {
  const [params, setParams] = useSearchParams();
  const view = params.get('view') === 'all' || (!params.has('view') && params.get('tracked') === 'untracked') ? 'all' : 'tracked';
  return <div className={workspace.page}>
    <Stack gap="md">
      <div className={workspace.hero}><Title order={1} className={workspace.heading}>Catalog</Title></div>
      <Tabs value={view} onChange={(value) => {
        if (!value) return;
        setParams((previous) => {
          const next = new URLSearchParams(previous);
          next.set('view', value); next.delete('tracked');
          return next;
        });
      }} keepMounted={false} color="teal" classNames={{ list: workspace.tabsList, tab: workspace.tab }}>
        <Tabs.List><Tabs.Tab value="tracked">Tracked titles</Tabs.Tab><Tabs.Tab value="all">All titles</Tabs.Tab></Tabs.List>
        <Tabs.Panel value="tracked" pt="md"><TitlesLens showHeader={false} lockedFilters={{ tracked: 'tracked' }} /></Tabs.Panel>
        <Tabs.Panel value="all" pt="md"><TitlesLens showHeader={false} /></Tabs.Panel>
      </Tabs>
    </Stack>
  </div>;
}
