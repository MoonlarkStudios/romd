import { AppShell } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { romdLayout } from '@romd/foundation';
import { Suspense } from 'react';
import { Outlet } from 'react-router';
import { useDashboardStatusBar } from '../../hooks/api/useDashboardStatusBar';
import { JobSocketProvider } from '../../hooks/realtime/JobSocketContext';
import { SystemSocketProvider } from '../../hooks/realtime/SystemSocketContext';
import { DraftGuardProvider } from '../Administration/DraftGuard';
import { LoadingState } from '../AsyncBoundary/LoadingState';
import { SpotlightSearch } from '../SpotlightSearch';
import { StatusBar } from '../StatusBar/StatusBar';
import { Header } from './Header';
import { Navbar } from './Navbar';

const STATUS_BAR_HEIGHT = 32;

export function AppLayout() {
  const [opened, { toggle, close }] = useDisclosure();
  const { data: statusBar, isLoading, isError } = useDashboardStatusBar();

  return (
    <JobSocketProvider>
      <SystemSocketProvider>
        <SpotlightSearch />
        <AppShell
          style={{
            '--romd-admin-status-bar-height': `${STATUS_BAR_HEIGHT}px`,
          }}
          header={{
            height: romdLayout.headerHeight,
          }}
          navbar={{
            width: 250,
            breakpoint: 'sm',
            collapsed: {
              mobile: !opened,
            },
          }}
          padding="md"
        >
          <AppShell.Header>
            <Header
              opened={opened}
              toggle={toggle}
            />
          </AppShell.Header>

          <AppShell.Navbar>
            <Navbar onNavigate={close} />
          </AppShell.Navbar>

          <AppShell.Main
            style={{
              display: 'flex',
              flexDirection: 'column',
              minHeight: 0,
              paddingBottom: `calc(var(--mantine-spacing-md) + ${STATUS_BAR_HEIGHT}px)`,
            }}
          >
            <DraftGuardProvider>
              <Suspense fallback={<LoadingState />}>
                <Outlet />
              </Suspense>
            </DraftGuardProvider>
          </AppShell.Main>
        </AppShell>

        <StatusBar
          health={statusBar?.health}
          coverage={statusBar?.coverage}
          storage={statusBar?.storage}
          isLoading={isLoading}
          isError={isError}
        />
      </SystemSocketProvider>
    </JobSocketProvider>
  );
}
