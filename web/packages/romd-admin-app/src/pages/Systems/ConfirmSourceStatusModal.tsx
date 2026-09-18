import {
  Alert,
  Button,
  Group,
  Loader,
  Modal,
  Paper,
  SimpleGrid,
  Stack,
  Text,
  TextInput,
} from '@mantine/core';
import type { Dat, SourceLifecycleStatus } from '@romd/admin-api-client';
import { applySourceLifecycle, previewSourceLifecycle } from '@romd/admin-api-client';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';

export interface ConfirmSourceStatusModalProps {
  dat: Dat;
  targetStatus: SourceLifecycleStatus | 'Delete' | null;
  onClose: () => void;
}

export function ConfirmSourceStatusModal({
  dat,
  targetStatus,
  onClose,
}: ConfirmSourceStatusModalProps) {
  const client = useQueryClient();
  const [confirmation, setConfirmation] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string>();
  const deleting = targetStatus === 'Delete';
  const restoring = targetStatus === 'Active';
  const title = deleting
    ? 'Delete source permanently'
    : restoring
      ? 'Re-enable source'
      : targetStatus === 'Discontinued'
        ? 'Mark source discontinued'
        : 'Disable source';
  const impact = useQuery({
    queryKey: [
      'source-impact',
      dat.id,
      targetStatus,
    ],
    enabled: targetStatus !== null,
    refetchOnWindowFocus: false,
    queryFn: async ({ signal }) => {
      const result = await previewSourceLifecycle({
        path: {
          datId: dat.id,
        },
        query: {
          action: targetStatus ?? '',
        },
        signal,
      });
      if (result.error || !result.data)
        throw new Error('Could not load the impact. Your source is unchanged.');
      return result.data;
    },
  });
  const apply = async () => {
    if (!targetStatus || !impact.data) return;
    setSaving(true);
    setError(undefined);
    try {
      const result = await applySourceLifecycle({
        path: {
          datId: dat.id,
        },
        body: {
          action: targetStatus,
          reviewToken: impact.data.reviewToken,
        },
      });
      if (result.error) {
        setError(
          'The source could not be changed. Its coverage may have changed or processing may be running. Review the refreshed impact before trying again.',
        );
        await impact.refetch();
        return;
      }
      await client.invalidateQueries();
      setConfirmation('');
      onClose();
    } catch {
      setError('The change could not finish. Refresh the source status before retrying.');
    } finally {
      setSaving(false);
    }
  };
  return (
    <Modal
      opened={targetStatus !== null}
      onClose={() => {
        if (!saving) {
          setConfirmation('');
          setError(undefined);
          onClose();
        }
      }}
      title={title}
      size="lg"
      centered
      closeOnClickOutside={!saving}
      closeOnEscape={!saving}
      withCloseButton={!saving}
    >
      <Stack>
        <Text fw={600}>{dat.name}</Text>
        <Text size="sm">
          {deleting
            ? 'Remove this source, all its DAT versions, and its subscription permanently. Stored ROM files and saves are not deleted.'
            : restoring
              ? 'Restore this source’s catalog contribution and resume subscription checks. Existing title identities and personal settings are kept.'
              : 'Pause subscription checks and remove this source’s catalog contribution. Its DAT versions, ROM files, and personal settings stay saved. You can re-enable it later.'}
        </Text>
        {impact.isPending && (
          <Group>
            <Loader size="sm" />
            <Text size="sm">Calculating the impact…</Text>
          </Group>
        )}
        {impact.isError && (
          <Alert color="yellow">
            Could not load the impact.{' '}
            <Button
              variant="subtle"
              onClick={() => void impact.refetch()}
            >
              Retry preview
            </Button>
          </Alert>
        )}
        {impact.data && (
          <>
            <Group justify="space-between">
              <Text
                size="sm"
                c="dimmed"
              >
                {Number(impact.data.versions).toLocaleString()} DAT versions in this source.
              </Text>
              <Button
                variant="subtle"
                size="xs"
                disabled={saving}
                loading={impact.isFetching}
                onClick={() => void impact.refetch()}
              >
                Refresh impact
              </Button>
            </Group>
            {!restoring && (
              <>
                <Text size="sm">After this source’s contribution is removed:</Text>
                <SimpleGrid
                  cols={{
                    base: 1,
                    sm: 2,
                  }}
                >
                  {[
                    [
                      impact.data.stillCovered,
                      'Titles still covered by another source',
                    ],
                    [
                      impact.data.ownedWithoutDefinition,
                      'Titles with local ROMs, without a remaining definition',
                    ],
                    [
                      impact.data.personalWithoutDefinition,
                      'Other titles kept for personal state',
                    ],
                    [
                      impact.data.catalogOnlyWithoutDefinition,
                      'Catalog-only titles without a remaining definition',
                    ],
                  ].map(([count, label]) => (
                    <Paper
                      key={String(label)}
                      withBorder
                      p="sm"
                    >
                      <Text
                        fw={700}
                        size="lg"
                      >
                        {Number(count).toLocaleString()}
                      </Text>
                      <Text size="sm">{label}</Text>
                    </Paper>
                  ))}
                </SimpleGrid>
                <Text
                  size="xs"
                  c="dimmed"
                >
                  Distinct titles, not source entries. Titles without remaining definitions leave
                  the active catalog. Owned titles and personal state retain their identity and show
                  “No active catalog definition.” Counts include titles already without a
                  definition.
                </Text>
              </>
            )}
            {impact.data.busy && (
              <Alert color="yellow">
                A source check or import is running. Wait for it to finish, then refresh the
                preview.
              </Alert>
            )}
          </>
        )}
        {deleting && (
          <TextInput
            label="Type the source name to confirm permanent deletion"
            value={confirmation}
            onChange={(e) => setConfirmation(e.currentTarget.value)}
            autoComplete="off"
          />
        )}
        {error && <Alert color="yellow">{error}</Alert>}
        <Group justify="flex-end">
          <Button
            variant="default"
            disabled={saving}
            onClick={() => {
              setConfirmation('');
              setError(undefined);
              onClose();
            }}
          >
            Cancel
          </Button>
          <Button
            color={restoring ? 'green' : deleting ? 'red' : 'orange'}
            loading={saving}
            disabled={
              !impact.data ||
              impact.isFetching ||
              impact.isError ||
              impact.data.busy ||
              (deleting && confirmation !== dat.name)
            }
            onClick={() => void apply()}
          >
            {title}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
