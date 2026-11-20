import { Alert, Badge, Button, Group, Progress, Stack, Text } from '@mantine/core';
import type { ArtworkSelectionState, ResolvedArtwork } from '@romd/admin-api-client';
import { artworkAspectRatios, artworkCrop, artworkObjectStyle } from '@romd/consumer-ui';
import { IconPhotoOff } from '@tabler/icons-react';
import { useId } from 'react';
import { ArtworkSurface } from '../ArtworkSurface';
import { type ArtworkRole, artworkRoleLabel } from '../artworkPresentation';
import { workspaceActionProps } from '../Workspace/workspaceActions';
import classes from './ArtworkWorkspace.module.css';

export type RolePresentationState =
  | 'loading'
  | 'unavailable'
  | 'importing'
  | 'pinned'
  | 'automatic'
  | 'fallback'
  | 'empty'
  | 'missing';

/** Which of the curator-facing states a role is in: who chose its image, or why it has none. */
export function rolePresentationState(
  selection: ArtworkSelectionState | undefined,
  artwork: ResolvedArtwork | undefined,
  { failedToLoad = false, importing = false } = {},
): RolePresentationState {
  if (!selection) return failedToLoad ? 'unavailable' : 'loading';
  if (selection.pendingJobId || importing) return 'importing';
  if (selection.mode === 'Empty') return 'empty';
  if (artwork?.fallbackReason === 'LegacyCover') return 'fallback';
  if (!artwork?.url) return 'missing';
  return selection.mode === 'Pinned' ? 'pinned' : 'automatic';
}

const stateCopy: Record<
  RolePresentationState,
  {
    label: string;
    color: string;
  }
> = {
  loading: {
    label: 'Loading',
    color: 'gray',
  },
  unavailable: {
    label: 'Unavailable',
    color: 'red',
  },
  importing: {
    label: 'Importing',
    color: 'blue',
  },
  pinned: {
    label: 'Pinned',
    color: 'grape',
  },
  automatic: {
    label: 'Automatic',
    color: 'teal',
  },
  fallback: {
    label: 'Fallback',
    color: 'yellow',
  },
  empty: {
    label: 'Intentionally empty',
    color: 'gray',
  },
  missing: {
    label: 'Missing',
    color: 'orange',
  },
};

export function roleStateLabel(state: RolePresentationState) {
  return stateCopy[state].label;
}

function stateDetail(state: RolePresentationState, role: ArtworkRole, source?: string) {
  const label = artworkRoleLabel(role).toLowerCase();
  switch (state) {
    case 'importing':
      return `The new ${label} is importing. Players keep seeing the current one until it is ready.`;
    case 'pinned':
      return `You chose this ${label}${source ? ` from ${source}` : ''}.`;
    case 'automatic':
      return `Chosen automatically${source ? ` from ${source}` : ''}.`;
    case 'fallback':
      return 'No poster is chosen, so players see the title cover.';
    case 'empty':
      return `You left the ${label} empty; automatic runs keep it that way.`;
    case 'missing':
      return `No ${label} yet. Automatic runs fill it when a provider has one.`;
    default:
      return '';
  }
}

/**
 * One presentation role as players see it: the consumer crop of its current image, who chose it, and its single edit
 * action. A failed import keeps the current image and offers Retry and Choose another.
 */
export function PresentationRoleCard({
  role,
  artwork,
  state,
  source,
  selected,
  panelId,
  disabled,
  failed,
  manual,
  onEdit,
  onAutomatic,
  onRetry,
  onChooseAnother,
}: {
  role: ArtworkRole;
  artwork?: ResolvedArtwork;
  state: RolePresentationState;
  source?: string;
  selected: boolean;
  panelId: string;
  disabled: boolean;
  failed: boolean;
  /** The curator chose this role (pinned or left empty), so it can return to automatic. */
  manual: boolean;
  onEdit: (trigger: HTMLElement) => void;
  onAutomatic: () => void;
  onRetry: () => void;
  onChooseAnother: (trigger: HTMLElement) => void;
}) {
  const id = useId();
  const label = artworkRoleLabel(role);
  const lower = label.toLowerCase();
  const copy = stateCopy[state];
  const detail = stateDetail(state, role, source);
  return (
    <article
      className={classes.roleCard}
      aria-labelledby={`${id}-label`}
      aria-describedby={`${id}-state`}
      aria-current={selected || undefined}
      data-inspector-row={`role-${role}`}
    >
      <ArtworkSurface background={role === 'Logo' ? 'checkerboard' : 'dark'}>
        <div
          className={classes.roleCrop}
          style={{
            aspectRatio: artworkAspectRatios[role],
          }}
        >
          {artwork?.url ? (
            <img
              src={artwork.url}
              alt={`Current ${lower}`}
              style={artworkObjectStyle(role, artworkCrop(artwork))}
            />
          ) : (
            <Stack
              align="center"
              justify="center"
              gap={4}
              h="100%"
            >
              <IconPhotoOff
                size={22}
                aria-hidden
              />
              <Text size="xs">No {lower}</Text>
            </Stack>
          )}
        </div>
      </ArtworkSurface>
      {state === 'importing' && (
        <Progress
          value={100}
          animated
          striped
          size="sm"
          aria-label={`Importing ${lower}`}
        />
      )}
      <Stack
        gap={4}
        className={classes.roleCardBody}
      >
        <Group
          justify="space-between"
          gap="xs"
          wrap="nowrap"
        >
          <Text
            id={`${id}-label`}
            fw={600}
            className="romd-card-title"
          >
            {label}
          </Text>
          <Badge
            id={`${id}-state`}
            size="sm"
            variant="light"
            color={copy.color}
          >
            {copy.label}
          </Badge>
        </Group>
        {detail && (
          <Text
            size="xs"
            c="dimmed"
          >
            {detail}
          </Text>
        )}
        {failed && (
          <Alert
            color="red"
            p="xs"
            role="alert"
          >
            <Text size="xs">The {lower} import failed. The current image is unchanged.</Text>
            <Group
              gap="xs"
              mt={6}
            >
              <Button
                size="compact-xs"
                variant="light"
                color="red"
                disabled={disabled}
                onClick={onRetry}
              >
                Retry
              </Button>
              <Button
                size="compact-xs"
                variant="subtle"
                disabled={disabled}
                onClick={(event) => onChooseAnother(event.currentTarget)}
              >
                Choose another
              </Button>
            </Group>
          </Alert>
        )}
        <Group
          gap="xs"
          mt="auto"
          pt={4}
        >
          <Button
            {...workspaceActionProps}
            data-inspector-trigger
            size="compact-sm"
            variant={selected ? 'filled' : 'light'}
            aria-controls={selected ? panelId : undefined}
            aria-expanded={selected}
            disabled={disabled}
            onClick={(event) => onEdit(event.currentTarget)}
          >
            Edit {lower}
          </Button>
          {manual && (
            <Button
              {...workspaceActionProps}
              size="compact-sm"
              variant="subtle"
              disabled={disabled}
              onClick={onAutomatic}
            >
              Use automatically
            </Button>
          )}
        </Group>
      </Stack>
    </article>
  );
}
