/** Complete wire page for fixtures whose collection fits one bounded response. */
export const completePage = <T>(items: T[]) => ({
  items,
  nextCursor: null,
  hasNextPage: false,
});
