import { PRIVACY_NOTE } from './privacyNote';

const text = PRIVACY_NOTE.join(' ');

describe('privacy note', () => {
  it('says photos and recognition stay on the phone', () => {
    expect(text).toMatch(/photos stay on your iPhone/i);
    expect(text).toMatch(/read on your iPhone/i);
  });

  it('reads right on the web too: it never says "this iPhone"', () => {
    expect(text).not.toMatch(/this iPhone/i);
  });

  it('says what goes to your own PC, including the optional cover photo', () => {
    expect(text).toMatch(/your own PC/i);
    expect(text).toMatch(/Use my photo/);
  });

  it('says the PC keeps picture fingerprints, and that they are not the pictures', () => {
    expect(text).toMatch(/picture fingerprints/i);
    expect(text).toMatch(/not the pictures/i);
  });

  it('says catalog services get only barcodes and titles, never photos or the account', () => {
    expect(text).toMatch(/only barcodes and titles/i);
    expect(text).toMatch(/never your photos or your account/i);
    expect(text).not.toMatch(/never who you are/i); // they do see the PC's network address
  });

  it('says there is no tracking or ads, and mentions export and deletion', () => {
    expect(text).toMatch(/no tracking or ads/i);
    expect(text).toMatch(/export/i);
    expect(text).toMatch(/delete/i);
  });

  it('claims nothing the privacy table rules out', () => {
    expect(text).not.toMatch(/analytics|third-party AI|price|location|contacts/i);
  });

  it('stays short', () => {
    expect(text.split(/\s+/).length).toBeLessThanOrEqual(150);
  });
});
