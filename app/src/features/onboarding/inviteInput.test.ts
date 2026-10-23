import { parseInvite } from './inviteInput';

describe('parseInvite', () => {
  it.each([
    ['ABCD-EFGH-JK', 'ABCD-EFGH-JK'],
    ['abcd-efgh-jk', 'ABCD-EFGH-JK'],
    ['abcdefghjk', 'ABCD-EFGH-JK'],
    ['  ABCD EFGH JK  ', 'ABCD-EFGH-JK'],
  ])('reads the code %j as %s', (input, expected) => {
    expect(parseInvite(input)).toBe(expected);
  });

  it('counts O as 0 and I or L as 1, as Crockford base32 allows', () => {
    expect(parseInvite('ab0d-efgh-1k')).toBe('AB0D-EFGH-1K');
    expect(parseInvite('abOd-efgh-Ik')).toBe('AB0D-EFGH-1K');
    expect(parseInvite('abOd-efgh-lk')).toBe('AB0D-EFGH-1K');
  });

  it.each([
    ['https://pc.tailnet.ts.net/join/valid-invite', 'valid-invite'],
    ['https://pc.tailnet.ts.net/join/valid-invite?x=1#top', 'valid-invite'],
    ['  /join/valid-invite  ', 'valid-invite'],
    ['valid-invite', 'valid-invite'],
  ])('takes the token from %j', (input, expected) => {
    expect(parseInvite(input)).toBe(expected);
  });

  it('reads a code that was pasted inside a link', () => {
    expect(parseInvite('https://pc.tailnet.ts.net/join/abcd-efgh-jk')).toBe('ABCD-EFGH-JK');
  });

  it.each(['', '   ', 'ab', 'not an invite!'])('rejects %j', (input) => {
    expect(parseInvite(input)).toBeNull();
  });
});
