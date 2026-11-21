import { Alert, Badge, Button, Group, List, Modal, Skeleton, Stack, Text } from '@mantine/core';
import type { LibraryPublicationAttempt, LibraryPublicationGame } from '@romd/admin-api-client';
import { Link } from 'react-router';
import { ApiClientError, useForceMaterializeLibrary } from '../../hooks/api/useLibraryManagement';
import { useLibraryPublicationReview } from '../../hooks/api/useLibraryPublication';
import { exclusionReasons } from './libraryEvaluation';
import { publicationOutcomeCopy } from './libraryRefresh';

export function LibraryPublicationProgress({
  attempt,
}: {
  attempt?:
    | (Pick<LibraryPublicationAttempt, 'jobId' | 'phase' | 'isTerminal'> & {
        outcome?: string | null;
      })
    | null;
}) {
  if (!attempt) return null;
  const active = !attempt.isTerminal;
  const label = active
    ? attempt.phase === 'Materializing'
      ? 'Publishing library'
      : 'Publication queued'
    : attempt.phase === 'Completed'
      ? 'Published'
      : attempt.phase;
  return (
    <Alert
      color={active ? 'blue' : attempt.phase === 'Failed' ? 'red' : 'gray'}
      role="status"
      mb="lg"
    >
      <Group
        justify="space-between"
        align="center"
      >
        <div>
          <Text
            fw={600}
            size="sm"
          >
            {label}
          </Text>
          {!active && (
            <Text size="sm">{publicationOutcomeCopy(attempt.phase, attempt.outcome)}</Text>
          )}
        </div>
        <Button
          component={Link}
          to={`/jobs/${attempt.jobId}`}
          variant="light"
          size="compact-sm"
        >
          View publication job
        </Button>
      </Group>
    </Alert>
  );
}

function Examples({ title, games }: { title: string; games: LibraryPublicationGame[] }) {
  if (!games.length) return null;
  return (
    <div>
      <Text
        size="sm"
        fw={600}
      >
        {title}
      </Text>
      <List size="sm">
        {games.map((game) => (
          <List.Item key={game.id}>
            {game.name}
            {game.reason && exclusionReasons[game.reason]
              ? ` · ${exclusionReasons[game.reason]}`
              : ''}
          </List.Item>
        ))}
      </List>
    </div>
  );
}

export function LibraryPublishDialog({
  libraryId,
  action,
  close,
  accepted,
}: {
  libraryId: string;
  action: string;
  close: () => void;
  accepted: (jobId: string) => void;
}) {
  const review = useLibraryPublicationReview(libraryId);
  const publish = useForceMaterializeLibrary();
  const data = review.data;
  const conflict = publish.error instanceof ApiClientError && publish.error.status === 409;
  return (
    <Modal
      opened
      onClose={close}
      title="Review publication"
      size="lg"
      closeOnEscape={!publish.isPending}
      closeOnClickOutside={!publish.isPending}
      withCloseButton={!publish.isPending}
    >
      <Stack gap="md">
        {review.isPending && (
          <Skeleton
            h={150}
            aria-label="Checking publication changes"
          />
        )}
        {review.isError && <Alert color="red">{review.error.message}</Alert>}
        {data && (
          <>
            <Text fw={650}>
              {data.publishedAt
                ? `${data.publishedGames} → ${data.projectedGames} owned games`
                : `${data.projectedGames} owned games in the first publication`}
            </Text>
            <Group gap="xs">
              <Badge
                variant="light"
                color="teal"
              >
                {data.addedGames} added
              </Badge>
              <Badge
                variant="light"
                color="orange"
              >
                {data.removedGames} removed
              </Badge>
              <Badge
                variant="light"
                color="gray"
              >
                {data.assignedAccounts} assigned{' '}
                {data.assignedAccounts === 1 ? 'account' : 'accounts'}
              </Badge>
            </Group>
            <Examples
              title={
                data.removedGames > data.removedExamples.length
                  ? 'Examples of games removed'
                  : 'Games removed'
              }
              games={data.removedExamples}
            />
            <Examples
              title={
                data.addedGames > data.addedExamples.length
                  ? 'Examples of games added'
                  : 'Games added'
              }
              games={data.addedExamples}
            />
            {data.shelvesEmptied > 0 && (
              <Text size="sm">
                {data.shelvesEmptied} {data.shelvesEmptied === 1 ? 'shelf will' : 'shelves will'}{' '}
                become empty. {data.shelvesEmptiedExamples.join(', ')}
              </Text>
            )}
            {data.publishedAt && (
              <Text
                size="xs"
                c="dimmed"
              >
                Last published {new Date(data.publishedAt).toLocaleString()}
              </Text>
            )}
          </>
        )}
        {publish.isError && <Alert color="red">{publish.error.message}</Alert>}
        <Group justify="end">
          <Button
            variant="default"
            onClick={close}
            disabled={publish.isPending}
          >
            Cancel
          </Button>
          {review.isError || conflict ? (
            <Button
              onClick={() => {
                publish.reset();
                void review.refetch();
              }}
              loading={review.isFetching}
            >
              Check again
            </Button>
          ) : (
            <Button
              disabled={!data || review.isFetching}
              loading={publish.isPending}
              onClick={() => {
                if (data)
                  publish.mutate(
                    {
                      libraryId,
                      reviewedRevision: data.revision,
                    },
                    {
                      onSuccess: (result) => accepted(result.jobId),
                    },
                  );
              }}
            >
              {action}
            </Button>
          )}
        </Group>
      </Stack>
    </Modal>
  );
}
