import { Button, Card, Center, Loader, Stack, Text, Title } from '@mantine/core';
import { useState } from 'react';
import { Navigate } from 'react-router';
import { useAuth } from '../contexts/AuthContext';

export function Login() {
  const { login, isAuthenticated, isLoading } = useAuth();
  const [isRedirecting, setIsRedirecting] = useState(false);

  if (isLoading) {
    return (
      <Center h="100vh">
        <Loader size="lg" />
      </Center>
    );
  }

  if (isAuthenticated) {
    return (
      <Navigate
        to="/"
        replace
      />
    );
  }

  const handleSignIn = () => {
    setIsRedirecting(true);
    void login();
  };

  return (
    <Center
      h="100vh"
      bg="var(--mantine-color-body)"
    >
      <Card
        shadow="md"
        padding="xl"
        radius="md"
        w={400}
        withBorder
      >
        <Stack gap="md">
          <Title
            order={2}
            ta="center"
          >
            ROMD Admin
          </Title>
          <Text
            c="dimmed"
            size="sm"
            ta="center"
          >
            Sign in to continue
          </Text>

          <Button
            fullWidth
            loading={isRedirecting}
            onClick={handleSignIn}
            mt="md"
          >
            Sign In
          </Button>
        </Stack>
      </Card>
    </Center>
  );
}
