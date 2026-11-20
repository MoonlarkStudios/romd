import type { MediaType } from '@romd/admin-api-client';

export type MediaGroup = 'presentation' | 'packaging' | 'gameplay' | 'documents';

interface MediaTypeInfo {
  label: string;
  group: MediaGroup;
  /** Packaging scans keep their scanned proportions and can be preferred per release. */
  packaging?: true;
  /** Documents are PDFs, opened in the manual viewer rather than shown as images. */
  document?: true;
  /** Uploads of this type are offered in the collection form. */
  uploadable?: true;
}

/**
 * Every media type the contract names, keyed by the contract's union so a new type fails type-checking until it
 * has a label and a group here.
 */
export const mediaTypeInfo = {
  Cover: {
    label: 'Cover',
    group: 'presentation',
    uploadable: true,
  },
  Background: {
    label: 'Background',
    group: 'presentation',
    uploadable: true,
  },
  Banner: {
    label: 'Banner',
    group: 'presentation',
    uploadable: true,
  },
  Logo: {
    label: 'Logo',
    group: 'presentation',
    uploadable: true,
  },
  Box3d: {
    label: '3D box',
    group: 'presentation',
    uploadable: true,
  },
  BoxFront: {
    label: 'Box front',
    group: 'packaging',
    packaging: true,
    uploadable: true,
  },
  BoxBack: {
    label: 'Box back',
    group: 'packaging',
    packaging: true,
    uploadable: true,
  },
  BoxSpine: {
    label: 'Box spine',
    group: 'packaging',
    packaging: true,
    uploadable: true,
  },
  BoxFull: {
    label: 'Full box wrap',
    group: 'packaging',
    packaging: true,
    uploadable: true,
  },
  CartridgeFront: {
    label: 'Cartridge front',
    group: 'packaging',
    packaging: true,
    uploadable: true,
  },
  CartridgeBack: {
    label: 'Cartridge back',
    group: 'packaging',
    packaging: true,
    uploadable: true,
  },
  DiscLabel: {
    label: 'Disc label',
    group: 'packaging',
    packaging: true,
    uploadable: true,
  },
  Screenshot: {
    label: 'Screenshot',
    group: 'gameplay',
    uploadable: true,
  },
  TitleScreen: {
    label: 'Title screen',
    group: 'gameplay',
    uploadable: true,
  },
  Video: {
    label: 'Video',
    group: 'gameplay',
  },
  Manual: {
    label: 'Manual',
    group: 'documents',
    document: true,
    uploadable: true,
  },
} satisfies Record<MediaType, MediaTypeInfo>;

export const mediaGroups: {
  value: MediaGroup;
  label: string;
}[] = [
  {
    value: 'presentation',
    label: 'Presentation art',
  },
  {
    value: 'packaging',
    label: 'Packaging',
  },
  {
    value: 'gameplay',
    label: 'Gameplay',
  },
  {
    value: 'documents',
    label: 'Manuals',
  },
];

export const allMediaTypes = Object.keys(mediaTypeInfo).filter(isMediaType);

export const uploadableMediaTypes = allMediaTypes.filter(
  (type) => (mediaTypeInfo[type] as MediaTypeInfo).uploadable,
);

export function isMediaType(value: string): value is MediaType {
  return Object.hasOwn(mediaTypeInfo, value);
}

export function mediaTypeLabel(type: string): string {
  return isMediaType(type) ? mediaTypeInfo[type].label : type;
}

export function isPackagingType(type: string): boolean {
  return isMediaType(type) && (mediaTypeInfo[type] as MediaTypeInfo).packaging === true;
}

export function isDocumentType(type: string): boolean {
  return isMediaType(type) && (mediaTypeInfo[type] as MediaTypeInfo).document === true;
}

export function mediaGroupOf(type: string): MediaGroup {
  return isMediaType(type) ? mediaTypeInfo[type].group : 'presentation';
}

/** Images and documents never exchange types, so a classification offers only types of the item's own kind. */
export function compatibleMediaTypes(type: string): MediaType[] {
  const document = isDocumentType(type);
  return uploadableMediaTypes.filter((candidate) => isDocumentType(candidate) === document);
}
