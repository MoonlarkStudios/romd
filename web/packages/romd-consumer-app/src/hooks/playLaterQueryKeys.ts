export const playLaterAccountKey = (accountId: string) =>
  [
    'consumer',
    'playLater',
    accountId,
  ] as const;
