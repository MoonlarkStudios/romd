import { Alert, Button, Group, Skeleton, Stack, Text, Title, UnstyledButton } from '@mantine/core';
import { IconChevronRight, IconHistory } from '@tabler/icons-react';
import { useMemo } from 'react';
import { useNavigate } from 'react-router';
import { EmptyState } from '../../components/EmptyState';
import { JobStatusBadge } from '../../components/Jobs/JobStatusBadge';
import {
  getJobTitle,
} from '../../components/Jobs/jobTypeRegistry';
import workspace from '../../components/Workspace/Workspace.module.css';
import { useJobs } from '../../hooks/api/useJobs';
import { formatRelativeTime } from '../../utils/format';

export function RecentImports({
  activeJobIds,
  onReopen,
}: {
  activeJobIds: string[];
  onReopen: (jobId: string) => void;
}) {
  const navigate = useNavigate();
  const jobsQuery = useJobs();

  const recent = useMemo(() => {
    const active = new Set(activeJobIds);
    return (jobsQuery.data ?? [])
      .filter((job) => job.jobType === 'upload' && !active.has(job.id))
      .slice(0, 6);
  }, [jobsQuery.data, activeJobIds]);

  return (
    <section>
      <Stack gap="md">
        <Group justify="space-between" align="center">
          <Title order={2} className={workspace.sectionHeading}>
            Recent imports
          </Title>
          <UnstyledButton onClick={() => navigate('/jobs')}>
            <Text size="xs" c="blue">
              View all
            </Text>
          </UnstyledButton>
        </Group>

        {jobsQuery.isError ? <Alert color="red" title="Could not load recent imports"><Button variant="subtle" onClick={() => void jobsQuery.refetch()}>Retry history</Button></Alert> : jobsQuery.isPending ? <Skeleton height={80} /> : recent.length > 0 ? (
          <Stack gap={4}>
            {recent.map((job) => {
              return (
                <UnstyledButton
                  key={job.id}
                  onClick={() => onReopen(job.id)}
                  style={{
                    padding: 'var(--mantine-spacing-xs) var(--mantine-spacing-sm)',
                    borderRadius: 'var(--mantine-radius-sm)',
                  }}
                >
                  <Group justify="space-between" gap="sm" wrap="nowrap">
                    <Stack gap={0} style={{ minWidth: 0, flex: 1 }}>
                      <Text size="sm" truncate>
                        {getJobTitle(job)}
                      </Text>
                      <Text size="xs" c="dimmed">
                        {formatRelativeTime(job.completedAt ?? job.createdAt ?? new Date())}
                      </Text>
                    </Stack>
                    <Group gap={4} wrap="nowrap" style={{ flexShrink: 0 }}>
                      <JobStatusBadge job={job} />
                      <IconChevronRight size={16} color="var(--mantine-color-dimmed)" />
                    </Group>
                  </Group>
                </UnstyledButton>
              );
            })}
          </Stack>
        ) : (
          <EmptyState
            size="sm"
            icon={<IconHistory size={24} />}
            title="No imports yet"
            description="Imports you run will appear here."
          />
        )}
      </Stack>
    </section>
  );
}
