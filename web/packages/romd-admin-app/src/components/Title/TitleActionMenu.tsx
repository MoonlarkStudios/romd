import { ActionIcon, Button, Card, Group, Menu, Modal, Select, Stack, Text } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import {
  IconAlertTriangle,
  IconArrowsMoveVertical,
  IconArrowsSplit,
  IconDotsVertical,
} from '@tabler/icons-react';
import { useState } from 'react';
import { useNavigate } from 'react-router';
import { TitleConflictError } from '../../hooks/api/titleConflict';
import { useMergeTitles, useMoveGame } from '../../hooks/api/useTitleActions';
import type {
  TitleDetailView as TitleDetail,
  TitleReleaseView as TitleRelease,
} from '../../hooks/api/useTitleDetail';
import { useTitleDetail } from '../../hooks/api/useTitleDetail';
import { usePermissions } from '../../hooks/usePermissions';
import { TitleSearchSelect } from '../TitleSearchSelect';

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
      <Menu
        shadow="md"
        width={210}
        position="bottom-end"
      >
        <Menu.Target>
          <ActionIcon
            variant="subtle"
            color="gray"
            size="lg"
            aria-label="Title actions"
          >
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
        <TitleMergeForm
          titleId={titleId}
          currentTitle={title}
          onClose={mergeDisclosure.close}
        />
      </Modal>

      <Modal
        opened={moveOpened}
        onClose={moveDisclosure.close}
        title="Move Release"
        centered
      >
        <MoveReleaseForm
          titleId={titleId}
          releases={releases}
          onClose={moveDisclosure.close}
        />
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
  const { data: loaded, isError: targetFailed } = useTitleDetail(targetTitleId ?? undefined);
  // The merge is checked against the target as the form first showed it, so a background refetch cannot swap
  // the revision behind the curator; only a conflict, which reloads it, shows it again.
  const [shown, setShown] = useState<TitleDetail | null>(null);
  const target = shown?.id === targetTitleId ? shown : null;
  if (!target && loaded && loaded.id === targetTitleId) setShown(loaded);

  const handleMerge = async () => {
    if (!target) return;

    try {
      await mergeTitles.mutateAsync({
        sourceTitleId: titleId,
        sourceRevision: currentTitle.revision,
        targetTitleId: target.id,
        targetRevision: target.revision,
      });
      notifications.show({
        title: 'Titles merged',
        message: `"${currentTitle.name}" has been merged into "${target.name}".`,
        color: 'green',
      });
      onClose();
      navigate(`/titles/${target.id}`);
    } catch (reason) {
      if (reason instanceof TitleConflictError) setShown(null);
      notifications.show(
        reason instanceof TitleConflictError
          ? {
              title: 'Titles changed',
              message: 'Both titles were reloaded. Review them, then merge again.',
              color: 'yellow',
            }
          : {
              title: 'Merge failed',
              message: 'Could not merge the titles.',
              color: 'red',
            },
      );
    }
  };

  return (
    <Stack gap="md">
      <Card
        withBorder
        padding="sm"
        bg="red.0"
      >
        <Group
          gap="xs"
          align="flex-start"
          wrap="nowrap"
        >
          <IconAlertTriangle
            size={16}
            color="var(--mantine-color-red-7)"
          />
          <Text
            size="xs"
            c="red"
            fw={500}
          >
            {`This moves all releases and media to the selected title, then deletes "${currentTitle.name}".`}
          </Text>
        </Group>
      </Card>

      <TitleSearchSelect
        label="Merge into"
        placeholder="Search for target title..."
        value={targetTitleId}
        onChange={(id) => setTargetTitleId(id)}
        excludeIds={[
          titleId,
        ]}
      />
      {target && (
        <Text
          size="sm"
          c="dimmed"
        >
          {`"${target.name}" has ${releaseCount(target)}.`}
        </Text>
      )}
      {targetFailed && (
        <Text
          size="sm"
          c="red"
        >
          Could not load the selected title.
        </Text>
      )}

      <Group justify="flex-end">
        <Button
          variant="default"
          onClick={onClose}
        >
          Cancel
        </Button>
        <Button
          color="red"
          onClick={handleMerge}
          loading={mergeTitles.isPending}
          disabled={!target || targetFailed}
        >
          Merge
        </Button>
      </Group>
    </Stack>
  );
}

function releaseCount(title: TitleDetail): string {
  const count = title.releases?.length ?? 0;
  return `${count} ${count === 1 ? 'release' : 'releases'}`;
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
        titleId,
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
      <Text
        size="sm"
        c="dimmed"
      >
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
        excludeIds={[
          titleId,
        ]}
      />
      <Group justify="flex-end">
        <Button
          variant="default"
          onClick={onClose}
        >
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
