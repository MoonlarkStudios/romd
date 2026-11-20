import {
  Accordion,
  Alert,
  Anchor,
  Badge,
  Button,
  Group,
  NumberInput,
  Stack,
  Table,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import {
  type DiscSet,
  type DiscSetMemberEdit,
  listAdminDiscSets,
  updateAdminDiscSet,
} from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link } from 'react-router';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { libraryManagementKeys } from '../../hooks/api/useLibraryManagement';
import { collectPages } from '../../utils/collectPages';
import classes from './ReleasesPanel.module.css';

const issueLabels: Record<string, string> = {
  SingleObservedDisc: 'Only one disc name observed',
  DuplicateDiscNumber: 'Duplicate disc number',
  ConflictingTotals: 'Conflicting declared disc totals',
  MissingDiscNumbers: 'Missing disc numbers',
  TitleConflict: 'Conflicting title assignment',
  RepeatedRelease: 'The same release occupies multiple slots',
  UnresolvedDiscRoleSibling: 'Related names have unresolved disc roles',
  SourceUnavailable: 'Source entry unavailable',
  ContentChanged: 'Source content changed since the decision',
  DiscNumberOutOfRange: 'Disc number outside the supported range',
  UnsupportedDiscMarker: 'Unsupported disc marker',
  MultipleDiscMarkers: 'Ambiguous disc markers',
  InvalidDiscTotal: 'Invalid declared disc total',
  MalformedQualifiers: 'Malformed name qualifiers',
  NameTooLong: 'Source name exceeds the supported length',
  EmptyName: 'Empty source name',
  MissingTitle: 'No title in source name',
};
const issueLabel = (issue: string) => issueLabels[issue] ?? issue;

const effectiveSet = (set: DiscSet) => set.correction ?? set.proposal;
export const discSetRowId = (id: string) => `disc-set-${id}`;

export function useTitleDiscSets(titleId: string, enabled: boolean) {
  return useQuery({
    queryKey: [
      'discSets',
      titleId,
    ],
    enabled,
    queryFn: ({ signal }) =>
      collectPages(async (cursor) => {
        const response = await listAdminDiscSets({
          signal,
          query: {
            titleId,
            cursor,
            limit: 100,
          },
        });
        if (response.error || !response.data) throw new Error('Could not load disc sets.');
        return response.data;
      }),
  });
}

export function DiscSetsPanel({
  query,
  selectedId,
  onSelect,
}: {
  query: ReturnType<typeof useTitleDiscSets>;
  selectedId: string | null;
  onSelect: (id: string | null) => void;
}) {
  return (
    <section
      id="disc-sets"
      className={classes.discSets}
      aria-labelledby="disc-sets-heading"
    >
      <Stack gap="sm">
        <Title
          id="disc-sets-heading"
          order={2}
          className={workspace.sectionHeading}
        >
          Disc sets
        </Title>
        <Anchor
          component={Link}
          to="/disc-layouts"
          size="sm"
        >
          Review original descriptors and repair missing CUE layouts
        </Anchor>
        {query.isPending ? (
          <Text
            size="sm"
            c="dimmed"
            role="status"
          >
            Loading disc sets…
          </Text>
        ) : query.isError ? (
          <Alert
            color="red"
            title="Could not load disc sets"
          >
            <Button
              {...workspaceActionProps}
              variant="subtle"
              size="compact-sm"
              onClick={() => void query.refetch()}
            >
              Retry
            </Button>
          </Alert>
        ) : query.data.length === 0 ? (
          <Text
            size="sm"
            c="dimmed"
          >
            No disc proposals for this title. Proposals appear after the system catalog rebuilds.
          </Text>
        ) : (
          <Accordion
            variant="separated"
            value={selectedId}
            onChange={onSelect}
            keepMounted
            order={3}
          >
            {query.data.map((set) => {
              const effective = effectiveSet(set);
              const needsReview =
                set.proposal.reviewReasons.length > 0 ||
                effective.members.some((member) => member.issue);
              return (
                <Accordion.Item
                  key={set.id}
                  value={set.id}
                  id={discSetRowId(set.id)}
                >
                  <Accordion.Control>
                    <span className={classes.discSetIdentity}>
                      <Text
                        component="span"
                        className={classes.discSetName}
                        fw={600}
                      >
                        {effective.name}
                      </Text>
                      <span className={classes.discSetStats}>
                        <Text
                          component="span"
                          size="xs"
                          c="dimmed"
                        >
                          {effective.members.length} observed · Expected:{' '}
                          {effective.expectedDiscCount ?? 'unknown'}
                        </Text>
                        <Badge
                          component="span"
                          size="xs"
                          variant="light"
                        >
                          {set.decision}
                        </Badge>
                        {needsReview && (
                          <Badge
                            component="span"
                            size="xs"
                            variant="light"
                            color="yellow"
                          >
                            Needs review
                          </Badge>
                        )}
                      </span>
                    </span>
                  </Accordion.Control>
                  <Accordion.Panel>
                    <DiscSetEditor
                      key={set.revision}
                      set={set}
                    />
                  </Accordion.Panel>
                </Accordion.Item>
              );
            })}
          </Accordion>
        )}
      </Stack>
    </section>
  );
}

