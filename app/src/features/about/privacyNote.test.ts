import { PRIVACY_NOTE } from './privacyNote';

const text = PRIVACY_NOTE.join(' ');
const para = (re: RegExp) => PRIVACY_NOTE.find((p) => re.test(p))!;

describe('privacy note', () => {
  it('says covers are read on the phone, with Apple’s on-device tools, and nothing goes to an outside AI service', () => {
    expect(PRIVACY_NOTE[0]).toBe('Covers are read on your iPhone with Apple’s on-device tools. Nothing goes to an outside AI service.');
  });

  it('reads right on the web too: it never says "this iPhone"', () => {
    expect(text).not.toMatch(/this iPhone/i);
  });

  it('never calls the host "your PC": a library may be hosted by someone else', () => {
    expect(text).not.toMatch(/\bPC\b/);
    expect(text).toMatch(/the server that hosts your library/i);
  });

  it('counts four cases a photo goes to the server', () => {
    expect(text).toMatch(/in four cases/i);
    expect(text).not.toMatch(/three cases|two cases/i);
  });

  it('says a shelf photo is sent smaller with its hidden details removed, read by a free model on the server, and thrown away, never kept', () => {
    const shelf = para(/shelf photo/i);
    expect(shelf).toMatch(/When it can read shelves, a shelf photo is sent smaller, with its hidden photo details removed/);
    expect(shelf).toMatch(/free model running on that server reads the spines/i);
    expect(shelf).toMatch(/thrown away, never kept/i);
  });

  it('no longer promises that photos never leave the phone', () => {
    expect(text).not.toMatch(/photos stay on your iPhone/i);
    expect(text).not.toMatch(/never leave/i);
  });

  it('says what a close call sends: the scan photo and an outline of the cover, checked, thrown away, never kept', () => {
    const closeCall = para(/close call/i);
    expect(closeCall).toMatch(/your scan photo and an outline of the cover/i);
    expect(closeCall).toMatch(/checked, then thrown away/i);
    expect(closeCall).toMatch(/never kept/i);
    expect(closeCall).not.toMatch(/Use my photo|unverified/i);
  });

  it('says Use my photo and a row kept as unverified send the photo of one cover, which the server keeps, private to the library, details removed', () => {
    const kept = para(/unverified/i);
    expect(kept).toMatch(/Use my photo/);
    expect(kept).toMatch(/photo of one cover/i);
    expect(kept).toMatch(/which the server keeps/i);
    expect(kept).toMatch(/private to your library/i);
    expect(kept).toMatch(/hidden photo details removed/i);
    expect(kept).toMatch(/stays until you delete the row or link it to an item/i);
    expect(kept).toMatch(/adding the row makes it the cover/i);
    expect(kept).not.toMatch(/never kept/i);
  });

  it('says the server keeps picture fingerprints, and that they are not the pictures', () => {
    expect(text).toMatch(/picture fingerprints/i);
    expect(text).toMatch(/not the pictures/i);
  });

  it('says catalog services get only barcodes and titles, never photos or the account', () => {
    expect(text).toMatch(/only barcodes and titles from that server/i);
    expect(text).toMatch(/never your photos or your account/i);
    expect(text).not.toMatch(/never who you are/i);
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

  it('is seven paragraphs and stays short', () => {
    expect(PRIVACY_NOTE).toHaveLength(7);
    expect(text.split(/\s+/).length).toBeLessThanOrEqual(230);
  });
});
