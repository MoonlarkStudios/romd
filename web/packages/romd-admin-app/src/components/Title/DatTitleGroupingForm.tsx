import { Alert, Badge, Button, Group, List, Select, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { getDatTitleGrouping } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useNavigate } from 'react-router';
import { useMergeTitles } from '../../hooks/api/useTitleActions';
import type { TitleDetailView } from '../../hooks/api/useTitleDetail';

interface DatTitleGroupingFormProps {
  title: TitleDetailView;
  onClose: () => void;
}

export function DatTitleGroupingForm({ title, onClose }: DatTitleGroupingFormProps) {
  const navigate = useNavigate();
  const merge = useMergeTitles();
  const [keepTitleId, setKeepTitleId] = useState(title.id);
  const [applyFailed, setApplyFailed] = useState(false);
  const review = useQuery({
    queryKey: [
      'dat-title-grouping',
      title.id,
    ],
    refetchOnMount: 'always',
    refetchOnWindowFocus: false,
    queryFn: async ({ signal }) => {
      const response = await getDatTitleGrouping({
        path: {
          titleId: title.id,
        },
        signal,
      });
      if (response.error || !response.data) throw new Error('Could not load the DAT family.');
      return response.data;
    },
  });
  const candidate = review.data?.suggestion;
  const keepClone = keepTitleId === title.id;

  async function apply() {
    if (!candidate) return;
    const targetTitleId = keepClone ? title.id : candidate.parentTitleId;
    try {
      await merge.mutateAsync({
        sourceTitleId: keepClone ? candidate.parentTitleId : title.id,
        sourceRevision: keepClone ? candidate.parentTitleRevision : candidate.cloneTitleRevision,
        targetTitleId,
        targetRevision: keepClone ? candidate.cloneTitleRevision : candidate.parentTitleRevision,
        datGroupingEvidence: candidate.evidence,
      });
      notifications.show({
        title: 'DAT titles grouped',
        message: 'The catalog is updating.',
        color: 'green',
      });
      onClose();
      navigate(`/titles/${targetTitleId}`);
    } catch {
      setApplyFailed(true);
      setKeepTitleId(title.id);
      await review.refetch();
    }
  }

  if (review.isFetching) return <Text role="status">Checking the active DAT family…</Text>;
  if (review.isError) {
    return (
      <Stack>
        <Alert color="red">Could not load the DAT family.</Alert>
        <Button
          variant="light"
          onClick={() => review.refetch()}
        >
          Retry
        </Button>
      </Stack>
    );
  }
  if (!candidate) return <Text>No separate title was found in this hash-verified DAT family.</Text>;

  return (
    <Stack gap="md">
      {applyFailed && (
        <Alert color="yellow">
          Grouping could not be saved. Review the current titles and DAT evidence before trying
          again.
        </Alert>
      )}
      <Group gap="xs">
        <Badge variant="light">Hash verified</Badge>
        <Text size="sm">
          {[
            candidate.datName,
            candidate.datVersion,
          ]
            .filter(Boolean)
            .join(' · ')}
        </Text>
      </Group>
      <List
        size="sm"
        spacing={4}
        aria-label="Verified DAT releases"
      >
        {candidate.releaseNames.map((name) => (
          <List.Item key={name}>{name}</List.Item>
        ))}
      </List>
      <Select
        label="Title to keep"
        description="The selected name appears on the catalog card."
        allowDeselect={false}
        value={keepTitleId}
        onChange={(id) => setKeepTitleId(id ?? title.id)}
        data={[
          {
            value: title.id,
            label: title.name,
          },
          {
            value: candidate.parentTitleId,
            label: candidate.parentTitleName,
          },
        ]}
      />
      <Text size="sm">
        Title ID to keep{' '}
        <Text
          span
          fw={600}
        >
          {keepTitleId}
        </Text>
      </Text>
      <Alert
        color="yellow"
        title="What changes"
      >
        <List
          size="sm"
          spacing={4}
        >
          <List.Item>
            All releases move to the selected title and keep their IDs and DAT names.
          </List.Item>
          <List.Item>
            Play history and collection entries follow the releases. Duplicate collection entries
            keep the selected title’s position and note.
          </List.Item>
          <List.Item>
            Artwork and missing provider links are carried over. Existing links and artwork
            selections on the selected title take precedence.
          </List.Item>
          <List.Item>
            The selected title keeps its release pin. A pin on the absorbed title is cleared.
          </List.Item>
          <List.Item>
            Check libraries that explicitly select the title being absorbed. Their selections use
            title IDs.
          </List.Item>
        </List>
      </Alert>
      <Group justify="flex-end">
        <Button
          variant="default"
          onClick={onClose}
        >
          Cancel
        </Button>
        <Button
          onClick={apply}
          loading={merge.isPending}
        >
          Group titles
        </Button>
      </Group>
    </Stack>
  );
}
