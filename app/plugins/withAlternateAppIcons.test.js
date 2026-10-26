const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const withAlternateAppIcons = require('./withAlternateAppIcons');

const { applyAlternateIconResources, applyAlternateIconsPlist, validateIcons } = withAlternateAppIcons;

// The sections of a prebuilt project.pbxproj the plugin touches, shaped as the xcode parser hands them over.
function project() {
  return {
    PBXProject: {
      P1: { isa: 'PBXProject', mainGroup: 'G1' },
      P1_comment: 'Project object',
    },
    PBXNativeTarget: {
      T1: { isa: 'PBXNativeTarget', name: 'CollectionMaxxing', productType: '"com.apple.product-type.application"', buildPhases: [{ value: 'R1', comment: 'Resources' }] },
      T1_comment: 'CollectionMaxxing',
    },
    PBXResourcesBuildPhase: {
      R1: { isa: 'PBXResourcesBuildPhase', files: [] },
      R1_comment: 'Resources',
    },
    PBXGroup: {
      G1: { isa: 'PBXGroup', children: [] },
      G1_comment: 'CollectionMaxxing',
    },
    PBXFileReference: {},
    PBXBuildFile: {},
  };
}

const ICONS = [{ name: 'gameboy', source2x: './assets/icons/icon-gameboy@2x.png', source3x: './assets/icons/icon-gameboy@3x.png' }];

describe('alternate app icons', () => {
  it('registers the alternate in the plist under its name, keeping the other keys', () => {
    const plist = { CFBundleName: 'CollectionMaxxing', CFBundleAlternateIcons: { other: { CFBundleIconFiles: ['other-icon'] } } };

    applyAlternateIconsPlist(plist, [{ name: 'gameboy' }, { name: 'other2' }]);

    expect(plist.CFBundleAlternateIcons).toEqual({
      other: { CFBundleIconFiles: ['other-icon'] },
      gameboy: { CFBundleIconFiles: ['gameboy-icon'], UIPrerenderedIcon: false },
      other2: { CFBundleIconFiles: ['other2-icon'], UIPrerenderedIcon: false },
    });
    expect(plist.CFBundleName).toBe('CollectionMaxxing');
  });

  it('adds the @2x/@3x files as bundle resources of the app target', () => {
    const objects = project();

    expect(applyAlternateIconResources(objects, { projectName: 'CollectionMaxxing', files: ['gameboy-icon@2x.png', 'gameboy-icon@3x.png'] })).toBe(2);

    const refs = Object.values(objects.PBXFileReference);
    expect(refs).toHaveLength(2);
    expect(refs.map((r) => r.path).sort()).toEqual(['CollectionMaxxing/gameboy-icon@2x.png', 'CollectionMaxxing/gameboy-icon@3x.png']);
    for (const ref of refs) {
      expect(ref.isa).toBe('PBXFileReference');
      expect(ref.sourceTree).toBe('SOURCE_ROOT');
    }
    // Every build file points at a ref, every ref is in the Resources phase and the main group.
    const refIds = Object.keys(objects.PBXFileReference);
    const buildIds = Object.keys(objects.PBXBuildFile);
    expect(buildIds).toHaveLength(2);
    for (const id of buildIds) expect(refIds).toContain(objects.PBXBuildFile[id].fileRef);
    expect(objects.PBXResourcesBuildPhase.R1.files.map((f) => f.value).sort()).toEqual(buildIds.sort());
    expect(objects.PBXGroup.G1.children.map((f) => f.value).sort()).toEqual(refIds.sort());
  });

  it('adds nothing twice: a second prebuild without --clean leaves the project as it was', () => {
    const objects = project();
    const args = { projectName: 'CollectionMaxxing', files: ['gameboy-icon@2x.png', 'gameboy-icon@3x.png'] };
    applyAlternateIconResources(objects, args);
    const once = JSON.parse(JSON.stringify(objects));

    expect(applyAlternateIconResources(objects, args)).toBe(0);
    expect(objects).toEqual(once);
  });

  it('refuses a project with no app target or no Resources phase, so a broken template fails the build early', () => {
    const args = { projectName: 'CollectionMaxxing', files: ['gameboy-icon@2x.png'] };
    expect(() => applyAlternateIconResources({ PBXNativeTarget: {} }, args)).toThrow('no app target');
    const noPhase = project();
    noPhase.PBXNativeTarget.T1.buildPhases = [];
    expect(() => applyAlternateIconResources(noPhase, args)).toThrow('no Resources phase');
  });

  it('accepts the shipped Game Boy files', () => {
    expect(() => validateIcons(ICONS)).not.toThrow();
  });

  it('refuses icons with no name, a bad name or missing sources', () => {
    expect(() => validateIcons(undefined)).toThrow('icons');
    expect(() => validateIcons([])).toThrow('icons');
    expect(() => validateIcons([{ source2x: 'a', source3x: 'b' }])).toThrow('name');
    expect(() => validateIcons([{ name: 'bad name!', source2x: 'a', source3x: 'b' }])).toThrow('name');
    expect(() => validateIcons([{ name: 'gameboy', source3x: ICONS[0].source3x }])).toThrow('source2x');
    expect(() => validateIcons([{ name: 'gameboy', source2x: './assets/icons/nope.png', source3x: ICONS[0].source3x }])).toThrow('nope.png');
  });

  it('refuses sources that are not the sizes iOS looks for (120px @2x, 180px @3x)', () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'cm-icons-'));
    const tiny = path.join(dir, 'tiny.png');
    const big = path.join(dir, 'big.png');
    fs.writeFileSync(tiny, fakePng(100, 100));
    fs.writeFileSync(big, fakePng(180, 180));
    const rel = (f) => `./${path.relative(path.join(__dirname, '..'), f).replace(/\\/g, '/')}`;
    expect(() => validateIcons([{ name: 'gameboy', source2x: rel(tiny), source3x: rel(big) }])).toThrow('120x120');
    expect(() => validateIcons([{ name: 'gameboy', source2x: ICONS[0].source2x, source3x: rel(tiny) }])).toThrow('180x180');
  });

  it('registers its mods and leaves the config otherwise alone', () => {
    const config = { name: 'CollectionMaxxing', slug: 'collectionmaxxing' };
    const out = withAlternateAppIcons(config, { icons: ICONS });
    expect(out.name).toBe('CollectionMaxxing');
    expect(out.mods.ios.infoPlist).toBeDefined();
    expect(out.mods.ios.xcodeproj).toBeDefined();
    expect(out.mods.ios.dangerous).toBeDefined();
  });
});

/** A minimal PNG with the given IHDR size: enough for the dimension check. */
function fakePng(width, height) {
  const header = Buffer.alloc(33);
  Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]).copy(header, 0);
  header.writeUInt32BE(13, 8);
  header.write('IHDR', 12, 'ascii');
  header.writeUInt32BE(width, 16);
  header.writeUInt32BE(height, 20);
  return header;
}
