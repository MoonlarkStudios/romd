import {
  ActionIcon,
  Button,
  Card,
  Group,
  Menu,
  Modal,
  Select,
  Stack,
  Text,
} from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import type { TitleDetail, TitleRelease } from '@romd/admin-api-client';
import {
  IconAlertTriangle,
  IconArrowsMoveVertical,
  IconArrowsSplit,
  IconDotsVertical,
} from '@tabler/icons-react';
import { useState } from 'react';
import { useNavigate } from 'react-router';
import { TitleSearchSelect } from '../../components/TitleSearchSelect';
import { useMergeTitles, useMoveGame } from '../../hooks/api/useTitleActions';
import { usePermissions } from '../../hooks/usePermissions';

interface TitleActionMenuProps {
  title: TitleDetail;
  titleId: string;
}

export function TitleActionMenu({ title, titleId }: TitleActionMenuProps) {
  const { canManageTitles } = usePermissions();
  const [mergeOpened, mergeDisclosure] = useDisclosure(false);
  const [moveOpened, moveDisclosure] = useDisclosure(false);

  if (!canManageTitles) {
    return null;
  }

  const releases = title.releases ?? [];

  return (
    <>
      <Menu shadow="md" width={210} position="bottom-end">
        <Menu.Target>
          <ActionIcon variant="subtle" color="gray" size="lg" aria-label="Title actions">
            <IconDotsVertical size={18} />
          </ActionIcon>
        </Menu.Target>
        <Menu.Dropdown>
          <Menu.Label>Curation</Menu.Label>
          <Menu.Item
            leftSection={<IconArrowsMoveVertical size={14} />}
            disabled={releases.length === 0}
            onClick={moveDisclosure.open}
          >
            Move release...
          </Menu.Item>
          <Menu.Divider />
          <Menu.Item
            color="red"
            leftSection={<IconArrowsSplit size={14} />}
            onClick={mergeDisclosure.open}
          >
            Merge title...
          </Menu.Item>
        </Menu.Dropdown>
      </Menu>

      <Modal
        opened={mergeOpened}
        onClose={mergeDisclosure.close}
        title="Merge Title"
        centered
      >
        <TitleMergeForm titleId={titleId} currentTitle={title} onClose={mergeDisclosure.close} />
      </Modal>

      <Modal
        opened={moveOpened}
        onClose={moveDisclosure.close}
        title="Move Release"
        centered
      >
        <MoveReleaseForm titleId={titleId} releases={releases} onClose={moveDisclosure.close} />
      </Modal>
    </>
  );
}

interface TitleMergeFormProps {
  titleId: string;
  currentTitle: TitleDetail;
  onClose: () => void;
}

function TitleMergeForm({ titleId, currentTitle, onClose }: TitleMergeFormProps) {
  const navigate = useNavigate();
  const mergeTitles = useMergeTitles();
  const [targetTitleId, setTargetTitleId] = useState<string | null>(null);
  const [targetTitleName, setTargetTitleName] = useState<string | null>(null);

  const handleMerge = async () => {
    if (!targetTitleId) return;

    try {
      await mergeTitles.mutateAsync({
        sourceTitleId: titleId,
        targetTitleId,
      });
      notifications.show({
        title: 'Titles merged',
        message: `"${currentTitle.name}" has been merged into "${targetTitleName}".`,
        color: 'green',
      });
      onClose();
      navigate(`/titles/${targetTitleId}`);
    } catch {
      notifications.show({
        title: 'Merge failed',
        message: 'Could not merge the titles.',
        color: 'red',
      });
    }
  };

  return (
    <Stack gap="md">
      <Card withBorder padding="sm" bg="red.0">
        <Group gap="xs" align="flex-start" wrap="nowrap">
          <IconAlertTriangle size={16} color="var(--mantine-color-red-7)" />
          <Text size="xs" c="red" fw={500}>
            {`This moves all releases and media to the selected title, then deletes "${currentTitle.name}".`}
          </Text>
        </Group>
      </Card>

      <TitleSearchSelect
        label="Merge into"
        placeholder="Search for target title..."
        value={targetTitleId}
        onChange={(id, title) => {
          setTargetTitleId(id);
          setTargetTitleName(title?.name ?? null);
        }}
        excludeIds={[titleId]}
      />

      <Group justify="flex-end">
        <Button variant="default" onClick={onClose}>
          Cancel
        </Button>
        <Button
          color="red"
          onClick={handleMerge}
          loading={mergeTitles.isPending}
          disabled={!targetTitleId}
        >
          Merge
        </Button>
      </Group>
    </Stack>
  );
}

interface MoveReleaseFormProps {
  titleId: string;
  releases: TitleRelease[];
  onClose: () => void;
}

function MoveReleaseForm({ titleId, releases, onClose }: MoveReleaseFormProps) {
  const moveGame = useMoveGame();
  const [selectedGameId, setSelectedGameId] = useState<string | null>(null);
  const [targetTitleId, setTargetTitleId] = useState<string | null>(null);
  const [targetTitleName, setTargetTitleName] = useState<string | null>(null);

  const releaseOptions = releases.map((release) => ({
    value: release.id,
    label: release.name,
  }));

  const handleMove = async () => {
    if (!selectedGameId || !targetTitleId) return;

    try {
      await moveGame.mutateAsync({
        gameId: selectedGameId,
        targetTitleId,
      });
      notifications.show({
        title: 'Release moved',
        message: `Release has been moved to "${targetTitleName}".`,
        color: 'green',
      });
      setSelectedGameId(null);
      setTargetTitleId(null);
      setTargetTitleName(null);
      onClose();
    } catch {
      notifications.show({
        title: 'Move failed',
        message: 'Could not move the release.',
        color: 'red',
      });
    }
  };

  if (releases.length === 0) {
    return (
      <Text size="sm" c="dimmed">
        No releases are available to move.
      </Text>
    );
  }

  return (
    <Stack gap="md">
      <Select
        label="Release"
        placeholder="Choose a release..."
        data={releaseOptions}
        value={selectedGameId}
        onChange={setSelectedGameId}
        searchable
      />
      <TitleSearchSelect
        label="Move to"
        placeholder="Search for target title..."
        value={targetTitleId}
        onChange={(id, title) => {
          setTargetTitleId(id);
          setTargetTitleName(title?.name ?? null);
        }}
        excludeIds={[titleId]}
      />
      <Group justify="flex-end">
        <Button variant="default" onClick={onClose}>
          Cancel
        </Button>
        <Button
          onClick={handleMove}
          loading={moveGame.isPending}
          disabled={!selectedGameId || !targetTitleId}
        >
          Move
        </Button>
      </Group>
    </Stack>
  );
}
