import type { ReferenceAsset } from '@romd/admin-api-client';
import { fireEvent } from '@testing-library/react';
import { expect, it } from 'vitest';
import { render } from '../../test/utils/render';
import { SystemLogo } from './SystemLogo';

it('uses the published logo and falls back after an image error without hiding a new revision', () => {
  const icon: ReferenceAsset = {
    url: '/api/reference-catalog/assets/first',
    sha256: 'first',
    contentType: 'image/png',
    monochrome: true,
  };
  const { container, rerender } = render(<SystemLogo icon={icon} />);
  const image = container.querySelector('img');
  expect(image).toHaveAttribute('src', icon.url);
  expect(image).toHaveAttribute('alt', '');
  if (!image) throw new Error('Expected a logo image');
  fireEvent.error(image);
  expect(container.querySelector('img')).toBeNull();
  expect(container.querySelector('svg')).toBeInTheDocument();

  rerender(
    <SystemLogo
      icon={{
        ...icon,
        url: '/api/reference-catalog/assets/second',
      }}
    />,
  );
  expect(container.querySelector('img')).toHaveAttribute(
    'src',
    '/api/reference-catalog/assets/second',
  );
});
