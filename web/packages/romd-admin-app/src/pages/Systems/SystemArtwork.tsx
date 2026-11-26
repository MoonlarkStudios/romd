import {
  Alert,
  AspectRatio,
  Button,
  Center,
  FileButton,
  Group,
  Image,
  SimpleGrid,
  Stack,
  Text,
  Title,
} from '@mantine/core';
import {
  deleteSystemOverride,
  getAdminSystem,
  type SystemResource,
  updateSystem,
  uploadReferenceAsset,
} from '@romd/admin-api-client';
import { useMutation } from '@tanstack/react-query';
import { useRef } from 'react';
import workspace from '../../components/Workspace/Workspace.module.css';
import { workspaceActionProps } from '../../components/Workspace/workspaceActions';
import { useRefreshSystemPresentation } from '../../hooks/api/useManagedSystems';
import { usePlatformOverrides } from '../../hooks/api/usePlatforms';
import { usePermissions } from '../../hooks/usePermissions';
import { SystemLogo } from './SystemLogo';

type ArtworkAction = {
  file: File | null;
  revision: string;
};

export function SystemArtwork({ system }: { system: SystemResource }) {
  return (
    <Stack gap="md">
      <SystemArtworkPanel
        system={system}
        field="consoleArtwork"
        label="Console artwork"
      />
      <SystemArtworkPanel
        system={system}
        field="controllerArtwork"
        label="Controller artwork"
      />
    </Stack>
  );
}

function SystemArtworkPanel({
  system,
  field,
  label,
}: {
  system: SystemResource;
  field: 'consoleArtwork' | 'controllerArtwork';
  label: string;
}) {
  const { canEditReferenceData } = usePermissions();
  const overrides = usePlatformOverrides(system.key, canEditReferenceData);
  const refresh = useRefreshSystemPresentation();
  const resetFile = useRef<() => void>(null);
  const path = {
    systemKey: system.key,
  };
  const artwork = system[field];
  const custom = !!overrides.data?.[field];
  const artworkSource = custom ? 'Custom artwork' : 'Built-in artwork';
  const headingId = `${field}-heading`;

  const change = useMutation({
    mutationFn: async ({ file, revision }: ArtworkAction) => {
      const headers = {
        'If-Match': `"${revision}"`,
      };
      if (file) {
        if (file.type !== 'image/png' && file.type !== 'image/webp')
          throw new Error('Choose a PNG or WebP image.');
        if (file.size > 2 * 1024 * 1024)
          throw new Error('Choose an image that is 2 MiB or smaller.');
        const uploaded = await uploadReferenceAsset({
          body: file,
          headers: {
            'Content-Type': file.type,
          },
        });
        if (uploaded.error || !uploaded.data)
          throw new Error(uploaded.error?.detail ?? 'The image could not be uploaded.');
        const updated = await updateSystem({
          path,
          headers,
          body: {
            [field]: uploaded.data,
          },
        });
        if (updated.response?.status === 412) {
          const latest = await getAdminSystem({
            path,
          });
          if (latest.data) await refresh(latest.data);
          throw new Error(
            'This system changed during the upload. Choose the image again to apply it.',
          );
        }
        if (updated.error || !updated.data)
          throw new Error(updated.error?.detail ?? 'The artwork could not be saved.');
        return updated.data;
      }

      const restored = await deleteSystemOverride({
        path: {
          ...path,
          field,
        },
        headers,
      });
      const latest = await getAdminSystem({
        path,
      });
      if (restored.response?.status === 412) {
        if (latest.data) await refresh(latest.data);
        throw new Error('This system changed. Review the current artwork and try again.');
      }
      if (restored.error)
        throw new Error(restored.error.detail ?? 'The override could not be removed.');
      if (latest.error || !latest.data)
        throw new Error('The override was removed. Reload this page to see the current artwork.');
      return latest.data;
    },
    onSuccess: refresh,
  });

  return (
    <section
      aria-labelledby={headingId}
      className={workspace.panel}
    >
      <Stack gap="md">
        <Title
          order={2}
          id={headingId}
          className={workspace.sectionHeading}
        >
          {label}
        </Title>
        <SimpleGrid
          cols={{
            base: 1,
            sm: 2,
          }}
          spacing="lg"
        >
          <AspectRatio
            ratio={4 / 3}
            bg="var(--romd-portal-overlay)"
            bdrs="md"
          >
            {artwork ? (
              <Image
                src={artwork.url}
                alt={`${system.name} ${label.toLowerCase()}`}
                fit="contain"
              />
            ) : (
              <Center>
                <SystemLogo
                  icon={system.icon}
                  size="preview"
                />
              </Center>
            )}
          </AspectRatio>
          <Stack
            gap="sm"
            justify="center"
            align="flex-start"
          >
            <Text
              size="sm"
              fw={600}
            >
              {artwork
                ? overrides.isSuccess
                  ? artworkSource
                  : 'Current artwork'
                : 'No artwork yet'}
            </Text>
            {!artwork && system.icon && (
              <Text
                size="sm"
                c="dimmed"
              >
                The system logo is shown as a fallback.
              </Text>
            )}
            {canEditReferenceData && (
              <>
                <Text
                  size="sm"
                  c="dimmed"
                >
                  Use a PNG or WebP up to 2 MiB.
                </Text>
                <Group gap="xs">
                  <FileButton
                    accept="image/png,image/webp"
                    resetRef={resetFile}
                    onChange={(file) => {
                      if (file)
                        change.mutate({
                          file,
                          revision: system.revision,
                        });
                      resetFile.current?.();
                    }}
                  >
                    {(props) => (
                      <Button
                        {...props}
                        {...workspaceActionProps}
                        loading={change.isPending && !!change.variables?.file}
                        disabled={change.isPending}
                      >
                        {artwork ? 'Replace image' : 'Upload image'}
                      </Button>
                    )}
                  </FileButton>
                  {custom && (
                    <Button
                      variant="subtle"
                      color="gray"
                      loading={change.isPending && change.variables?.file === null}
                      disabled={change.isPending}
                      onClick={() =>
                        change.mutate({
                          file: null,
                          revision: system.revision,
                        })
                      }
                    >
                      {system.ownership === 'Romd' ? 'Restore default' : 'Remove custom artwork'}
                    </Button>
                  )}
                </Group>
              </>
            )}
            {change.isError && (
              <Alert
                color="red"
                role="alert"
              >
                {change.error.message}
              </Alert>
            )}
            {overrides.isError && !change.isPending && (
              <Button
                variant="subtle"
                onClick={() => void overrides.refetch()}
              >
                Retry loading artwork settings
              </Button>
            )}
          </Stack>
        </SimpleGrid>
      </Stack>
    </section>
  );
}
