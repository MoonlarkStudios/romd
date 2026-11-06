export const persistenceKeys = ['serverInstanceSha256', 'adminUserSha256', 'librarySha256',
  'titleSha256', 'releaseSha256', 'contentSha256'];

export function assertPersistedState(actual, expected) {
  if (!actual || persistenceKeys.some((key) => !/^[0-9a-f]{64}$/.test(actual[key] ?? ''))) {
    throw new Error('Persistent qualification identity is incomplete');
  }
  if (expected && persistenceKeys.some((key) => actual[key] !== expected[key])) {
    throw new Error('Persistent server, account, library, release or content identity changed');
  }
}
