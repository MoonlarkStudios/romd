import { Button, Group, PasswordInput, Stack } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconKey } from '@tabler/icons-react';
import { useState } from 'react';
import { useChangePassword } from '../../hooks/api/useUsers';

export function AccountSecurity() {
  const changePasswordMutation = useChangePassword();
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');

  const handleChangePassword = async () => {
    if (!currentPassword || !newPassword) {
      notifications.show({
        title: 'Missing fields',
        message: 'Current password and new password are required.',
        color: 'red',
      });
      return;
    }

    if (newPassword !== confirmPassword) {
      notifications.show({
        title: 'Passwords do not match',
        message: 'Confirm the new password before saving.',
        color: 'red',
      });
      return;
    }

    try {
      await changePasswordMutation.mutateAsync({
        currentPassword,
        newPassword,
      });
      setCurrentPassword('');
      setNewPassword('');
      setConfirmPassword('');
      notifications.show({
        title: 'Password changed',
        message: 'Use the new password on your next sign-in.',
        color: 'green',
      });
    } catch (error) {
      notifications.show({
        title: 'Password change failed',
        message: error instanceof Error ? error.message : 'Could not change your password.',
        color: 'red',
      });
    }
  };

  return (
    <Stack gap="md">
      <PasswordInput
        label="Current password"
        value={currentPassword}
        onChange={(event) => setCurrentPassword(event.currentTarget.value)}
        autoComplete="current-password"
      />
      <PasswordInput
        label="New password"
        value={newPassword}
        onChange={(event) => setNewPassword(event.currentTarget.value)}
        autoComplete="new-password"
      />
      <PasswordInput
        label="Confirm new password"
        value={confirmPassword}
        onChange={(event) => setConfirmPassword(event.currentTarget.value)}
        autoComplete="new-password"
      />
      <Group justify="flex-end">
        <Button
          leftSection={<IconKey size={16} />}
          onClick={handleChangePassword}
          loading={changePasswordMutation.isPending}
        >
          Change Password
        </Button>
      </Group>
    </Stack>
  );
}

