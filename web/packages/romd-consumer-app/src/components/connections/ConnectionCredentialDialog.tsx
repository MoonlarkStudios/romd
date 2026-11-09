import { Button, CopyButton, Modal, Stack, Text, TextInput } from '@mantine/core';
import type { VfsConnectionCredential } from '@romd/consumer-api-client';

// Presentation only. The page owns the one-time secret and clears it when this closes.
export function ConnectionCredentialDialog({
  credential,
  onClose,
}: {
  credential: VfsConnectionCredential | null;
  onClose: () => void;
}) {
  function download() {
    if (!credential) return;
    const url = URL.createObjectURL(
      new Blob(
        [
          credential.rcloneConfig,
        ],
        {
          type: 'text/plain',
        },
      ),
    );
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = 'romd.conf';
    anchor.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  return (
    <Modal
      opened={credential !== null}
      onClose={onClose}
      title="Save your connection"
      closeOnClickOutside={false}
      closeOnEscape={false}
      withCloseButton={false}
    >
      {credential && (
        <Stack>
          <Text>
            This password is shown only now. Save it before closing. It remains valid until revoked
            or replaced, or your account security changes.
          </Text>
          <SecretField
            label="WebDAV endpoint"
            value={credential.endpoint}
          />
          <SecretField
            label="Username"
            value={credential.connection.username}
          />
          <SecretField
            label="App password"
            value={credential.password}
          />
          <Button onClick={download}>Download rclone config</Button>
          <Text
            size="sm"
            c="dimmed"
          >
            The config contains a recoverable password. Anyone with a copy can access your library.
            Keep it private.
          </Text>
          <Button
            variant="light"
            onClick={onClose}
          >
            I have saved my connection
          </Button>
        </Stack>
      )}
    </Modal>
  );
}

function SecretField({ label, value }: { label: string; value: string }) {
  return (
    <Stack gap="xs">
      <TextInput
        label={label}
        value={value}
        readOnly
        autoComplete="off"
      />
      <CopyButton value={value}>
        {({ copied, copy }) => (
          <Button
            variant="subtle"
            onClick={copy}
          >
            {copied ? 'Copied' : `Copy ${label.toLowerCase()}`}
          </Button>
        )}
      </CopyButton>
    </Stack>
  );
}
