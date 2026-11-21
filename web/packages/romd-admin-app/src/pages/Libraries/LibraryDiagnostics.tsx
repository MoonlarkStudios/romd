import {
  Accordion,
  Alert,
  Badge,
  Button,
  Group,
  SimpleGrid,
  Skeleton,
  Stack,
  Text,
} from '@mantine/core';
import type { LibraryTitleVisibility, RatingBoard } from '@romd/admin-api-client';
import { Link, useLocation, useSearchParams } from 'react-router';
import { titleSectionPath } from '../../components/Title/titleSections';
import { TitleSearchSelect } from '../../components/TitleSearchSelect';
import {
  useLibraryTitleReleaseDiagnostics,
  useLibraryTitleVisibility,
} from '../../hooks/api/useLibraryManagement';
import { boardLabel } from '../TitleDetail/contentRatings';
import classes from './Libraries.module.css';
import { exclusionReasons } from './libraryEvaluation';

function VisibilitySummary({ game, returnTo }: { game: LibraryTitleVisibility; returnTo: string }) {
  const ratingIssue = [
    'ContentRating',
    'RefusedClassification',
    'UnknownRatingHidden',
    'UnknownRatingNeedsReview',
  ].some((reason) => game.reason === reason);
  const releaseIssue =
    [
      'MissingRom',
      'NoLinkedRelease',
      'ExcludedDat',
    ].some((reason) => game.reason === reason) ||
    (game.isEligible && !game.isOwned);
  const section = ratingIssue ? 'ratings' : releaseIssue ? 'releases' : undefined;
  return (
    <Stack gap="xs">
      <Text fw={650}>{game.titleName}</Text>
      <SimpleGrid
        cols={{
          base: 1,
          sm: 2,
        }}
        spacing="sm"
      >
        <Stack
          className={classes.overviewStat}
          gap={4}
        >
          <Text
            size="xs"
            c="dimmed"
          >
            Current saved rules
          </Text>
          <Text
            size="sm"
            fw={600}
          >
            {game.isEligible
              ? game.isOwned
                ? 'Eligible and owned'
                : 'Eligible without owned files'
              : 'Excluded'}
          </Text>
          {game.reason && (
            <Text size="sm">
              {exclusionReasons[game.reason] ?? 'Does not meet the saved rules.'}
            </Text>
          )}
          {game.ratingBoard && game.ratingCategory && (
            <Text
              size="xs"
              c="dimmed"
            >
              {boardLabel(game.ratingBoard as RatingBoard)} {game.ratingCategory}
              {game.ratingAge != null && ` · Ages ${game.ratingAge}+`}
            </Text>
          )}
        </Stack>
        <Stack
          className={classes.overviewStat}
          gap={4}
        >
          <Text
            size="xs"
            c="dimmed"
          >
            Published library
          </Text>
          <Text
            size="sm"
            fw={600}
          >
            {!game.hasPublication
              ? 'Library not published'
              : game.isPublished
                ? 'Visible to this audience'
                : 'Hidden from this audience'}
          </Text>
          {game.needsPublish && game.hasPublication && (
            <Text size="sm">Publish changes to update what this audience can see.</Text>
          )}
        </Stack>
      </SimpleGrid>
      {section && (
        <Button
          component={Link}
          to={titleSectionPath(game.titleId, section)}
          state={{
            libraryReturnTo: returnTo,
          }}
          variant="light"
          size="compact-sm"
          style={{
            alignSelf: 'flex-start',
          }}
        >
          {section === 'ratings' ? 'Open ratings' : 'Open releases'}
        </Button>
      )}
    </Stack>
  );
}

export function LibraryDiagnostics({
  libraryId,
  published = true,
}: {
  libraryId: string;
  published?: boolean;
}) {
  const [params, setParams] = useSearchParams();
  const { pathname, search } = useLocation();
  const titleId = params.get('inspect') || null;
  const visibility = useLibraryTitleVisibility(libraryId, titleId);
  const query = useLibraryTitleReleaseDiagnostics(published ? libraryId : null, titleId);
  return (
    <Accordion
      variant="separated"
      radius="md"
      defaultValue={titleId ? 'diagnostics' : undefined}
    >
      <Accordion.Item value="diagnostics">
        <Accordion.Control>Game visibility</Accordion.Control>
        <Accordion.Panel>
          <Stack>
            <TitleSearchSelect
              label={titleId ? 'Find another game' : 'Inspect a game'}
              placeholder="Search the catalog"
              value={titleId}
              clearAfterSelect
              onChange={(id) => {
                const next = new URLSearchParams(params);
                if (id) next.set('inspect', id);
                else next.delete('inspect');
                setParams(next, {
                  preventScrollReset: true,
                });
              }}
            />
            {titleId && visibility.isPending && (
              <Skeleton
                h={70}
                aria-label="Checking game visibility"
              />
            )}
            {visibility.isError && (
              <Alert color="red">
                {visibility.error.message}{' '}
                <Button
                  variant="subtle"
                  size="compact-sm"
                  onClick={() => void visibility.refetch()}
                >
                  Retry
                </Button>
              </Alert>
            )}
            {visibility.data && (
              <VisibilitySummary
                game={visibility.data}
                returnTo={`${pathname}${search}`}
              />
            )}
            {published && titleId && query.isPending && <Skeleton h={70} />}
            {query.isError && (
              <Alert color="red">
                Could not load published release rules.{' '}
                <Button
                  variant="subtle"
                  size="compact-sm"
                  onClick={() => void query.refetch()}
                >
                  Retry
                </Button>
              </Alert>
            )}
            {titleId &&
              visibility.data?.isPublished &&
              query.isSuccess &&
              query.data.length === 0 && (
                <Text
                  size="sm"
                  c="dimmed"
                >
                  No release details in the last publication.
                </Text>
              )}
            {!!query.data?.length && (
              <details>
                <summary>Published release rules ({query.data.length})</summary>
                <Stack
                  mt="sm"
                  gap="xs"
                >
                  {query.data.map((row) => (
                    <Group
                      key={row.id}
                      justify="space-between"
                    >
                      <Stack gap={2}>
                        <Text
                          size="sm"
                          fw={550}
                        >
                          {row.name}
                        </Text>
                        {row.blockReason && (
                          <Text
                            size="xs"
                            c="dimmed"
                          >
                            {exclusionReasons[row.blockReason] ??
                              'Excluded by the published release rules'}
                          </Text>
                        )}
                        {row.vfsPathError && (
                          <Text
                            size="xs"
                            c="orange"
                          >
                            VFS unavailable: {row.vfsPathError}
                          </Text>
                        )}
                      </Stack>
                      <Badge color={row.isExposed ? 'green' : 'gray'}>
                        {row.isExposed ? 'Allowed' : 'Excluded'}
                      </Badge>
                    </Group>
                  ))}
                </Stack>
              </details>
            )}
          </Stack>
        </Accordion.Panel>
      </Accordion.Item>
    </Accordion>
  );
}