function DiscSetEditor({ set }: { set: DiscSet }) {
  const queryClient = useQueryClient();
  const effective = effectiveSet(set);
  const [editing, setEditing] = useState(false);
  const [name, setName] = useState(effective.name);
  const [total, setTotal] = useState<string | number>(effective.expectedDiscCount ?? '');
  const [reason, setReason] = useState('');
  const [members, setMembers] = useState<DiscSetMemberEdit[]>(
    effective.members.map((m) => ({
      entryKey: m.entryKey,
      ordinal: m.ordinal,
    })),
  );
  const mutation = useMutation({
    mutationFn: async (decision: string) => {
      const response = await updateAdminDiscSet({
        path: {
          discSetId: set.id,
        },
        headers: {
          'If-Match': `"${set.revision}"`,
        },
        body: {
          decision,
          name,
          expectedDiscCount: total === '' ? null : Number(total),
          members,
          reason,
        },
      });
      if (response.error)
        throw new Error(
          response.response?.status === 412
            ? 'This set changed. Reload before saving your correction.'
            : 'Could not save. Check the review issues, exact source entry names, ordinals and total.',
        );
    },
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: [
            'discSets',
          ],
        }),
        queryClient.invalidateQueries({
          queryKey: libraryManagementKeys.discSetOptions(),
        }),
      ]);
    },
  });
  const move = (index: number, offset: number) => {
    const next = [
      ...members,
    ];
    const destination = index + offset;
    [next[index], next[destination]] = [
      next[destination],
      next[index],
    ];
    setMembers(
      next.map((m, i) => ({
        ...m,
        ordinal: i + 1,
      })),
    );
  };
  const issues = [
    ...new Set([
      ...set.proposal.reviewReasons,
      ...effective.members.flatMap((m) =>
        m.issue
          ? [
              m.issue,
            ]
          : [],
      ),
    ]),
  ];
  return (
    <Stack gap="sm">
      <Text size="sm">Catalog source {set.catalogSourceId}</Text>
      <Text
        size="sm"
        c="dimmed"
      >
        Accepting a proposal preserves its current membership through catalog refreshes.
      </Text>
      {issues.length > 0 && (
        <Alert
          title="Review required"
          color="yellow"
        >
          {issues.map(issueLabel).join('; ')}
        </Alert>
      )}
      {set.correction && (
        <Text
          size="sm"
          c="dimmed"
        >
          Pinned correction: {set.correction.members.length} members. Current automatic proposal:{' '}
          {set.proposal.members.length} members.
        </Text>
      )}
      <Table className={classes.discMemberTable}>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>Disc</Table.Th>
            <Table.Th>Source name</Table.Th>
            <Table.Th>Resolution</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {effective.members.map((member) => (
            <Table.Tr key={member.entryKey}>
              <Table.Td>{member.ordinal}</Table.Td>
              <Table.Td>{member.name}</Table.Td>
              <Table.Td>{member.issue ? issueLabel(member.issue) : 'Resolved'}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
      {editing && (
        <Stack gap="xs">
          <TextInput
            label="Set name"
            value={name}
            onChange={(e) => setName(e.currentTarget.value)}
          />
          <NumberInput
            label="Expected disc count (blank means unknown)"
            allowDecimal={false}
            min={1}
            max={99}
            value={total}
            onChange={setTotal}
          />
          <Text size="sm">
            Use exact DAT entry names from this catalog source. Removing a row excludes it from this
            set. Up/down sets a contiguous order; ordinals can also preserve known gaps.
          </Text>
          {members.map((member, index) => (
            <Stack
              key={`${index}:${members.length}`}
              gap={4}
            >
              <div className={classes.discMemberFields}>
                <NumberInput
                  aria-label={`Ordinal for member ${index + 1}`}
                  allowDecimal={false}
                  min={1}
                  max={99}
                  value={member.ordinal}
                  onChange={(value) =>
                    setMembers(
                      members.map((m, i) =>
                        i === index
                          ? {
                              ...m,
                              ordinal: Number(value),
                            }
                          : m,
                      ),
                    )
                  }
                />
                <TextInput
                  aria-label={`Source entry for member ${index + 1}`}
                  value={member.entryKey}
                  onChange={(e) => {
                    const value = e.currentTarget.value;
                    setMembers(
                      members.map((m, i) =>
                        i === index
                          ? {
                              ...m,
                              entryKey: value,
                            }
                          : m,
                      ),
                    );
                  }}
                />
              </div>
              <Group
                gap={4}
                justify="flex-end"
              >
                <Button
                  size="compact-sm"
                  variant="subtle"
                  disabled={index === 0}
                  aria-label={`Move member ${index + 1} up`}
                  onClick={() => move(index, -1)}
                >
                  ↑
                </Button>
                <Button
                  variant="subtle"
                  disabled={index === members.length - 1}
                  size="compact-sm"
                  aria-label={`Move member ${index + 1} down`}
                  onClick={() => move(index, 1)}
                >
                  ↓
                </Button>
                <Button
                  variant="subtle"
                  color="red"
                  size="compact-sm"
                  aria-label={`Remove member ${index + 1}`}
                  onClick={() => setMembers(members.filter((_, i) => i !== index))}
                >
                  Remove
                </Button>
              </Group>
            </Stack>
          ))}
          <Button
            {...workspaceActionProps}
            variant="light"
            onClick={() =>
              setMembers([
                ...members,
                {
                  entryKey: '',
                  ordinal: members.length + 1,
                },
              ])
            }
            disabled={members.length >= 99}
          >
            Add disc
          </Button>
        </Stack>
      )}
      <TextInput
        label="Decision reason"
        value={reason}
        maxLength={1000}
        onChange={(e) => setReason(e.currentTarget.value)}
      />
      {mutation.isError && (
        <Alert color="red">
          {mutation.error.message}
          <Button
            variant="subtle"
            onClick={() =>
              void queryClient.invalidateQueries({
                queryKey: [
                  'discSets',
                ],
              })
            }
          >
            Reload
          </Button>
        </Alert>
      )}
      <Group>
        {editing ? (
          <Button
            {...workspaceActionProps}
            loading={mutation.isPending}
            onClick={() => mutation.mutate('correct')}
          >
            Save correction
          </Button>
        ) : (
          <Button
            {...workspaceActionProps}
            disabled={
              set.proposal.reviewReasons.length > 0 || set.proposal.members.some((m) => m.issue)
            }
            loading={mutation.isPending}
            onClick={() => mutation.mutate('accept')}
          >
            Accept proposal
          </Button>
        )}
        <Button
          {...workspaceActionProps}
          variant="light"
          onClick={() => setEditing(!editing)}
        >
          {editing ? 'Close correction' : 'Correct membership and order'}
        </Button>
        <Button
          color="red"
          variant="light"
          disabled={mutation.isPending}
          onClick={() => mutation.mutate('reject')}
        >
          Reject set
        </Button>
        <Button
          variant="subtle"
          disabled={mutation.isPending}
          onClick={() => mutation.mutate('reset')}
        >
          Reset to proposal
        </Button>
      </Group>
      {set.reason && (
        <Text
          size="xs"
          c="dimmed"
        >
          Last decision: {set.reason}
        </Text>
      )}
    </Stack>
  );
}
