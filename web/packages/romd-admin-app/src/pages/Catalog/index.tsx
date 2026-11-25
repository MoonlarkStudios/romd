import { Alert, Button, Stack, Title } from '@mantine/core';
import { Navigate, useLocation, useSearchParams } from 'react-router';
import { SectionTabs } from '../../components/Workspace/SectionTabs';
import workspace from '../../components/Workspace/Workspace.module.css';
import { useEnrichmentWorkspaceCounts } from '../../hooks/api/useEnrichmentWorkspace';
import { usePermissions } from '../../hooks/usePermissions';
import classes from './Catalog.module.css';
import { CatalogReview } from './CatalogReview';
import { TitlesLens } from './TitlesLens';

export function Catalog() {
  const [params] = useSearchParams();
  const { state } = useLocation();
  const { canManageTitles } = usePermissions();
  const counts = useEnrichmentWorkspaceCounts('', canManageTitles);
  const request = params.get('view');
  const view =
    request === 'review' && canManageTitles
      ? 'review'
      : request === 'all' || (!request && params.get('tracked') === 'untracked')
        ? 'all'
        : 'tracked';

  const changeView = (id: string) => {
    const next = new URLSearchParams(params);
    next.set('view', id);
    for (const key of [
      'tracked',
      'title',
      'requirement',
      'role',
    ])
      next.delete(key);
    if (id !== 'review') next.delete('state');
    return {
      search: `?${next}`,
    };
  };

  if (request === 'review' && !canManageTitles)
    return (
      <Navigate
        to="/catalog"
        replace
      />
    );

  return (
    <div className={`${workspace.page} ${classes.catalogPage}`}>
      <Stack gap="md">
        <div className={`${workspace.hero} ${classes.catalogHeading}`}>
          <Title
            order={1}
            className={workspace.heading}
          >
            Catalog
          </Title>
        </div>
        <div className={classes.catalogViews}>
          <SectionTabs
            label="Catalog views"
            current={view}
            state={state}
            items={[
              ...(canManageTitles
                ? [
                    {
                      id: 'review',
                      label: 'Needs review',
                      to: changeView('review'),
                      count: counts.data?.needsReview,
                    },
                  ]
                : []),
              {
                id: 'tracked',
                label: 'Tracked titles',
                to: changeView('tracked'),
              },
              {
                id: 'all',
                label: 'All titles',
                to: changeView('all'),
              },
            ].map((item) => ({
              ...item,
              to:
                item.id === view
                  ? {
                      search: `?${params}`,
                    }
                  : item.to,
            }))}
          />
        </div>
        {view === 'review' && counts.isError && (
          <Alert
            color="yellow"
            title={counts.data ? 'Review count may be out of date' : 'Review count unavailable'}
          >
            <Button
              variant="subtle"
              size="compact-sm"
              onClick={() => void counts.refetch()}
            >
              Retry count
            </Button>
          </Alert>
        )}
        {view === 'review' ? (
          <CatalogReview />
        ) : (
          <TitlesLens
            key={view}
            showHeader={false}
            lockedFilters={
              view === 'tracked'
                ? {
                    tracked: 'tracked',
                  }
                : undefined
            }
          />
        )}
      </Stack>
    </div>
  );
}
