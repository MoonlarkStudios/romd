import type {
  ChangePasswordRequest,
  CreateUserRequest,
  ProblemDetails,
  UpdateUserRequest,
  UserDto,
} from '@romd/admin-api-client';
import {
  assignDefaultLibraryToUsers,
  assignUserLibrary,
  assignUserRole,
  changePassword,
  createUser,
  deleteUser,
  getUserById,
  getUserDirectory,
  listUsers,
  updateUser,
} from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

export type UserRole = 'User' | 'Contributor' | 'Manager' | 'Admin';

export const userRoleOptions: { value: UserRole; label: string }[] = [
  { value: 'User', label: 'User' },
  { value: 'Contributor', label: 'Contributor' },
  { value: 'Manager', label: 'Manager' },
  { value: 'Admin', label: 'Admin' },
];

export const userKeys = {
  all: ['users'] as const,
  lists: () => [...userKeys.all, 'list'] as const,
  list: () => [...userKeys.lists()] as const,
};

export { libraryManagementKeys as libraryKeys, useLibraries } from './useLibraryManagement';

function isProblemDetails(error: unknown): error is ProblemDetails {
  return typeof error === 'object' && error !== null && ('detail' in error || 'title' in error);
}

function getApiErrorMessage(error: unknown, fallback: string): string {
  if (!isProblemDetails(error)) {
    return fallback;
  }

  return error.detail ?? error.title ?? fallback;
}

export function useUserDirectory(filters: { search?: string; role?: string; status?: string; libraryId?: string; cursor?: string }) {
  return useQuery({ queryKey: [...userKeys.all, 'directory', filters], queryFn: async ({ signal }) => {
    const response = await getUserDirectory({ query: filters, signal });
    if (response.error || !response.data) throw new Error(getApiErrorMessage(response.error, 'Could not load the user directory.'));
    return response.data;
  } });
}

export function useUser(userId: string) {
  return useQuery({ queryKey: [...userKeys.all, 'detail', userId], queryFn: async () => {
    const response = await getUserById({ path: { userId } });
    if (response.error || !response.data) throw new Error(getApiErrorMessage(response.error, 'Account not found or could not be loaded.'));
    return response.data;
  } });
}

export function useUsers() {
  return useQuery({
    queryKey: userKeys.list(),
    queryFn: async () => {
      const response = await listUsers();
      if (response.error) {
        throw new Error('Failed to fetch users');
      }
      return response.data as UserDto[];
    },
  });
}


export function useCreateUser() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (request: CreateUserRequest) => {
      const response = await createUser({ body: request });
      if (response.error) {
        throw new Error(getApiErrorMessage(response.error, 'Failed to create user'));
      }
      return response.data as UserDto;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: userKeys.all });
    },
  });
}

export function useUpdateUser() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ userId, request }: { userId: string; request: UpdateUserRequest }) => {
      const response = await updateUser({
        path: { userId },
        body: request,
      });
      if (response.error) {
        throw new Error(getApiErrorMessage(response.error, 'Failed to update user'));
      }
      return response.data as UserDto;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: userKeys.all });
    },
  });
}

export function useAssignUserRole() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ userId, role }: { userId: string; role: UserRole }) => {
      const response = await assignUserRole({
        path: { userId },
        body: { role },
      });
      if (response.error) {
        throw new Error(getApiErrorMessage(response.error, 'Failed to assign role'));
      }
      return response.data as UserDto;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: userKeys.all });
    },
  });
}

export function useAssignUserLibrary() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ userId, libraryId }: { userId: string; libraryId: string | null }) => {
      const response = await assignUserLibrary({
        path: { userId },
        body: { libraryId },
      });
      if (response.error) {
        throw new Error(getApiErrorMessage(response.error, 'Failed to assign library'));
      }
      return response.data as UserDto;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: userKeys.all });
    },
  });
}

export function useDeleteUser() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (userId: string) => {
      const response = await deleteUser({ path: { userId } });
      if (response.error) {
        throw new Error(getApiErrorMessage(response.error, 'Failed to delete user'));
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: userKeys.all });
    },
  });
}

export function useAssignDefaultLibraryToUsers() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (userIds: string[]) => {
      const response = await assignDefaultLibraryToUsers({ body: { userIds } });
      if (response.error) {
        throw new Error(getApiErrorMessage(response.error, 'Failed to assign default library'));
      }
      return response.data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: userKeys.all });
    },
  });
}

export function useChangePassword() {
  return useMutation({
    mutationFn: async (request: ChangePasswordRequest) => {
      const response = await changePassword({ body: request });
      if (response.error) {
        throw new Error(getApiErrorMessage(response.error, 'Failed to change password'));
      }
    },
  });
}
