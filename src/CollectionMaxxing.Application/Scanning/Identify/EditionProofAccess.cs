using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// Who may send an edition proof. A proof is not a scan of its own: when it matches, it rewrites an item the library already
/// has (its release link and its verified flag), so it takes the role any other edit of an item takes, owner or editor.
/// <c>/identify</c> itself is open to a viewer (they may scan and look), so the route cannot say this; the rule is here, in one
/// place, and both the request (<see cref="IdentifyItems"/>) and its queued job (<see cref="ResolvePendingIdentify"/>) ask it
/// with the role the person has <em>now</em>. A job's payload can be days old, and the sender may have been demoted since.
/// </summary>
internal static class EditionProofAccess
{
    public static readonly DomainError EditorRequired = new(
        ErrorKind.Forbidden,
        "scan.edition_proof_needs_editor",
        "Only an owner or editor of this library can check an item's edition.");

    /// <summary>True when <paramref name="role"/> (null for a non-member) may change a library item by proving its edition.</summary>
    public static bool Allows(LibraryRole? role) => role is LibraryRole.Owner or LibraryRole.Editor;
}
