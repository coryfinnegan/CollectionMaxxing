using CollectionMaxxing.Domain.Catalog;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// The kind a snap is of: what its own print says when no read names that kind, else the kind its reads agree on. The Vision tier
/// reads text, not kinds, so its items name none and the server searches every kind; a same-titled book then often sorts above the
/// film on the shelf. A read that names no kind is shown the snap's kind's releases first when they score level with the others. The
/// same rule serves <c>/identify</c> and the eval grounder, because both clean a snap's items through <c>IdentifyCleaning.Clean</c>,
/// which stamps the answer onto the reads that need it.
/// </summary>
/// <remarks>
/// <para>
/// A kind counts only when a clear majority of the reads that name a kind hold it: at least <see cref="MinReads"/> of them, and at
/// least <see cref="MinShare"/> of those naming a kind. Three, because two reads agreeing is a coincidence (a two-item snap is
/// not a shelf) and three is the least that can be called agreement. 70%, because it tolerates one dissenter among four reads
/// (75% agree), two among seven (71%) and three among ten (70%), but not one among three (67%), two among five (60%) or an even
/// split: a lone book among films is an item to be ordered, not a reason to doubt the shelf, while a snap that really mixes
/// kinds says nothing.
/// </para>
/// <para>
/// The phone's reading of the shelf's own print (<c>shelfKind.ts</c>) speaks only when no read of the snap names a kind in the
/// print's family. The phone reads the print only when at least two of the shelf's spines print one kind, so one DVD among books
/// makes no print at all. A read that names the printed kind shows the engines can tell that kind apart on this shelf, so their
/// kinds stand and the reads decide as they would without a print. When no read names it (the picture model,
/// guessing from the picture, called every tape on one shelf a book) the print wins: it is the snap's kind, and it takes the
/// kind off each read that denies it (<see cref="IdentifyCleaning"/>), so the type term no longer counts for that read and every
/// provider is asked. That changes how the read scores, not only the order of releases that tie. The print is already the one
/// kind it names: print that names two kinds (a film format beside a bestseller line) reaches here as none.
/// </para>
/// <para>
/// A film and a show are one family: a shelf of videotapes holds both, and the catalogue files a series under either.
/// </para>
/// </remarks>
public static class KindContext
{
    /// <summary>The fewest reads naming the same kind before the snap is said to have one.</summary>
    public const int MinReads = 3;

    /// <summary>The smallest share of the reads that name a kind that must name the same one (a family counts as one).</summary>
    public const double MinShare = 0.7;

    /// <summary>
    /// The kind the snap is of: the kind its own print names when it speaks (<see cref="PrintThatSpeaks"/>), else the kind most of its
    /// reads name (<see cref="Dominant"/>). The kinds are the reads' own, before any is taken off a read.
    /// </summary>
    public static WorkKind? Of(WorkKind? printKind, IEnumerable<WorkKind?> kinds) =>
        PrintThatSpeaks(printKind, kinds) is { } printed ? Family(printed) : Dominant(kinds);

    /// <summary>
    /// The kind the shelf's own print names, when it has anything to say: no read of the snap names a kind in its family. A read that
    /// does means the engines can tell that kind apart on this shelf, so the print is left out. A print of <see cref="WorkKind.Other"/>,
    /// or none, says nothing, and so does a read with no kind or <see cref="WorkKind.Other"/>.
    /// </summary>
    public static WorkKind? PrintThatSpeaks(WorkKind? printKind, IEnumerable<WorkKind?> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);

        return printKind is { } printed && printed != WorkKind.Other && !kinds.Any(kind => kind is { } named && InFamily(named, printed))
            ? printed
            : null;
    }

    /// <summary>
    /// The kind most of the reads name, or null when no kind has a clear majority. A read with no kind, or <see cref="WorkKind.Other"/>
    /// (the reader saying it does not know), says nothing. A film or show family is reported as <see cref="WorkKind.Film"/>.
    /// </summary>
    public static WorkKind? Dominant(IEnumerable<WorkKind?> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);

        var named = kinds.OfType<WorkKind>().Where(kind => kind != WorkKind.Other).Select(Family).ToList();
        if (named.Count < MinReads)
        {
            return null;
        }

        var top = named.GroupBy(kind => kind).OrderByDescending(group => group.Count()).First();
        return top.Count() >= MinReads && (double)top.Count() / named.Count >= MinShare ? top.Key : null;
    }

    /// <summary>True when a release of <paramref name="release"/> kind belongs to the family of <paramref name="snapKind"/>.</summary>
    public static bool InFamily(WorkKind release, WorkKind snapKind) => release != WorkKind.Other && Family(release) == Family(snapKind);

    private static WorkKind Family(WorkKind kind) => kind == WorkKind.Tv ? WorkKind.Film : kind;
}
