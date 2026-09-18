import { Group, rem, Text } from '@mantine/core';
import { Dropzone } from '@mantine/dropzone';
import {
  IconDatabase,
  IconDeviceGamepad2,
  IconSparkles,
  IconUpload,
  IconX,
} from '@tabler/icons-react';

export type UploadMode = 'auto' | 'rom' | 'dat';

interface UploadDropZoneProps {
  mode: UploadMode;
  onDrop: (files: File[]) => void;
  loading?: boolean;
}

interface DropzoneFileError {
  code: string;
  message: string;
}

type ValidatorFn = (file: File) => DropzoneFileError | DropzoneFileError[] | null;

function isDatCatalog(file: File): boolean {
  const extension = file.name.toLowerCase().split('.').pop();
  return (
    extension === 'dat' ||
    extension === 'xml' ||
    file.type === 'text/xml' ||
    file.type === 'application/xml'
  );
}

function validateDatCatalog(file: File): DropzoneFileError | null {
  if (isDatCatalog(file)) return null;
  return {
    code: 'file-invalid-type',
    message: 'Only DAT and XML catalogs can be uploaded in DAT mode.',
  };
}

const modeConfig: Record<
  UploadMode,
  {
    color: string;
    icon: typeof IconSparkles;
    title: string;
    subtitle: string;
    accept: string[] | undefined;
    validator: ValidatorFn | undefined;
  }
> = {
  auto: {
    color: 'blue',
    icon: IconSparkles,
    title: 'Drag archives, ROMs, or DATs here',
    subtitle: 'Auto-detects file type and processes accordingly',
    accept: undefined,
    validator: undefined,
  },
  rom: {
    color: 'green',
    icon: IconDeviceGamepad2,
    title: 'Upload game binaries or ROM archives',
    subtitle: 'Accepts ROM files, archives, and disc images',
    accept: undefined,
    validator: (file: File): DropzoneFileError | null => {
      if (isDatCatalog(file)) {
        return {
          code: 'file-invalid-type',
          message: 'DAT/XML files should be uploaded in DAT mode',
        };
      }
      return null;
    },
  },
  dat: {
    color: 'orange',
    icon: IconDatabase,
    title: 'Upload Logiqx DAT or XML files',
    subtitle: 'Catalog definition files only',
    accept: undefined,
    validator: validateDatCatalog,
  },
};

export function UploadDropZone({ mode, onDrop, loading }: UploadDropZoneProps) {
  const config = modeConfig[mode];
  const Icon = config.icon;
  const colorVar = `var(--mantine-color-${config.color}-6)`;

  return (
    <Dropzone
      onDrop={onDrop}
      accept={config.accept}
      validator={config.validator}
      loading={loading}
      maxFiles={1}
      styles={{
        root: {
          borderColor: `var(--mantine-color-${config.color}-4)`,
        },
      }}
    >
      <Group
        justify="center"
        gap="xl"
        mih={180}
        style={{ pointerEvents: 'none' }}
      >
        <Dropzone.Accept>
          <IconUpload
            style={{
              width: rem(52),
              height: rem(52),
              color: colorVar,
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
          <Icon
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
            {config.title}
          </Text>
          <Text size="sm" c="dimmed" inline mt={7}>
            {config.subtitle}
          </Text>
        </div>
      </Group>
    </Dropzone>
  );
}
