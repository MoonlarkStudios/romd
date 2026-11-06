import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

describe('consumer presentation scope', () => {
  it('uses the consumer poster proportion inside host apps whose own token differs', () => {
    const styles = readFileSync(`${import.meta.dirname}/styles.css`, 'utf8');
    const scope = styles.slice(
      styles.indexOf('.romd-consumer-presentation {'),
      styles.indexOf('}', styles.indexOf('.romd-consumer-presentation {')),
    );
    expect(scope).toContain('--romd-poster-aspect: 3 / 4;');
  });
});
