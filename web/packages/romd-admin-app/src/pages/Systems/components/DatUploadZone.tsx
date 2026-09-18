import { Group, rem, Text } from '@mantine/core';
import { Dropzone } from '@mantine/dropzone';
import { IconFileImport, IconUpload, IconX } from '@tabler/icons-react';

interface DatUploadZoneProps {
  onDrop: (files: File[]) => void;
  loading?: boolean;
}

interface DropzoneFileError {
  code: string;
  message: string;
}

function validateDatCatalog(file: File): DropzoneFileError | null {
  const extension = file.name.toLowerCase().split('.').pop();
  if (
    extension === 'dat' ||
    extension === 'xml' ||
    file.type === 'text/xml' ||
    file.type === 'application/xml'
  ) {
    return null;
  }

  return {
    code: 'file-invalid-type',
    message: 'Only DAT and XML catalogs can be uploaded here.',
  };
}

export function DatUploadZone({ onDrop, loading }: DatUploadZoneProps) {
  return (
    <Dropzone
      data-testid="dat-upload-zone"
      onDrop={onDrop}
      validator={validateDatCatalog}
      loading={loading}
      maxFiles={1}
    >
      <Group
        justify="center"
        gap="xl"
        mih={120}
        style={{ pointerEvents: 'none' }}
      >
        <Dropzone.Accept>
          <IconUpload
            style={{
              width: rem(52),
              height: rem(52),
              color: 'var(--mantine-color-blue-6)',
            }}
            stroke={1.5}
          />
        </Dropzone.Accept>
        <Dropzone.Reject>
          <IconX
            style={{
              width: rem(52),
              height: rem(52),
              color: 'var(--mantine-color-red-6)',
            }}
            stroke={1.5}
          />
        </Dropzone.Reject>
        <Dropzone.Idle>
          <IconFileImport
            style={{
              width: rem(52),
              height: rem(52),
              color: 'var(--mantine-color-dimmed)',
            }}
            stroke={1.5}
          />
        </Dropzone.Idle>

        <div>
          <Text size="lg" inline>
            Drag a DAT file here or click to select
          </Text>
          <Text size="sm" c="dimmed" inline mt={7}>
            Accepts .dat and .xml files (Logiqx format)
          </Text>
        </div>
      </Group>
    </Dropzone>
  );
}
