import type { CurrentUserResponse } from '@romd/admin-api-client';

export const authFixtures = {
  currentUser: {
    id: 'user-1',
    email: 'admin@example.com',
    roles: ['Admin'],
    libraryId: null,
  } satisfies CurrentUserResponse,

  regularUser: {
    id: 'user-2',
    email: 'user@example.com',
    roles: ['User'],
    libraryId: null,
  } satisfies CurrentUserResponse,
};
