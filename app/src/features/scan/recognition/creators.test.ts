// Picking the author out of the Vision lines beside the title (#438 stage 1).
import type { TextLine } from '@/native/cm-vision';
import { creatorOf } from './creators';

const box = { x: 0, y: 0, width: 1, height: 0.1 };
const line = (text: string, confidence: number): TextLine => ({ text, confidence, box });

describe('creatorOf', () => {
  it('reads the author line beside the title (the Suddenly You cover)', () => {
    const title = line('Suddenly You', 0.9);
    const lines = [
      title,
      line('LISA KLEYPAs', 0.8),
      line('NEW YORK TIMES BESTSELLING AUTHOR', 0.75),
      line('The rare author who can make you laugh and cry on the same page', 0.7),
    ];

    expect(creatorOf(lines, title)).toBe('LISA KLEYPAs');
  });

  it('is empty when the piece holds only its title', () => {
    const title = line('Dune', 0.9);

    expect(creatorOf([title], title)).toBeUndefined();
  });

  it('ignores a line that only repeats the title', () => {
    const title = line('Suddenly You', 0.9);

    expect(creatorOf([title, line('suddenly you!', 0.85)], title)).toBeUndefined();
  });

  it('ignores years, prices and other digit lines', () => {
    const title = line('Suddenly You', 0.9);

    expect(creatorOf([title, line('2001', 0.95), line('$7.99', 0.9)], title)).toBeUndefined();
  });

  it('prefers the more confident of two name-like lines', () => {
    const title = line('Good Omens', 0.9);
    const lines = [title, line('NEIL GAIMAN', 0.7), line('TERRY PRATCHETT', 0.85)];

    expect(creatorOf(lines, title)).toBe('TERRY PRATCHETT');
  });
});
