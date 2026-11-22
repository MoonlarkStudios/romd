export class ConsumerLibraryUnavailableError extends Error {
  constructor() {
    super('Your library is not available yet.');
  }
}

export class ConsumerGameUnavailableError extends Error {
  constructor() {
    super('This game is no longer available in your library.');
  }
}
