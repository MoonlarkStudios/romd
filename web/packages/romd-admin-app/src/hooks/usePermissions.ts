import { useMemo } from 'react';
import { useAuth } from '../contexts/AuthContext';

export type Role = 'User' | 'Contributor' | 'Manager' | 'Admin';

export interface Permissions {
  /** Can upload ROMs and track ingestion (Contributor+) */
  canUploadRoms: boolean;
  /** Can manage titles, trigger enrichment, edit metadata (Manager+) */
  canManageTitles: boolean;
  /** Can trigger enrichment jobs (Manager+) */
  canTriggerEnrichment: boolean;
  /** Can change catalog source lifecycle status (Manager+) */
  canManageSources: boolean;
  /** Can manage users, roles, and libraries (Admin) */
  canManageUsers: boolean;
  canConfigureIntegrations: boolean;
  canEditReferenceData: boolean;
  canEditMetadataPolicy: boolean;
  /** Current user's highest role */
  role: Role;
  /** Whether user has at least the specified role level */
  hasRole: (requiredRole: Role) => boolean;
}

const ROLE_HIERARCHY: Record<Role, number> = {
  User: 0,
  Contributor: 1,
  Manager: 2,
  Admin: 3,
};

function getHighestRole(roles: string[]): Role {
  let highestLevel = 0;
  let highestRole: Role = 'User';

  for (const role of roles) {
    const level = ROLE_HIERARCHY[role as Role];
    if (level !== undefined && level > highestLevel) {
      highestLevel = level;
      highestRole = role as Role;
    }
  }

  return highestRole;
}

function hasRoleLevel(userRoles: string[], requiredRole: Role): boolean {
  const requiredLevel = ROLE_HIERARCHY[requiredRole];
  return userRoles.some((role) => {
    const level = ROLE_HIERARCHY[role as Role];
    return level !== undefined && level >= requiredLevel;
  });
}

/**
 * Hook to check user permissions based on role hierarchy.
 * Role hierarchy: Admin > Manager > Contributor > User
 *
 * @example
 * ```tsx
 * const { canManageTitles, canUploadRoms, role, hasRole } = usePermissions();
 *
 * if (canManageTitles) {
 *   // Show metadata editing UI
 * }
 *
 * if (hasRole('Contributor')) {
 *   // Show upload button
 * }
 * ```
 */
export function usePermissions(): Permissions {
  const { user } = useAuth();
  const userRoles = user?.roles ?? [];

  return useMemo(() => {
    const role = getHighestRole(userRoles);

    return {
      canUploadRoms: hasRoleLevel(userRoles, 'Contributor'),
      canManageTitles: hasRoleLevel(userRoles, 'Manager'),
      canTriggerEnrichment: hasRoleLevel(userRoles, 'Manager'),
      canManageSources: hasRoleLevel(userRoles, 'Manager'),
      canManageUsers: hasRoleLevel(userRoles, 'Admin'),
      canConfigureIntegrations: hasRoleLevel(userRoles, 'Admin'),
      canEditReferenceData: hasRoleLevel(userRoles, 'Admin'),
      canEditMetadataPolicy: hasRoleLevel(userRoles, 'Manager'),
      role,
      hasRole: (requiredRole: Role) => hasRoleLevel(userRoles, requiredRole),
    };
  }, [userRoles]);
}
