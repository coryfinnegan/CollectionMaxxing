// Release signing for the TestFlight build (ADR-024). ios-build.yml passes the team and the App Store profile at prebuild
// (IOS_SIGNING_TEAM_ID, IOS_SIGNING_PROFILE, read in app.config.ts); then only the app target's Release configuration
// switches to manual signing with the Apple Distribution certificate. Without them (a local prebuild, the web export,
// Jest) nothing changes and the project stays as Expo makes it. Pods are left alone: they build as static libraries.
const { withXcodeProject } = require('expo/config-plugins');

const unquote = (value) => String(value ?? '').replace(/^"(.*)"$/, '$1');
const quote = (value) => `"${String(value).replace(/"/g, '\\"')}"`;

/** Switches the Release configuration of every app target in a parsed project to manual signing. Returns how many it changed. */
function applyReleaseSigning(objects, { teamId, profile }) {
  let changed = 0;
  for (const [id, target] of Object.entries(objects.PBXNativeTarget ?? {})) {
    if (id.endsWith('_comment') || unquote(target.productType) !== 'com.apple.product-type.application') continue;
    const list = objects.XCConfigurationList?.[target.buildConfigurationList];
    for (const { value } of list?.buildConfigurations ?? []) {
      const config = objects.XCBuildConfiguration?.[value];
      if (!config || unquote(config.name) !== 'Release') continue;
      Object.assign(config.buildSettings, {
        CODE_SIGN_STYLE: 'Manual',
        DEVELOPMENT_TEAM: teamId,
        PROVISIONING_PROFILE_SPECIFIER: quote(profile),
        CODE_SIGN_IDENTITY: quote('Apple Distribution'),
        '"CODE_SIGN_IDENTITY[sdk=iphoneos*]"': quote('Apple Distribution'),
      });
      changed++;
    }
    // Xcode reads the target's signing style from here too; keep the two in agreement, as EAS does.
    for (const [projectId, project] of Object.entries(objects.PBXProject ?? {})) {
      if (projectId.endsWith('_comment')) continue;
      project.attributes ??= {};
      project.attributes.TargetAttributes ??= {};
      project.attributes.TargetAttributes[id] = { ...project.attributes.TargetAttributes[id], DevelopmentTeam: teamId, ProvisioningStyle: 'Manual' };
    }
  }
  return changed;
}

function withReleaseSigning(config, { teamId, profile } = {}) {
  if (!teamId && !profile) return config;
  if (!teamId || !profile) throw new Error('Release signing needs both IOS_SIGNING_TEAM_ID and IOS_SIGNING_PROFILE.');
  if (!/^[A-Z0-9]{10}$/.test(teamId)) throw new Error(`IOS_SIGNING_TEAM_ID should be a 10-character Apple team ID, got "${teamId}".`);
  return withXcodeProject(config, (mod) => {
    if (applyReleaseSigning(mod.modResults.hash.project.objects, { teamId, profile }) === 0) {
      throw new Error('Release signing found no app target Release configuration to sign.');
    }
    return mod;
  });
}

module.exports = withReleaseSigning;
module.exports.applyReleaseSigning = applyReleaseSigning;
