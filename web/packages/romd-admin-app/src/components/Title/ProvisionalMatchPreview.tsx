import { Alert, Anchor, Badge, Group, Image, Paper, Stack, Text } from '@mantine/core';
import type { TitleProviderMatch } from '@romd/admin-api-client';
import classes from './ProviderMatches.module.css';

const ratingFormat = new Intl.NumberFormat(undefined, {
  maximumFractionDigits: 1,
});

export function ProvisionalMatchPreview({
  match,
  localTitle,
  localSystems,
  proposedSystems,
}: {
  match: TitleProviderMatch;
  localTitle: string;
  localSystems: readonly string[];
  proposedSystems: readonly string[];
}) {
  const review = match.provisionalReview;
  if (!review) return null;
  if (!review.evidenceCurrent)
    return (
      <Alert
        color="yellow"
        title="Provisional evidence changed"
      >
        ROMD withdrew the provisional fields and artwork. Search again or enter a provider ID to
        make a fresh decision.
      </Alert>
    );

  const proposal = match.decision?.candidates.find(({ game }) => game.id === review.gameId);
  const fields = review.fields;
  const values: [
    string,
    string | number | null | undefined,
  ][] = [
    [
      'Publisher',
      fields?.publisher,
    ],
    [
      'Developer',
      fields?.developer,
    ],
    [
      'Genre',
      fields?.genre,
    ],
    [
      'Release date',
      fields?.releaseDate,
    ],
    [
      'Players',
      fields?.players,
    ],
    [
      'Rating',
      fields?.rating == null ? null : `${ratingFormat.format(fields.rating)} / 100`,
    ],
    ...review.ratings.map<
      [
        string,
        string,
      ]
    >(({ board, code }) => [
      `${board.toUpperCase()} rating`,
      code,
    ]),
  ];
  const supplied = values.filter(([, value]) => value != null);
  return (
    <Paper
      component="section"
      aria-label="Provisional match review"
      withBorder
      p="sm"
    >
      <Stack gap="sm">
        <Text
          size="xs"
          c="dimmed"
        >
          Local title ·{' '}
          {[
            localTitle,
            ...localSystems,
          ].join(' · ')}
        </Text>
        <Group
          align="start"
          justify="space-between"
        >
          <Stack
            gap={4}
            className={classes.proposalIdentity}
            align="start"
          >
            <Badge
              variant="light"
              color="yellow"
              size="sm"
            >
              Provisional match
            </Badge>
            {proposal?.game.url.startsWith('https://') ? (
              <Anchor
                href={proposal.game.url}
                target="_blank"
                rel="noopener noreferrer"
                fw={600}
              >
                {proposal.game.name}
              </Anchor>
            ) : (
              <Text fw={600}>{proposal?.game.name ?? match.providerName}</Text>
            )}
            {proposal && (
              <Text
                size="sm"
                c="dimmed"
              >
                {[
                  ...proposedSystems,
                  proposal.game.year,
                  `${match.providerName} #${proposal.game.id}`,
                ]
                  .filter(Boolean)
                  .join(' · ')}
              </Text>
            )}
            <Text size="sm">{match.decision?.explanation}</Text>
          </Stack>
          {!!review.artwork.length && (
            <Group
              className={classes.proposalArtwork}
              gap="xs"
              align="start"
              aria-label="Provisional artwork"
            >
              {review.artwork.map(({ role, previewUrl }) => (
                <Stack
                  key={role}
                  gap={4}
                >
                  {previewUrl && (
                    <Image
                      src={previewUrl}
                      alt={`${role} from ${match.providerName}`}
                      fit="contain"
                      w={role === 'Poster' ? 72 : 96}
                      h={role === 'Poster' ? 104 : 72}
                      radius="sm"
                    />
                  )}
                  <Text
                    size="xs"
                    c="dimmed"
                  >
                    {role}
                  </Text>
                </Stack>
              ))}
            </Group>
          )}
        </Group>
        {!!proposal?.evidence.length && (
          <details className={classes.reviewDetails}>
            <summary>Why ROMD picked this match</summary>
            <ul className={classes.evidenceList}>
              {proposal.evidence.map((fact) => (
                <Text
                  component="li"
                  size="sm"
                  key={fact}
                >
                  {fact}
                </Text>
              ))}
            </ul>
          </details>
        )}
        {!!proposal?.conflicts.length && (
          <Alert
            color="yellow"
            title="Conflicting evidence"
          >
            <ul className={classes.evidenceList}>
              {proposal.conflicts.map((conflict) => (
                <li key={conflict}>{conflict}</li>
              ))}
            </ul>
          </Alert>
        )}
        {!!supplied.length && (
          <Stack gap={4}>
            <Text fw={600}>Provisional fields</Text>
            <dl className={`${classes.contributionFields} ${classes.fieldGrid}`}>
              {supplied.map(([label, value]) => (
                <div key={label}>
                  <Text
                    component="dt"
                    size="xs"
                    c="dimmed"
                  >
                    {label}
                  </Text>
                  <Text
                    component="dd"
                    size="sm"
                  >
                    {value}
                  </Text>
                </div>
              ))}
            </dl>
          </Stack>
        )}
        {fields?.description && (
          <details className={classes.reviewDetails}>
            <summary>Provisional description</summary>
            <Text
              className={classes.description}
              size="sm"
            >
              {fields.description}
            </Text>
          </details>
        )}
        {!supplied.length && !fields?.description && !review.artwork.length && (
          <Text
            size="sm"
            c="dimmed"
          >
            This match has no provisional contributions to display.
          </Text>
        )}
        <details className={classes.reviewDetails}>
          <summary>What each action does</summary>
          <dl className={classes.contributionFields}>
            <div>
              <Text
                component="dt"
                size="sm"
                fw={500}
              >
                Confirm
              </Text>
              <Text
                component="dd"
                size="sm"
              >
                {match.capabilities.includes('metadata')
                  ? 'Publish this game’s provider metadata, keep its retained artwork, and recalculate affected library rules.'
                  : 'Keep this game’s retained artwork for automatic use.'}
              </Text>
            </div>
            <div>
              <Text
                component="dt"
                size="sm"
                fw={500}
              >
                Replace
              </Text>
              <Text
                component="dd"
                size="sm"
              >
                Withdraw this match’s contributions and queue enrichment for the game you select.
              </Text>
            </div>
            <div>
              <Text
                component="dt"
                size="sm"
                fw={500}
              >
                Reject
              </Text>
              <Text
                component="dd"
                size="sm"
              >
                Withdraw this match’s contributions and turn off automatic matching for this
                provider. User edits and pinned artwork are kept.
              </Text>
            </div>
          </dl>
        </details>
      </Stack>
    </Paper>
  );
}
