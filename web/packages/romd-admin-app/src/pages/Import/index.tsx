import { Alert, Button, Divider, Group, Stack, Title } from '@mantine/core';
import { IconPlus } from '@tabler/icons-react';
import { useEffect, useState } from 'react';
import workspace from '../../components/Workspace/Workspace.module.css';
import { ImportJobCard } from './ImportJobCard';
import { RecentImports } from './RecentImports';
import { StagePanel } from './StagePanel';
import { useImportSession } from './useImportSession';

export function ImportPage({ embedded = false }: { embedded?: boolean }) {
  const session = useImportSession();
  const [addingFiles, setAddingFiles] = useState(false);
  useEffect(() => { if (session.isStarting) setAddingFiles(false); }, [session.isStarting]);
  const showStage = addingFiles || session.staged.length > 0 || session.isStarting || session.activeJobIds.length === 0;
  return <Stack gap="xl">
    {!embedded && <Title order={1} className={workspace.heading}>Import ROMs</Title>}
    {showStage ? <StagePanel {...session} /> : <Group justify="flex-end"><Button variant="default" leftSection={<IconPlus size={16} />} onClick={() => setAddingFiles(true)}>Import more files</Button></Group>}
    {session.batchError && <Alert color="red" title="Import could not be restored"><Button variant="subtle" onClick={session.retryBatch}>Retry import status</Button></Alert>}
    {session.activeJobIds.map((jobId) => <ImportJobCard key={jobId} jobId={jobId} onDismiss={() => session.dismissJob(jobId)} />)}
    <Divider />
    <RecentImports activeJobIds={session.activeJobIds} onReopen={session.reopenJob} />
  </Stack>;
}
