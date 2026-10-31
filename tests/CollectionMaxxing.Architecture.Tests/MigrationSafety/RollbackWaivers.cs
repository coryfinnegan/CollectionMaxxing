namespace CollectionMaxxing.Architecture.Tests.MigrationSafety;

/// <summary>
/// The migrations the rollback check accepts although they break the previous release, each for the risks it names and with the
/// reason. See <see cref="RollbackSafeMigrationsTests"/> for what the check looks for and when to add a line here.
/// </summary>
internal static class RollbackWaivers
{
    public static readonly IReadOnlyList<RollbackWaiver> All =
    [
        // Migrations from before the rule. They are deployed everywhere, and the code that used what they removed is gone.
        new("20261023000332_Auth0SignIn", [RollbackRisk.DropsOrRenames], "Before the rule; deployed long ago. Dropped the OpenIddict, passkey and pairing tables and the Identity login columns."),
        new("20261025230258_ScanSessionContext", [RollbackRisk.RequiredColumnWithoutDefault], "Before the rule; deployed long ago. Added scan_sessions.channel and media_hint nullable, backfilled them, then made them required."),
        new("20261026131827_ScanAutoCommit", [RollbackRisk.RequiredColumnWithoutDefault], "Before the rule; deployed long ago. Added scan_sessions.auto_commit_tiers nullable, backfilled it, then made it required."),
    ];
}
