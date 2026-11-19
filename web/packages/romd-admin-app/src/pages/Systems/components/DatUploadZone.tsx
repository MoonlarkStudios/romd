import { Group, rem, Text } from '@mantine/core';
import { Dropzone } from '@mantine/dropzone';
import { IconFileImport, IconUpload, IconX } from '@tabler/icons-react';

interface DatUploadZoneProps {
  onDrop: (files: File[]) => void;
  loading?: boolean;
  disabled?: boolean;
  compact?: boolean;
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

export function DatUploadZone({ onDrop, loading, disabled, compact = false }: DatUploadZoneProps) {
  return (
    <Dropzone
      data-testid="dat-upload-zone"
      onDrop={onDrop}
      validator={validateDatCatalog}
      loading={loading}
      disabled={disabled}
      maxFiles={1}
      p={compact ? 'sm' : undefined}
      aria-label="Upload a DAT file"
    >
      <Group
        justify={compact ? 'flex-start' : 'center'}
        gap={compact ? 'sm' : 'xl'}
        mih={compact ? 48 : 120}
        wrap={compact ? 'nowrap' : 'wrap'}
        style={{
          pointerEvents: 'none',
        }}
      >
        <Dropzone.Accept>
          <IconUpload
            style={{
              width: rem(compact ? 24 : 52),
              height: rem(compact ? 24 : 52),
              color: 'var(--mantine-primary-color-filled)',
            }}
            stroke={1.5}
          />
        </Dropzone.Accept>
        <Dropzone.Reject>
          <IconX
            style={{
              width: rem(compact ? 24 : 52),
              height: rem(compact ? 24 : 52),
              color: 'var(--mantine-color-red-6)',
            }}
            stroke={1.5}
          />
        </Dropzone.Reject>
        <Dropzone.Idle>
          <IconFileImport
            style={{
              width: rem(compact ? 24 : 52),
              height: rem(compact ? 24 : 52),
              color: 'var(--mantine-color-dimmed)',
            }}
            stroke={1.5}
          />
        </Dropzone.Idle>

        <div>
          <Text
            size={compact ? 'sm' : 'lg'}
            fw={compact ? 600 : undefined}
          >
            {compact ? 'Choose a DAT file' : 'Drag a DAT file here or click to select'}
          </Text>
          <Text
            size={compact ? 'xs' : 'sm'}
            c="dimmed"
            mt={compact ? undefined : 7}
          >
            {compact
              ? 'Drop a Logiqx .dat or .xml file'
              : 'Accepts .dat and .xml files (Logiqx format)'}
          </Text>
        </div>
      </Group>
    </Dropzone>
  );
}
