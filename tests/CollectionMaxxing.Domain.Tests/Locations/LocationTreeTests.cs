using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Locations;

namespace CollectionMaxxing.Domain.Tests.Locations;

public class LocationTreeTests
{
    private static readonly Guid Library = Guid.CreateVersion7();

    private readonly LocationTree _tree = new(Library, []);

    private Location AddOk(string name, Guid? parentId = null, int? sortOrder = null)
    {
        var result = _tree.Add(Guid.CreateVersion7(), parentId, name, sortOrder);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return result.Value;
    }

    // ---- names -------------------------------------------------------------------------------

    [Fact]
    public void A_location_is_created_in_the_trees_library_with_the_given_name()
    {
        var shelf = AddOk("Living room");

        shelf.LibraryId.ShouldBe(Library);
        shelf.Name.ShouldBe("Living room");
        shelf.ParentId.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_name_is_required(string? name)
    {
        var result = _tree.Add(Guid.CreateVersion7(), null, name, null);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("location.name_required");
        result.Error.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Field.ShouldBe("name");
    }

    [Fact]
    public void A_name_of_exactly_100_characters_is_accepted()
    {
        AddOk(new string('a', 100));
    }

    [Fact]
    public void A_name_longer_than_100_characters_is_rejected()
    {
        var result = _tree.Add(Guid.CreateVersion7(), null, new string('a', 101), null);

        result.Error!.Code.ShouldBe("location.name_too_long");
        result.Error.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed_before_the_length_check_and_before_saving()
    {
        var location = AddOk("  " + new string('a', 100) + "  ");

        location.Name.Length.ShouldBe(100);
    }

    // ---- unique names per parent -------------------------------------------------------------

    [Fact]
    public void Two_siblings_cannot_share_a_name_ignoring_case()
    {
        AddOk("Shelf A");

        var result = _tree.Add(Guid.CreateVersion7(), null, "shelf a", null);

        result.Error!.Code.ShouldBe("location.name_duplicate");
        result.Error.Kind.ShouldBe(ErrorKind.Conflict);
    }

    [Fact]
    public void The_same_name_is_allowed_under_different_parents()
    {
        var room1 = AddOk("Room 1");
        var room2 = AddOk("Room 2");

        AddOk("Shelf", room1.Id);
        AddOk("Shelf", room2.Id);
    }

    [Fact]
    public void A_root_location_may_share_a_name_with_a_nested_one()
    {
        var room = AddOk("Room");

        AddOk("Shelf", room.Id);
        AddOk("Shelf");
    }

    [Fact]
    public void Renaming_to_a_siblings_name_is_rejected()
    {
        AddOk("A");
        var b = AddOk("B");

        var result = _tree.Update(b.Id, new LocationPatch(Name: "a"));

        result.Error!.Code.ShouldBe("location.name_duplicate");
    }

    [Fact]
    public void Renaming_a_location_to_a_different_case_of_its_own_name_is_allowed()
    {
        var a = AddOk("shelf");

        var result = _tree.Update(a.Id, new LocationPatch(Name: "Shelf"));

        result.IsSuccess.ShouldBeTrue();
        a.Name.ShouldBe("Shelf");
    }

    [Fact]
    public void Moving_under_a_parent_that_already_has_that_name_is_rejected()
    {
        var room = AddOk("Room");
        AddOk("Shelf", room.Id);
        var loose = AddOk("Shelf");

        var result = _tree.Update(loose.Id, new LocationPatch(ParentSpecified: true, ParentId: room.Id));

        result.Error!.Code.ShouldBe("location.name_duplicate");
    }

    // ---- parent ------------------------------------------------------------------------------

    [Fact]
    public void A_parent_must_exist_in_the_same_library()
    {
        var result = _tree.Add(Guid.CreateVersion7(), Guid.CreateVersion7(), "Shelf", null);

        result.Error!.Code.ShouldBe("location.parent_not_found");
        result.Error.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Field.ShouldBe("parentId");
    }

    // ---- cycles ------------------------------------------------------------------------------

    [Fact]
    public void A_location_cannot_become_its_own_parent()
    {
        var room = AddOk("Room");

        var result = _tree.Update(room.Id, new LocationPatch(ParentSpecified: true, ParentId: room.Id));

        result.Error!.Code.ShouldBe("location.cycle");
        result.Error.Kind.ShouldBe(ErrorKind.Conflict);
    }

    [Fact]
    public void A_location_cannot_move_under_its_own_descendant()
    {
        var room = AddOk("Room");
        var shelf = AddOk("Shelf", room.Id);
        var bin = AddOk("Bin", shelf.Id);

        var result = _tree.Update(room.Id, new LocationPatch(ParentSpecified: true, ParentId: bin.Id));

        result.Error!.Code.ShouldBe("location.cycle");
        room.ParentId.ShouldBeNull();
    }

    [Fact]
    public void A_location_can_move_to_a_sibling_branch()
    {
        var room1 = AddOk("Room 1");
        var room2 = AddOk("Room 2");
        var shelf = AddOk("Shelf", room1.Id);

        var result = _tree.Update(shelf.Id, new LocationPatch(ParentSpecified: true, ParentId: room2.Id));

        result.IsSuccess.ShouldBeTrue();
        shelf.ParentId.ShouldBe(room2.Id);
    }

    [Fact]
    public void A_nested_location_can_move_back_to_the_root()
    {
        var room = AddOk("Room");
        var shelf = AddOk("Shelf", room.Id);

        var result = _tree.Update(shelf.Id, new LocationPatch(ParentSpecified: true, ParentId: null));

        result.IsSuccess.ShouldBeTrue();
        shelf.ParentId.ShouldBeNull();
    }

    // ---- patch semantics ---------------------------------------------------------------------

    [Fact]
    public void A_patch_that_leaves_the_parent_unspecified_keeps_the_parent()
    {
        var room = AddOk("Room");
        var shelf = AddOk("Shelf", room.Id);

        _tree.Update(shelf.Id, new LocationPatch(Name: "Top shelf")).IsSuccess.ShouldBeTrue();

        shelf.ParentId.ShouldBe(room.Id);
        shelf.Name.ShouldBe("Top shelf");
    }

    [Fact]
    public void Updating_an_unknown_location_is_not_found()
    {
        var result = _tree.Update(Guid.CreateVersion7(), new LocationPatch(Name: "x"));

        result.Error!.Code.ShouldBe("location.not_found");
        result.Error.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public void A_patch_with_an_invalid_name_changes_nothing()
    {
        var shelf = AddOk("Shelf");

        var result = _tree.Update(shelf.Id, new LocationPatch(Name: "", SortOrder: 9));

        result.IsSuccess.ShouldBeFalse();
        shelf.Name.ShouldBe("Shelf");
        shelf.SortOrder.ShouldNotBe(9);
    }

    // ---- ordering ----------------------------------------------------------------------------

    [Fact]
    public void A_new_location_goes_after_its_existing_siblings_by_default()
    {
        var a = AddOk("A");
        var b = AddOk("B");
        var c = AddOk("C");

        a.SortOrder.ShouldBe(0);
        b.SortOrder.ShouldBe(1);
        c.SortOrder.ShouldBe(2);
    }

    [Fact]
    public void Sort_order_counts_per_parent()
    {
        var room = AddOk("Room");
        AddOk("Other");

        AddOk("Shelf", room.Id).SortOrder.ShouldBe(0);
    }

    [Fact]
    public void An_explicit_sort_order_is_kept()
    {
        AddOk("A", sortOrder: 7).SortOrder.ShouldBe(7);
    }

    // ---- removing ----------------------------------------------------------------------------

    [Fact]
    public void A_leaf_location_can_be_removed()
    {
        var shelf = AddOk("Shelf");

        _tree.Remove(shelf.Id).IsSuccess.ShouldBeTrue();

        _tree.Locations.ShouldBeEmpty();
    }

    [Fact]
    public void A_location_with_children_cannot_be_removed()
    {
        var room = AddOk("Room");
        AddOk("Shelf", room.Id);

        var result = _tree.Remove(room.Id);

        result.Error!.Code.ShouldBe("location.has_children");
        result.Error.Kind.ShouldBe(ErrorKind.Conflict);
    }

    [Fact]
    public void Removing_an_unknown_location_is_not_found()
    {
        _tree.Remove(Guid.CreateVersion7()).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    // ---- existing data -----------------------------------------------------------------------

    [Fact]
    public void A_tree_built_from_stored_locations_enforces_the_same_rules()
    {
        var first = AddOk("Room");
        var reloaded = new LocationTree(Library, _tree.Locations.ToList());

        var result = reloaded.Add(Guid.CreateVersion7(), null, "ROOM", null);

        result.Error!.Code.ShouldBe("location.name_duplicate");
        reloaded.Locations.ShouldContain(first);
    }
}
