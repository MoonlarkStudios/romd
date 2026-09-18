import type { TitleMediaRef } from '@romd/admin-api-client';
import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { render } from '../../test/utils/render';
import { MediaGallery } from './MediaGallery';

const media: TitleMediaRef[] = [
  {
    id: 'm1',
    type: 'Cover',
    url: '/media/m1',
    sourceId: 'user',
    isPrimary: true,
    file: { sizeBytes: '8000', sizeOnDiskBytes: '2048', isCompressed: true },
  },
];

describe('MediaGallery', () => {
  it('shows each asset on-disk size from its stored file', () => {
    render(<MediaGallery media={media} titleId="t1" />);

    // 2048 bytes formats to "2 KB", rendered beside the asset type.
    expect(screen.getByText(/2 KB/)).toBeInTheDocument();
  });

  it('omits the size when the media has no stored file', () => {
    render(<MediaGallery media={[{ ...media[0], file: undefined }]} titleId="t1" />);

    expect(screen.queryByText(/KB/)).not.toBeInTheDocument();
  });
});
