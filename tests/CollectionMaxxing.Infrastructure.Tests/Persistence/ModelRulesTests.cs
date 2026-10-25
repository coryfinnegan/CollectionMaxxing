using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CollectionMaxxing.Infrastructure.Tests.Persistence;

/// <summary>
/// Rules that must hold for EVERY entity, present and future: a new library-scoped table that forgets its filter, its
/// library foreign key or its concurrency token fails here, so the rule cannot rot as streams add tables.
/// </summary>
public class ModelRulesTests(PostgresFixture db) : DatabaseTest(db)
{
    /// <summary>Library-scoped rows that are only ever inserted or deleted, never edited in place.</summary>
    private static readonly Type[] ImmutableLibraryScoped =
    [
        typeof(CollectionMaxxing.Domain.Items.ItemTag),
        typeof(CollectionMaxxing.Domain.Items.ItemTombstone),
        typeof(CollectionMaxxing.Domain.Scanning.ScanSnap),
        typeof(CollectionMaxxing.Domain.Operations.MatchFeedback),
    ];

    private IModel Model()
    {
        using var context = Db.CreateContext();
        return context.GetService<IDesignTimeModel>().Model;
    }

    private List<IEntityType> LibraryScoped() =>
        [.. Model().GetEntityTypes().Where(t => typeof(ILibraryScoped).IsAssignableFrom(t.ClrType))];

    [Fact]
    public void Every_library_scoped_entity_is_filtered_by_the_callers_libraries()
    {
        var model = Model();

        LibraryScoped().ShouldNotBeEmpty();
        LibraryScoped().Where(t => t.GetDeclaredQueryFilters().Count == 0).Select(t => t.ClrType.Name).ShouldBeEmpty();
        model.FindEntityType(typeof(Library))!.GetDeclaredQueryFilters().ShouldNotBeEmpty();
    }

    [Fact]
    public void Every_library_scoped_entity_has_a_foreign_key_to_its_library()
    {
        var missing = LibraryScoped()
            .Where(t => t.GetForeignKeys().All(fk => fk.PrincipalEntityType.ClrType != typeof(Library)))
            .Select(t => t.ClrType.Name);

        missing.ShouldBeEmpty();
    }

    [Fact]
    public void Every_editable_library_scoped_entity_carries_the_xmin_concurrency_token()
    {
        var unprotected = LibraryScoped()
            .Where(t => !ImmutableLibraryScoped.Contains(t.ClrType))
            .Where(t => !typeof(IConcurrencyVersioned).IsAssignableFrom(t.ClrType)
                || t.FindProperty(nameof(IConcurrencyVersioned.Version)) is not { IsConcurrencyToken: true } version
                || version.GetColumnName() != "xmin")
            .Select(t => t.ClrType.Name);

        unprotected.ShouldBeEmpty();
    }

    [Fact]
    public void Library_scoped_parents_are_referenced_with_the_library_in_the_key_so_cross_library_links_are_impossible()
    {
        // Every foreign key from one library-scoped entity to another must include library_id.
        var libraryScoped = LibraryScoped().Select(t => t.ClrType).ToHashSet();
        var crossLibraryRisks = LibraryScoped()
            .SelectMany(t => t.GetForeignKeys().Select(fk => (Dependent: t, Key: fk)))
            .Where(x => libraryScoped.Contains(x.Key.PrincipalEntityType.ClrType))
            .Where(x => x.Key.Properties.All(p => p.Name != nameof(ILibraryScoped.LibraryId)))
            .Select(x => $"{x.Dependent.ClrType.Name} -> {x.Key.PrincipalEntityType.ClrType.Name} ({string.Join(",", x.Key.Properties.Select(p => p.Name))})")
            .ToList();

        // item_feature_prints.release_id etc. point at the (shared) catalogue, which is not library-scoped, so they never appear here.
        // scan_candidates -> scan_snaps is keyed by session: the snap and the candidate share a session, hence a library.
        crossLibraryRisks.Where(r => !r.StartsWith("ScanCandidate -> ScanSnap", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Fact]
    public void Enum_values_are_stored_with_the_spelling_the_api_uses()
    {
        EnumText.ToText(CollectionMaxxing.Domain.Items.ItemCompleteness.NotApplicable).ShouldBe("n_a");
        EnumText.ToText(CollectionMaxxing.Domain.Items.ItemCondition.VeryGood).ShouldBe("very_good");
        EnumText.ToText(CollectionMaxxing.Domain.Catalog.ReleaseFormat.UhdBluray).ShouldBe("uhd_bluray");
        EnumText.ToText(CollectionMaxxing.Domain.Catalog.ReleaseRegion.NtscU).ShouldBe("ntsc_u");
        EnumText.ToText(CollectionMaxxing.Domain.Scanning.CandidateSource.PictureAppleModel).ShouldBe("picture_apple_model");
        EnumText.ToText(CollectionMaxxing.Domain.Scanning.ShotKind.SingleCover).ShouldBe("single_cover");
        EnumText.Parse<CollectionMaxxing.Domain.Items.ItemCompleteness>("n_a").ShouldBe(CollectionMaxxing.Domain.Items.ItemCompleteness.NotApplicable);
    }
}
