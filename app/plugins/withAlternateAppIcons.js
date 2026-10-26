// Alternate home screen icons for the iPhone build (#364). Each icon ships its @2x/@3x
// PNGs as bundle resources and registers under CFBundleAlternateIcons, which
// modules/cm-app-icon switches between at runtime. Fails the prebuild early with a
// plain message when the inputs are wrong; re-running on an existing project adds
// nothing twice. Android needs no plugin: the adaptive sets are plain config (see
// the lead notes), and runtime switching there is out of scope (no Android app ships).
const { withDangerousMod, withInfoPlist, withXcodeProject } = require('expo/config-plugins');
const { createHash } = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const NAME_PATTERN = /^[A-Za-z0-9][A-Za-z0-9_.-]*$/;

const baseName = (name) => `${name}-icon`;
const destFile = (name, scale) => `${baseName(name)}@${scale}.png`;
const unquote = (value) => String(value ?? '').replace(/^"(.*)"$/, '$1');

/** Deterministic 24-hex object ids, so repeated prebuilds produce the same project. */
function objectId(...parts) {
  return createHash('sha1').update(parts.join(':')).digest('hex').slice(0, 24).toUpperCase();
}

/** Sources resolve against the project root (the plugin lives in <root>/plugins), whatever the caller's cwd. */
const resolveSource = (source) => path.join(__dirname, '..', source);

function pngSize(file) {
  const bytes = fs.readFileSync(file);
  return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) };
}

/** Fails the build early when the icon inputs are wrong. */
function validateIcons(icons) {
  if (!Array.isArray(icons) || icons.length === 0) {
    throw new Error('Alternate app icons need an `icons` list with at least one entry.');
  }
  for (const icon of icons) {
    if (!icon || !NAME_PATTERN.test(icon.name ?? '')) {
      throw new Error(`Alternate icon names hold letters, numbers, "_", "." and "-", got "${icon?.name}".`);
    }
    for (const [field, size] of [
      ['source2x', 120],
      ['source3x', 180],
    ]) {
      if (!icon[field]) throw new Error(`Alternate icon "${icon.name}" needs a ${field} PNG.`);
      if (!fs.existsSync(resolveSource(icon[field]))) {
        throw new Error(`Alternate icon "${icon.name}" ${field} not found: ${icon[field]}.`);
      }
      const { width, height } = pngSize(resolveSource(icon[field]));
      if (width !== size || height !== size) {
        throw new Error(`Alternate icon "${icon.name}" ${field} must be ${size}x${size}, got ${width}x${height}: ${icon[field]}.`);
      }
    }
  }
}

/** Registers each icon under CFBundleAlternateIcons, keeping the plist's other entries. */
function applyAlternateIconsPlist(infoPlist, icons) {
  infoPlist.CFBundleAlternateIcons ??= {};
  for (const { name } of icons) {
    infoPlist.CFBundleAlternateIcons[name] = { CFBundleIconFiles: [baseName(name)], UIPrerenderedIcon: false };
  }
  return infoPlist;
}

/** Adds files as bundle resources of every app target. Returns how many it added (0 when all were already there). */
function applyAlternateIconResources(objects, { projectName, files }) {
  const targets = Object.entries(objects.PBXNativeTarget ?? {}).filter(
    ([id, target]) => !id.endsWith('_comment') && unquote(target.productType) === 'com.apple.product-type.application',
  );
  if (targets.length === 0) throw new Error('Alternate app icons found no app target to add resources to.');
  objects.PBXFileReference ??= {};
  objects.PBXBuildFile ??= {};
  const have = new Set(
    Object.entries(objects.PBXFileReference)
      .filter(([id]) => !id.endsWith('_comment'))
      .map(([, ref]) => unquote(ref.path)),
  );
  let added = 0;
  for (const file of files) {
    const refPath = `${projectName}/${file}`;
    if (have.has(refPath)) continue;
    const refId = objectId('fileref', refPath);
    objects.PBXFileReference[refId] = { isa: 'PBXFileReference', lastKnownFileType: 'image.png', path: refPath, sourceTree: 'SOURCE_ROOT' };
    for (const [targetId, target] of targets) {
      const phaseId = (target.buildPhases ?? []).map((p) => p.value).find((id) => objects.PBXResourcesBuildPhase?.[id]);
      if (!phaseId) throw new Error('Alternate app icons found an app target with no Resources phase.');
      const buildId = objectId('buildfile', targetId, refPath);
      objects.PBXBuildFile[buildId] = { isa: 'PBXBuildFile', fileRef: refId };
      objects.PBXResourcesBuildPhase[phaseId].files.push({ value: buildId, comment: `${file} in Resources` });
    }
    // The main group shows the files in Xcode; the build only needs the phase above.
    for (const [, project] of Object.entries(objects.PBXProject ?? {})) {
      if (!project.mainGroup || !objects.PBXGroup?.[project.mainGroup]) continue;
      objects.PBXGroup[project.mainGroup].children.push({ value: refId, comment: file });
    }
    have.add(refPath);
    added++;
  }
  return added;
}

function withAlternateAppIcons(config, { icons } = {}) {
  validateIcons(icons);
  config = withDangerousMod(config, [
    'ios',
    async (mod) => {
      const dir = path.join(mod.modRequest.platformProjectRoot, mod.modRequest.projectName);
      fs.mkdirSync(dir, { recursive: true });
      for (const icon of icons) {
        fs.copyFileSync(resolveSource(icon.source2x), path.join(dir, destFile(icon.name, '2x')));
        fs.copyFileSync(resolveSource(icon.source3x), path.join(dir, destFile(icon.name, '3x')));
      }
      return mod;
    },
  ]);
  config = withInfoPlist(config, (mod) => {
    applyAlternateIconsPlist(mod.modResults, icons);
    return mod;
  });
  config = withXcodeProject(config, (mod) => {
    const files = icons.flatMap((icon) => [destFile(icon.name, '2x'), destFile(icon.name, '3x')]);
    applyAlternateIconResources(mod.modResults.hash.project.objects, { projectName: mod.modRequest.projectName, files });
    return mod;
  });
  return config;
}

module.exports = withAlternateAppIcons;
module.exports.applyAlternateIconsPlist = applyAlternateIconsPlist;
module.exports.applyAlternateIconResources = applyAlternateIconResources;
module.exports.validateIcons = validateIcons;
