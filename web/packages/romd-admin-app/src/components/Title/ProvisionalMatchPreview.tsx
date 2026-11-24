import { Alert, Group, Image, Paper, Stack, Text } from '@mantine/core';
import type { TitleProviderMatch } from '@romd/admin-api-client';
import classes from './ProviderMatches.module.css';

export function ProvisionalMatchPreview({ match }: { match: TitleProviderMatch }) {
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
      'Description',
      fields?.description,
    ],
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
      fields?.rating,
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
        <Stack gap={4}>
          <Text fw={600}>Why ROMD picked {proposal?.game.name ?? 'this game'}</Text>
          <Text size="sm">{match.decision?.explanation}</Text>
          {!!proposal?.evidence.length && (
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
          )}
        </Stack>
        {!!supplied.length && (
          <Stack gap={4}>
            <Text fw={600}>Provisional fields</Text>
            <dl className={classes.contributionFields}>
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
        {!!review.artwork.length && (
          <Stack gap={4}>
            <Text fw={600}>Provisional artwork</Text>
            <Group
              gap="sm"
              align="end"
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
                      w={112}
                      h={72}
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
          </Stack>
        )}
        {!supplied.length && !review.artwork.length && (
          <Text
            size="sm"
            c="dimmed"
          >
            This match has no provisional contributions to display.
          </Text>
        )}
        <details>
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
