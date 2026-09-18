import { Badge, Group, Stack, Text, Title } from '@mantine/core';
import { AccountSecurity } from '../components/Account/AccountSecurity';
import { MySessions } from '../components/Account/MySessions';
import classes from '../components/Workspace/Workspace.module.css';
import { useAuth } from '../contexts/AuthContext';

export function Account() {
  const { user } = useAuth();
  return <Stack className={classes.page} gap="lg">
    <div className={classes.hero}><Title order={1} className={classes.heading}>My account</Title></div>
    <section className={classes.panel}><Text fw={600}>{user?.email}</Text><Group mt="sm">{user?.roles.map(role => <Badge key={role} color="gray" variant="light">{role}</Badge>)}</Group></section>
    <section className={classes.panel}><Title order={2} className={classes.sectionHeading} mb="md">Password</Title><AccountSecurity /></section>
    <MySessions />
  </Stack>;
}
