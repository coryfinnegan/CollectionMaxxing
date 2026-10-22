const withReleaseSigning = require('./withReleaseSigning');

const { applyReleaseSigning } = withReleaseSigning;

// The sections of a prebuilt project.pbxproj the plugin touches, shaped as the xcode parser hands them over: quoted
// strings keep their quotes and every object has a "<id>_comment" twin.
function project() {
  const debug = { isa: 'XCBuildConfiguration', name: 'Debug', buildSettings: { CODE_SIGN_ENTITLEMENTS: 'CollectionMaxxing/CollectionMaxxing.entitlements' } };
  const release = { isa: 'XCBuildConfiguration', name: 'Release', buildSettings: { CODE_SIGN_ENTITLEMENTS: 'CollectionMaxxing/CollectionMaxxing.entitlements' } };
  const projectRelease = { isa: 'XCBuildConfiguration', name: 'Release', buildSettings: { SDKROOT: 'iphoneos' } };
  return {
    PBXProject: { P1: { isa: 'PBXProject', attributes: { TargetAttributes: { T1: { CreatedOnToolsVersion: '16.0' } } } }, P1_comment: 'Project object' },
    PBXNativeTarget: {
      T1: { isa: 'PBXNativeTarget', name: 'CollectionMaxxing', productType: '"com.apple.product-type.application"', buildConfigurationList: 'L1' },
      T1_comment: 'CollectionMaxxing',
    },
    XCConfigurationList: {
      L1: { isa: 'XCConfigurationList', buildConfigurations: [{ value: 'D1', comment: 'Debug' }, { value: 'R1', comment: 'Release' }] },
      L1_comment: 'Build configuration list for PBXNativeTarget "CollectionMaxxing"',
    },
    XCBuildConfiguration: { D1: debug, D1_comment: 'Debug', R1: release, R1_comment: 'Release', R2: projectRelease, R2_comment: 'Release' },
  };
}

const SIGNING = { teamId: 'ABCDE12345', profile: 'CollectionMaxxing App Store' };

describe('release signing for the TestFlight build', () => {
  it('signs the app target Release build manually with the Apple Distribution certificate and the App Store profile', () => {
    const objects = project();

    expect(applyReleaseSigning(objects, SIGNING)).toBe(1);

    expect(objects.XCBuildConfiguration.R1.buildSettings).toEqual({
      CODE_SIGN_ENTITLEMENTS: 'CollectionMaxxing/CollectionMaxxing.entitlements',
      CODE_SIGN_STYLE: 'Manual',
      DEVELOPMENT_TEAM: 'ABCDE12345',
      PROVISIONING_PROFILE_SPECIFIER: '"CollectionMaxxing App Store"',
      CODE_SIGN_IDENTITY: '"Apple Distribution"',
      '"CODE_SIGN_IDENTITY[sdk=iphoneos*]"': '"Apple Distribution"',
    });
    expect(objects.PBXProject.P1.attributes.TargetAttributes.T1).toEqual({ CreatedOnToolsVersion: '16.0', DevelopmentTeam: 'ABCDE12345', ProvisioningStyle: 'Manual' });
  });

  it('leaves the Debug build, the project-level settings and other targets as Expo made them', () => {
    const objects = project();
    objects.PBXNativeTarget.T2 = { isa: 'PBXNativeTarget', productType: '"com.apple.product-type.bundle"', buildConfigurationList: 'L2' };
    objects.XCConfigurationList.L2 = { buildConfigurations: [{ value: 'R3', comment: 'Release' }] };
    objects.XCBuildConfiguration.R3 = { name: 'Release', buildSettings: {} };

    applyReleaseSigning(objects, SIGNING);

    expect(objects.XCBuildConfiguration.D1.buildSettings).toEqual({ CODE_SIGN_ENTITLEMENTS: 'CollectionMaxxing/CollectionMaxxing.entitlements' });
    expect(objects.XCBuildConfiguration.R2.buildSettings).toEqual({ SDKROOT: 'iphoneos' });
    expect(objects.XCBuildConfiguration.R3.buildSettings).toEqual({});
  });

  it('quotes a profile name that contains quotes', () => {
    const objects = project();
    applyReleaseSigning(objects, { ...SIGNING, profile: 'My "best" profile' });
    expect(objects.XCBuildConfiguration.R1.buildSettings.PROVISIONING_PROFILE_SPECIFIER).toBe('"My \\"best\\" profile"');
  });

  it('changes nothing when CI did not ask for signing (local prebuilds, the web export)', () => {
    const config = { name: 'CollectionMaxxing', slug: 'collectionmaxxing' };
    expect(withReleaseSigning(config, {})).toBe(config);
    expect(withReleaseSigning(config, { teamId: undefined, profile: undefined })).toBe(config);
    expect(withReleaseSigning(config)).toBe(config);
  });

  it('refuses half a signing setup or a team ID that is not one, so a broken secret fails the build early', () => {
    const config = { name: 'CollectionMaxxing', slug: 'collectionmaxxing' };
    expect(() => withReleaseSigning(config, { teamId: 'ABCDE12345' })).toThrow('needs both');
    expect(() => withReleaseSigning(config, { profile: 'CollectionMaxxing App Store' })).toThrow('needs both');
    expect(() => withReleaseSigning(config, { teamId: 'abc', profile: 'x' })).toThrow('10-character Apple team ID');
  });
});
