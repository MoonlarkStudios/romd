import { Alert, Anchor, Breadcrumbs, Button, Skeleton, Stack, Text, Title } from '@mantine/core';
import { useLayoutEffect } from 'react';
import { Link, Navigate, useLocation, useParams } from 'react-router';
import { TitleHero } from '../../components/Title/TitleHero';
import { TitleWorkbench } from '../../components/Title/TitleWorkbench';
import {
  titleRoute,
  titleSectionLabels,
  titleSectionPath,
  titleSourcesAnchor,
} from '../../components/Title/titleSections';
import workspace from '../../components/Workspace/Workspace.module.css';
import { useTitleDetail } from '../../hooks/api/useTitleDetail';
import { ContentRatingsPanel } from './ContentRatingsPanel';
import { ReleasesPanel } from './ReleasesPanel';

export function TitleDetail() {
  const { titleId, section } = useParams();
  const location = useLocation();
  const route = titleId ? titleRoute(titleId, section, location.search, location.hash) : undefined;
  if (route?.replace)
    return (
      <Navigate
        replace
        to={route.to}
        state={location.state}
      />
    );
  return titleId && route ? (
    <TitlePage
      titleId={titleId}
      section={route.section}
    />
  ) : null;
}

function TitlePage({
  titleId,
  section,
}: {
  titleId: string;
  section: ReturnType<typeof titleRoute>['section'];
}) {
  const { state, hash } = useLocation();
  const returnTo: unknown = state?.catalogReturnTo;
  const catalog =
    typeof returnTo === 'string' && /^\/catalog(?:\?|$)/.test(returnTo) ? returnTo : '/catalog';
  const query = useTitleDetail(titleId);
  const title = query.data;
  const hasTitle = !!title;
  useLayoutEffect(() => {
    if (
      !hasTitle ||
      ![
        '#disc-sets',
        '#release-files',
        '#title-status',
        `#${titleSourcesAnchor}`,
      ].includes(hash)
    )
      return;
    const target = document.getElementById(hash.slice(1));
    if (target?.id === titleSourcesAnchor)
      target.focus({
        preventScroll: true,
      });
    target?.scrollIntoView?.({
      block: 'start',
    });
  }, [
    hasTitle,
    hash,
  ]);

  return (
    <Stack
      gap="md"
      className={workspace.page}
    >
      <Breadcrumbs aria-label="Breadcrumb">
        <Anchor
          component={Link}
          to={catalog}
          size="sm"
        >
          Catalog
        </Anchor>
        {title && (
          <Anchor
            component={Link}
            to={titleSectionPath(titleId)}
            state={state}
            size="sm"
          >
            {title.name}
          </Anchor>
        )}
        {title && (
          <Text
            size="sm"
            aria-current="page"
          >
            {titleSectionLabels[section]}
          </Text>
        )}
      </Breadcrumbs>
      {title ? (
        <>
          {query.isRefetchError && (
            <Alert
              title="Could not refresh this title"
              color="yellow"
            >
              The last available details are still shown.
              <Button
                variant="subtle"
                size="compact-sm"
                onClick={() => void query.refetch()}
              >
                Retry
              </Button>
            </Alert>
          )}
          <TitleHero
            title={title}
            titleId={titleId}
            compact={section === 'artwork'}
          />
          <TitleWorkbench
            key={titleId}
            title={title}
            section={section}
          >
            {section === 'releases' && (
              <ReleasesPanel
                titleId={titleId}
                preference={title}
                releases={title.releases}
              />
            )}
            {section === 'ratings' && (
              <ContentRatingsPanel
                key={titleId}
                title={title}
                titleId={titleId}
              />
            )}
          </TitleWorkbench>
        </>
      ) : query.isPending ? (
        <>
          <Skeleton h={220} />
          <Skeleton h={40} />
          <Skeleton h={175} />
        </>
      ) : (
        <Alert
          title={
            <Title
              order={1}
              size="h3"
            >
              Could not load title
            </Title>
          }
          color="red"
        >
          <Button
            variant="subtle"
            onClick={() => void query.refetch()}
          >
            Retry
          </Button>
        </Alert>
      )}
    </Stack>
  );
}
