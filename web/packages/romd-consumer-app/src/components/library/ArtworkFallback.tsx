import { Box, Text } from '@mantine/core';

interface ArtworkFallbackProps {
  name: string;
  fontSize?: number;
}

/**
 * The shared no-artwork treatment: a title-hash gradient plate with a faint
 * monogram, echoing the console's procedural cover plates. Use everywhere a
 * title or shelf has no artwork so fallbacks look identical across the app.
 */
export function ArtworkFallback({ name, fontSize = 52 }: ArtworkFallbackProps) {
  const hue = hueFromName(name);

  return (
    <Box
      style={{
        display: 'grid',
        placeItems: 'center',
        width: '100%',
        height: '100%',
        background: `linear-gradient(150deg, hsl(${hue} 62% 24%), hsl(${(hue + 36) % 360} 66% 13%))`,
      }}
    >
      <Text
        fz={fontSize}
        fw={800}
        style={{ color: 'rgba(255, 255, 255, 0.08)', letterSpacing: '-0.04em' }}
      >
        {monogram(name)}
      </Text>
    </Box>
  );
}

function monogram(name: string): string {
  return name
    .replace(/^The\s+|[:.,!'-]/g, '')
    .trim()
    .split(/\s+/)
    .slice(0, 2)
    .map((word) => word[0] ?? '')
    .join('')
    .toUpperCase();
}

function hueFromName(name: string): number {
  let hash = 0;
  for (let index = 0; index < name.length; index += 1) {
    hash = (hash * 31 + name.charCodeAt(index)) >>> 0;
  }

  return hash % 360;
}
