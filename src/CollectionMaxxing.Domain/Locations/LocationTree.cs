using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Domain.Locations;

/// <summary>
/// All the locations of one library, with the rules that span more than one of them:
/// names are unique per parent, parents must exist, the tree has no cycles.
/// Mutating methods leave the tree untouched when they fail.
/// </summary>
public sealed class LocationTree
{
    private readonly Guid _libraryId;
    private readonly Dictionary<Guid, Location> _byId;

    public LocationTree(Guid libraryId, IEnumerable<Location> existing)
    {
        _libraryId = libraryId;
        _byId = existing.ToDictionary(l => l.Id);
    }

    public IReadOnlyCollection<Location> Locations => _byId.Values;

    public Result<Location> Add(Guid id, Guid? parentId, string? name, int? sortOrder)
    {
        var checkedName = ValidateName(name);
        if (checkedName.Error is { } nameError)
        {
            return nameError;
        }

        if (parentId is { } parent && !_byId.ContainsKey(parent))
        {
            return new DomainError(ErrorKind.Validation, "location.parent_not_found", "The parent location doesn't exist.", "parentId");
        }

        if (HasSiblingNamed(parentId, checkedName.Value, except: null))
        {
            return DuplicateName();
        }

        var location = new Location(id, _libraryId, parentId, checkedName.Value, sortOrder ?? NextSortOrder(parentId));
        _byId.Add(id, location);
        return location;
    }

    public Result<Location> Update(Guid id, LocationPatch patch)
    {
        if (!_byId.TryGetValue(id, out var location))
        {
            return NotFound();
        }

        // Work out the final state first, so a rejected patch changes nothing.
        var name = location.Name;
        if (patch.Name is not null)
        {
            var checkedName = ValidateName(patch.Name);
            if (checkedName.Error is { } nameError)
            {
                return nameError;
            }

            name = checkedName.Value;
        }

        var parentId = patch.ParentSpecified ? patch.ParentId : location.ParentId;
        if (patch.ParentSpecified && parentId is { } newParent)
        {
            if (!_byId.ContainsKey(newParent))
            {
                return new DomainError(ErrorKind.Validation, "location.parent_not_found", "The parent location doesn't exist.", "parentId");
            }

            if (IsSelfOrDescendant(candidate: newParent, of: id))
            {
                return new DomainError(ErrorKind.Conflict, "location.cycle", "A location can't be moved inside itself.", "parentId");
            }
        }

        if (HasSiblingNamed(parentId, name, except: id))
        {
            return DuplicateName();
        }

        location.Rename(name);
        location.MoveTo(parentId);
        if (patch.SortOrder is { } sortOrder)
        {
            location.Reorder(sortOrder);
        }

        return location;
    }

    public Result Remove(Guid id)
    {
        if (!_byId.TryGetValue(id, out var location))
        {
            return Result.Failure(NotFound());
        }

        if (_byId.Values.Any(l => l.ParentId == id))
        {
            return Result.Failure(new DomainError(ErrorKind.Conflict, "location.has_children", "Move or delete the locations inside this one first."));
        }

        _byId.Remove(location.Id);
        return Result.Success();
    }

    private static Result<string> ValidateName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return new DomainError(ErrorKind.Validation, "location.name_required", "A name is required.", "name");
        }

        if (trimmed.Length > Location.MaxNameLength)
        {
            return new DomainError(ErrorKind.Validation, "location.name_too_long", $"A name can be at most {Location.MaxNameLength} characters.", "name");
        }

        return trimmed;
    }

    private static DomainError DuplicateName() =>
        new(ErrorKind.Conflict, "location.name_duplicate", "Another location here already has that name.", "name");

    private static DomainError NotFound() =>
        new(ErrorKind.NotFound, "location.not_found", "That location doesn't exist.");

    private bool HasSiblingNamed(Guid? parentId, string name, Guid? except) =>
        _byId.Values.Any(l =>
            l.ParentId == parentId
            && l.Id != except
            && string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    private int NextSortOrder(Guid? parentId)
    {
        var siblings = _byId.Values.Where(l => l.ParentId == parentId).ToList();
        return siblings.Count == 0 ? 0 : siblings.Max(l => l.SortOrder) + 1;
    }

    /// <summary>Walks up from <paramref name="candidate"/>; true if it reaches <paramref name="of"/> (or is it).</summary>
    private bool IsSelfOrDescendant(Guid candidate, Guid of)
    {
        Guid? current = candidate;
        while (current is { } id)
        {
            if (id == of)
            {
                return true;
            }

            current = _byId.TryGetValue(id, out var location) ? location.ParentId : null;
        }

        return false;
    }
}
