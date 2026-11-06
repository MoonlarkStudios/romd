import {
  Alert,
  Button,
  Checkbox,
  Group,
  NumberInput,
  Select,
  Skeleton,
  Stack,
  Table,
  Text,
  Title,
} from '@mantine/core';
import type { StartMediaReconciliationSelectionRequest } from '@romd/admin-api-client';
import { useRef, useState } from 'react';
import { useNavigate } from 'react-router';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import {
  useMediaReconciliationPolicies,
  useStartMediaSelection,
} from '../../hooks/api/useMediaReconciliation';
import { createRequestId } from '../../utils/requestId';

const MAXIMUM_SUBJECTS = 3 * 2147483647;
const FILTERS = [
  {
    value: 'AllSubjects',
    label: 'All media',
  },
  {
    value: 'KindSubjects',
    label: 'One media kind',
  },
  {
    value: 'RetainedRepresentations',
    label: 'Verify retained representations',
  },
];

export function MediaReconciliation() {
  const policies = useMediaReconciliationPolicies();
  const start = useStartMediaSelection();
  const navigate = useNavigate();
  const [filter, setFilter] = useState('AllSubjects');
  const [kindId, setKind] = useState<string | null>(null);
  const [limit, setLimit] = useState<number | string>(100);
  const [conversion, setConversion] = useState(false);
  const submitting = useRef(false);
  const acceptedRequest = useRef<{
    signature: string;
    request: StartMediaReconciliationSelectionRequest;
  } | null>(null);
  const verifyOnly = filter === 'RetainedRepresentations';
  const valid =
    Number.isSafeInteger(Number(limit)) &&
    Number(limit) > 0 &&
    Number(limit) <= MAXIMUM_SUBJECTS &&
    (filter !== 'KindSubjects' || policies.data?.some((policy) => policy.kindId === kindId));
  const submit = async () => {
    if (submitting.current || start.isPending || !valid) return;
    submitting.current = true;
    const scope = {
      filter,
      kindId: filter === 'KindSubjects' ? kindId : null,
      systemKey: null,
      limit: Number(limit),
      conversionRequested: !verifyOnly && conversion,
    };
    const signature = JSON.stringify(scope);
    if (acceptedRequest.current?.signature !== signature)
      acceptedRequest.current = {
        signature,
        request: {
          ...scope,
          requestId: createRequestId(),
        },
      };
    try {
      const reference = await start.mutateAsync(acceptedRequest.current.request);
      navigate(`/jobs/${reference.jobId}`);
    } catch {
      // A failed response keeps the exact request identity for a deliberate retry.
    } finally {
      submitting.current = false;
    }
  };
  return (
    <Stack
      className={workspace.panel}
      gap="sm"
    >
      <Title
        order={2}
        className={workspace.sectionHeading}
      >
        Media reconciliation
      </Title>
      {policies.isPending && <Skeleton height={80} />}
      {policies.isError && (
        <Alert
          color="red"
          title="Media policies unavailable"
        >
          <Button
            {...workspaceActionProps}
            variant="light"
            onClick={() => void policies.refetch()}
          >
            Retry
          </Button>
        </Alert>
      )}
      {policies.data && (
        <>
          <Table aria-label="Current media policies">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Media kind</Table.Th>
                <Table.Th>Stored format</Table.Th>
                <Table.Th>Conversion</Table.Th>
                <Table.Th>Read here</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {policies.data.map((policy) => (
                <Table.Tr key={policy.kindId}>
                  <Table.Td>{policy.kindId.replaceAll('-', ' ')}</Table.Td>
                  <Table.Td>{policy.storedFormatLabel}</Table.Td>
                  <Table.Td>
                    {policy.conversionMode === 'OnRequest' ? 'On request' : policy.conversionMode}
                  </Table.Td>
                  <Table.Td>{policy.canDecode ? 'Available' : 'Unavailable'}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
          <Group align="flex-end">
            <Select
              label="Selection"
              data={FILTERS}
              value={filter}
              allowDeselect={false}
              onChange={(value) => {
                if (value) setFilter(value);
              }}
            />
            {filter === 'KindSubjects' && (
              <Select
                label="Media kind"
                value={kindId}
                onChange={setKind}
                data={policies.data.map((policy) => ({
                  value: policy.kindId,
                  label: policy.kindId.replaceAll('-', ' '),
                }))}
              />
            )}
            <NumberInput
              label="Maximum subjects"
              value={limit}
              onChange={setLimit}
              min={1}
              max={MAXIMUM_SUBJECTS}
              allowDecimal={false}
              allowNegative={false}
            />
          </Group>
          {!verifyOnly && (
            <Checkbox
              label="Request conversion for kinds that require it"
              checked={conversion}
              onChange={(event) => setConversion(event.currentTarget.checked)}
            />
          )}
          <Text
            size="sm"
            c="dimmed"
          >
            A selection captures the media present when submitted. Retrying keeps that finite scope.
            Automatic conversion follows each kind’s policy.
          </Text>
          <Group>
            <Button
              {...workspaceActionProps}
              loading={start.isPending}
              disabled={!valid}
              onClick={() => void submit()}
            >
              Start reconciliation
            </Button>
          </Group>
        </>
      )}
      {start.error && <Alert color="red">{start.error.message}</Alert>}
    </Stack>
  );
}
