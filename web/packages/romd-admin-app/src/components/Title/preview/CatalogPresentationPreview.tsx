import { Stack } from '@mantine/core';
import { type ArtworkPreviewTitle, ArtworkRolePreview } from './ArtworkRolePreview';
import { PreviewDeviceSwitcher, usePreviewDevice } from './PreviewDeviceSwitcher';

/** The title as consumers see it on game details, at the chosen device width. */
export function CatalogPresentationPreview({ title }: { title: ArtworkPreviewTitle }) {
  const { ref, device, choose } = usePreviewDevice();
  return (
    <Stack
      gap="xs"
      ref={ref}
    >
      <PreviewDeviceSwitcher
        value={device}
        onChange={choose}
      />
      <ArtworkRolePreview
        title={title}
        artworkRole="Backdrop"
        device={device}
      />
    </Stack>
  );
}
