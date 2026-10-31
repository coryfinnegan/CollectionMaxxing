import { PRIVACY_NOTE } from './privacyNote';

const text = PRIVACY_NOTE.join(' ');

describe('privacy note', () => {
  it('says covers are read on the phone, by Apple’s on-device model, and nothing goes to an AI service', () => {
    expect(text).toMatch(/read on your iPhone/i);
    expect(text).toMatch(/on-device model/i);
    expect(text).toMatch(/nothing goes to an AI service/i);
  });

  it('reads right on the web too: it never says "this iPhone"', () => {
    expect(text).not.toMatch(/this iPhone/i);
  });

  it('no longer promises that photos never leave the phone, which a close call breaks', () => {
    expect(text).not.toMatch(/photos stay on your iPhone/i);
    expect(text).not.toMatch(/never leave/i);
  });

  it('says what a close call sends: the scan photo and an outline of the cover, to your own PC', () => {
    expect(text).toMatch(/close call/i);
    expect(text).toMatch(/your scan photo and an outline of the cover/i);
    expect(text).toMatch(/your own PC/i);
  });

  it('says that photo is only checked and thrown away, never kept', () => {
    expect(text).toMatch(/checked, then thrown away/i);
    expect(text).toMatch(/never kept/i);
  });

  it('says a cover photo is kept only if you choose Use my photo, with its hidden details removed', () => {
    expect(text).toMatch(/kept only if you choose Use my photo/);
    expect(text).toMatch(/hidden photo details are removed/i);
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

  it('follows the copy rules: sentence case, no exclamation marks, no apologies', () => {
    expect(text).not.toMatch(/!/);
    expect(text).not.toMatch(/sorry|oops/i);
    for (const paragraph of PRIVACY_NOTE) expect(paragraph).toMatch(/^[A-Z]/);
  });

  it('stays short', () => {
    expect(text.split(/\s+/).length).toBeLessThanOrEqual(150);
  });
});
