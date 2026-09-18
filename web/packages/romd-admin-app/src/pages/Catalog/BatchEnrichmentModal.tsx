import {
  Alert,
  Button,
  Group,
  Modal,
  Progress,
  Select,
  Stack,
  Text,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type { CatalogTitle } from '@romd/admin-api-client';
import { IconAlertCircle, IconSparkles } from '@tabler/icons-react';
import { useCallback, useState } from 'react';
import { useManagedSystems } from '../../hooks/api/useManagedSystems';
import { useTriggerEnrichment } from '../../hooks/api/useTitleActions';

interface BatchEnrichmentModalProps {
  opened: boolean;
  onClose: () => void;
  eligibleTitles: CatalogTitle[];
}

/**
 * Modal for triggering batch enrichment on multiple titles.
 * Processes titles sequentially to avoid overwhelming the API.
 */
export function BatchEnrichmentModal({
  opened,
  onClose,
  eligibleTitles,
}: BatchEnrichmentModalProps) {
  const { data: systems } = useManagedSystems();
  const triggerEnrichment = useTriggerEnrichment();

  const [platformFilter, setPlatformFilter] = useState<string | null>(null);
  const [isProcessing, setIsProcessing] = useState(false);
  const [progress, setProgress] = useState({ current: 0, total: 0, failed: 0 });
  const [shouldCancel, setShouldCancel] = useState(false);

  // Filter eligible titles by platform if selected
  // Note: CatalogTitle doesn't include enrichmentStatus, so we process all owned titles
  // The catalog can be pre-filtered by enrichmentStatus before opening this modal
  const enrichableTitles = platformFilter
    ? eligibleTitles.filter((t) => t.systemKey === platformFilter)
    : eligibleTitles;

  const platformOptions =
    systems?.filter((system) => system.enabled).map((p) => ({
      value: p.key,
      label: p.name,
    })) ?? [];

  const handleStartBatch = useCallback(async () => {
    if (enrichableTitles.length === 0) return;

    setIsProcessing(true);
    setShouldCancel(false);
    setProgress({ current: 0, total: enrichableTitles.length, failed: 0 });

    let failed = 0;

    for (let i = 0; i < enrichableTitles.length; i++) {
      if (shouldCancel) break;

      const title = enrichableTitles[i];
      try {
        await triggerEnrichment.mutateAsync(title.id);
      } catch {
        failed++;
      }

      setProgress((prev) => ({
        ...prev,
        current: i + 1,
        failed,
      }));

      // Small delay between requests to avoid rate limiting
      if (i < enrichableTitles.length - 1) {
        await new Promise((resolve) => setTimeout(resolve, 500));
      }
    }

    setIsProcessing(false);
    notifications.show({
      title: 'Batch enrichment complete',
      message: `Triggered enrichment for ${progress.current - failed} titles. ${failed > 0 ? `${failed} failed.` : ''}`,
      color: failed > 0 ? 'orange' : 'green',
    });
  }, [enrichableTitles, shouldCancel, triggerEnrichment, progress.current]);

  const handleCancel = () => {
    setShouldCancel(true);
  };

  const handleClose = () => {
    if (!isProcessing) {
      setProgress({ current: 0, total: 0, failed: 0 });
      setPlatformFilter(null);
      onClose();
    }
  };

  const progressPercent =
    progress.total > 0 ? Math.round((progress.current / progress.total) * 100) : 0;

  return (
    <Modal
      opened={opened}
      onClose={handleClose}
      title={
        <Group gap="xs">
          <IconSparkles size={20} />
          <Text fw={500}>Batch Enrichment</Text>
        </Group>
      }
      size="md"
      closeOnClickOutside={!isProcessing}
      closeOnEscape={!isProcessing}
    >
      <Stack gap="md">
        {!isProcessing ? (
          <>
            <Text size="sm" c="dimmed">
              Trigger metadata enrichment for multiple titles at once. This will queue IGDB
              lookups for each title.
            </Text>

            <Select
              label="Filter by System (optional)"
              placeholder="All systems"
              data={platformOptions}
              value={platformFilter}
              onChange={setPlatformFilter}
              clearable
              searchable
            />

            <Alert
              color="blue"
              icon={<IconAlertCircle size={16} />}
              title={`${enrichableTitles.length} titles eligible`}
            >
              {enrichableTitles.length === 0 ? (
                <Text size="sm">No titles found that need enrichment.</Text>
              ) : (
                <Text size="sm">
                  This will trigger enrichment for {enrichableTitles.length} titles that are not
                  yet enriched or previously failed.
                </Text>
              )}
            </Alert>

            <Group justify="flex-end">
              <Button variant="light" onClick={handleClose}>
                Cancel
              </Button>
              <Button
                leftSection={<IconSparkles size={16} />}
                onClick={handleStartBatch}
                disabled={enrichableTitles.length === 0}
              >
                Start Enrichment
              </Button>
            </Group>
          </>
        ) : (
          <>
            <Text size="sm" c="dimmed">
              Processing enrichment requests...
            </Text>

            <Progress value={progressPercent} size="xl" radius="xl" striped animated />

            <Group justify="space-between">
              <Text size="sm">
                {progress.current} / {progress.total} completed
              </Text>
              {progress.failed > 0 && (
                <Text size="sm" c="red">
                  {progress.failed} failed
                </Text>
              )}
            </Group>

            <Group justify="flex-end">
              <Button variant="light" color="red" onClick={handleCancel}>
                Cancel
              </Button>
            </Group>
          </>
        )}
      </Stack>
    </Modal>
  );
}
