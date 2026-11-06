export function catalogProviderName(provider: string): string {
  return (
    (
      {
        redump: 'Redump',
        'no-intro': 'No-Intro',
      } as Record<string, string>
    )[provider] ?? provider
  );
}
