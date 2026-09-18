import { Modal, Tabs, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import {
  IconDatabase,
  IconDeviceGamepad2,
  IconSparkles,
} from '@tabler/icons-react';
import { useState } from 'react';
import { useUploadDat, useUploadGeneric, useUploadRom } from '../../hooks/api/useUpload';
import { UploadDropZone } from './UploadDropZone';

interface UploadCenterModalProps {
  opened: boolean;
  onClose: () => void;
}

export function UploadCenterModal({
  opened,
  onClose,
}: UploadCenterModalProps) {
  const [activeTab, setActiveTab] = useState<string | null>('auto');

  const uploadGeneric = useUploadGeneric();
  const uploadRom = useUploadRom();
  const uploadDat = useUploadDat();

  const isUploading =
    uploadGeneric.isPending || uploadRom.isPending || uploadDat.isPending;

  const handleUploadError = (error: Error) => {
    notifications.show({
      title: 'Upload failed',
      message: error.message,
      color: 'red',
    });
  };

  const handleAutoDrop = (files: File[]) => {
    const file = files[0];
    if (!file) return;

    uploadGeneric.mutate(
      { file },
      {
        onSuccess: () => onClose(),
        onError: handleUploadError,
      }
    );
  };

  const handleRomDrop = (files: File[]) => {
    const file = files[0];
    if (!file) return;

    uploadRom.mutate(
      { file },
      {
        onSuccess: () => onClose(),
        onError: handleUploadError,
      }
    );
  };

  const handleDatDrop = (files: File[]) => {
    const file = files[0];
    if (!file) return;

    uploadDat.mutate(
      { file },
      {
        onSuccess: () => onClose(),
        onError: handleUploadError,
      }
    );
  };

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title="Upload Center"
      size="lg"
      centered
      closeOnClickOutside={!isUploading}
      closeOnEscape={!isUploading}
    >
      <Tabs value={activeTab} onChange={setActiveTab}>
        <Tabs.List grow>
          <Tabs.Tab
            value="auto"
            leftSection={<IconSparkles size={16} />}
            color="blue"
          >
            Smart Ingestion
          </Tabs.Tab>
          <Tabs.Tab
            value="rom"
            leftSection={<IconDeviceGamepad2 size={16} />}
            color="green"
          >
            Game Files
          </Tabs.Tab>
          <Tabs.Tab
            value="dat"
            leftSection={<IconDatabase size={16} />}
            color="orange"
          >
            Catalog Definitions
          </Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="auto" pt="md">
          <Text size="sm" c="dimmed" mb="md">
            Upload any file and the system will automatically detect whether
            it&apos;s a ROM, archive, or DAT file.
          </Text>
          <UploadDropZone
            mode="auto"
            onDrop={handleAutoDrop}
            loading={uploadGeneric.isPending}
          />
        </Tabs.Panel>

        <Tabs.Panel value="rom" pt="md">
          <Text size="sm" c="dimmed" mb="md">
            Upload ROM files directly. DAT/XML files will be rejected - use the
            Catalog Definitions tab for those.
          </Text>
          <UploadDropZone
            mode="rom"
            onDrop={handleRomDrop}
            loading={uploadRom.isPending}
          />
        </Tabs.Panel>

        <Tabs.Panel value="dat" pt="md">
          <Text size="sm" c="dimmed" mb="md">
            Upload Logiqx DAT or XML catalog files to define game collections.
          </Text>
          <UploadDropZone
            mode="dat"
            onDrop={handleDatDrop}
            loading={uploadDat.isPending}
          />
        </Tabs.Panel>
      </Tabs>
    </Modal>
  );
}
