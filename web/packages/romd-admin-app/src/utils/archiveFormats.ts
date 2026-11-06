import type { ArchiveFormatDto } from '@romd/admin-api-client';

/**
 * The server lists the archive formats a title, release or library can produce, labelled from its format catalog.
 * An archive binds an encoded format only for the files that have it, so its choice says so.
 */
export function archiveFormatLabel(choice: ArchiveFormatDto): string {
  return choice.format === 'original' ? choice.label : `${choice.label} where available`;
}
