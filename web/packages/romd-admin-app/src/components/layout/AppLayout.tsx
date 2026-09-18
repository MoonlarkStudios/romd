import { AppShell } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { Outlet } from 'react-router';
import { useCoverageStats } from '../../hooks/api/useCoverageStats';
import { useHealthStats } from '../../hooks/api/useHealthStats';
import { useStorageStats } from '../../hooks/api/useStorageStats';
import { JobSocketProvider } from '../../hooks/realtime/JobSocketContext';
import { SystemSocketProvider } from '../../hooks/realtime/SystemSocketContext';
import { SpotlightSearch } from '../SpotlightSearch';
import { StatusBar } from '../StatusBar/StatusBar';
import { Header } from './Header';
import { Navbar } from './Navbar';

const STATUS_BAR_HEIGHT = 32;

export function AppLayout() {
    const [opened, { toggle, close }] = useDisclosure();
    const { data: health, isLoading: healthLoading } = useHealthStats();
    const { data: coverage, isLoading: coverageLoading } = useCoverageStats();
    const { data: storage, isLoading: storageLoading } = useStorageStats();

    const isLoading = healthLoading || coverageLoading || storageLoading;

    return (
        <JobSocketProvider>
            <SystemSocketProvider>
                <SpotlightSearch />
                <AppShell
                    header={{
                        height: 60,
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
                        <Header opened={opened} toggle={toggle} />
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
                        <Outlet />
                    </AppShell.Main>
                </AppShell>

                <StatusBar
                    health={health}
                    coverage={coverage}
                    storage={storage}
                    isLoading={isLoading}
                />
            </SystemSocketProvider>
        </JobSocketProvider>
    );
}
