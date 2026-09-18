import { Alert, Button, Card, Center, Loader, Stack, Text, Title } from '@mantine/core';
import { IconInfoCircle } from '@tabler/icons-react';
import { useState } from 'react';
import { Navigate, useLocation } from 'react-router';
import { useAuth } from '../contexts/AuthContext';

export function Login() {
  const { login, isAuthenticated, isLoading } = useAuth();
  const location = useLocation();
  const [isRedirecting, setIsRedirecting] = useState(false);
  const [redirectError, setRedirectError] = useState<string | null>(null);

  // Surfaced when the auth callback bounces back here after a failed exchange.
  const callbackError = (location.state as { error?: string } | null)?.error ?? null;
  const error = redirectError ?? callbackError;

  if (isLoading) {
    return (
      <Center h="100vh">
        <Loader
          size="lg"
          color="mint"
        />
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

  const handleSignIn = async () => {
    setIsRedirecting(true);
    setRedirectError(null);

    try {
      await login();
    } catch {
      setIsRedirecting(false);
      setRedirectError('The sign-in service could not be reached. Try again.');
    }
  };

  return (
    <Center
      mih="100vh"
      p="md"
    >
      <Card
        shadow="md"
        padding="xl"
        w="min(100%, 420px)"
        withBorder
      >
        <Stack gap="md">
          <Stack
            gap={4}
            ta="center"
          >
            <Title
              order={2}
              className="romd-page-heading"
            >
              ROMD
            </Title>
            <Text
              c="dimmed"
              size="sm"
            >
              Sign in to your library
            </Text>
          </Stack>

          {error && (
            <Alert
              color="red"
              icon={<IconInfoCircle size={18} />}
            >
              {error}
            </Alert>
          )}

          <Button
            fullWidth
            color="mint"
            loading={isRedirecting}
            onClick={handleSignIn}
          >
            Sign in
          </Button>
        </Stack>
      </Card>
    </Center>
  );
}
