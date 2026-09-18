import type { UserDto } from '@romd/admin-api-client';
import type { UserRole } from '../../hooks/api/useUsers';
export const systemUserId = '00000000-0000-0000-0000-000000000001';
export const isSystemUser = (user: UserDto) => user.id === systemUserId || user.email.toLowerCase() === 'system@romd.local';
export const accountStatus = (user: UserDto) => isSystemUser(user) ? 'System' : user.isSuspended ? 'Suspended' : user.requiresActivation ? 'Pending activation' : 'Active';
export const highestRole = (user: UserDto): UserRole => ['Admin', 'Manager', 'Contributor', 'User'].find(role => user.roles.includes(role)) as UserRole ?? 'User';
export const roleDescription: Record<UserRole, string> = {
  User: 'Consumer catalog and console access. No access to the admin portal.',
  Contributor: 'Consumer access plus ROM uploads and ingestion tracking in the admin portal.',
  Manager: 'Contributor access plus catalog curation, sources, metadata, and enrichment.',
  Admin: 'Full administration, including accounts, libraries, integrations, and installation settings.',
};
