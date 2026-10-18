import { PRIVACY_NOTE } from './privacyNote';

const text = PRIVACY_NOTE.join(' ');

describe('privacy note', () => {
  it('says photos and recognition stay on the phone', () => {
    expect(text).toMatch(/photos stay on this iPhone/i);
    expect(text).toMatch(/read on this iPhone/i);
  });

  it('says what goes to your own PC, including the optional cover photo', () => {
    expect(text).toMatch(/your own PC/i);
    expect(text).toMatch(/Use my photo/);
  });

  it('says providers only see barcodes and titles, never who you are', () => {
    expect(text).toMatch(/only barcodes and titles/i);
    expect(text).toMatch(/never who you are/i);
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
