using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Libraries.Members;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Learning;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Items;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Items;

/// <summary>Use case: swap an item's release and keep everything else (condition, location, notes, tags).</summary>
public static class RematchItem
{
    public sealed record Command(Guid LibraryId, Guid Id, Guid? ReleaseId);

    public sealed class Handler(IAppDbContext db, TimeProvider clock, IUserDirectory? users = null)
    {
        public async Task<Result<Versioned<ItemDto>>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            if (command.ReleaseId is not { } releaseId)
            {
                return ItemErrors.Invalid("item.release_required", "Say which release to switch to.", "releaseId");
            }

            var item = await db.Items.FirstOrDefaultAsync(
                i => i.LibraryId == command.LibraryId && i.Id == command.Id && i.DeletedAt == null, cancellationToken);
            if (item is null)
            {
                return ItemErrors.NotFound;
            }

            // A private release of another library looks exactly like one that doesn't exist.
            if (!await ItemProjection.CanUseReleaseAsync(db, command.LibraryId, releaseId, cancellationToken))
            {
                return ItemErrors.ReleaseNotFound;
            }

            item.ReleaseId = releaseId;
            return await SaveAndProjectAsync(db, clock, users, command.LibraryId, item, cancellationToken);
        }
    }

    /// <summary>
    /// Use case: verify an item's edition from its barcode (docs/16 §11). A barcode of the item's own work swaps the
    /// release in place — copies, location, notes and lending stay — verifies the edition and teaches the catalogue;
    /// a barcode of anything else is refused naming the owning title, and nothing changes.
    /// </summary>
    public static class UpgradeFromBarcode
    {
        public sealed record Command(Guid LibraryId, Guid Id, string Barcode);

        public sealed class Handler(
            IAppDbContext db,
            TimeProvider clock,
            CandidateResolver candidates,
            BarcodeResolver external,
            ReleaseStore releases,
            BarcodeLearner learner,
            IUserDirectory? users = null)
        {
            public async Task<Result<Versioned<ItemDto>>> HandleAsync(Command command, CancellationToken cancellationToken)
            {
                var item = await db.Items.FirstOrDefaultAsync(
                    i => i.LibraryId == command.LibraryId && i.Id == command.Id && i.DeletedAt == null, cancellationToken);
                if (item is null)
                {
                    return ItemErrors.NotFound;
                }

                if (!Identifier.TryParse(command.Barcode, out var id))
                {
                    return ItemErrors.Invalid("item.barcode_invalid", "That code couldn't be read. Scan it again.", "barcode");
                }

                var resolved = await ResolveAsync(id, cancellationToken);
                if (!resolved.IsSuccess)
                {
                    return resolved.Error!;
                }

                var releaseId = resolved.Value;

                // Through the tracker: a release the providers just named exists only in this unit of work until the save
                // below. A private release of another library looks exactly like one that doesn't exist.
                var next = await ItemProjection.LoadReleaseWithWorkAsync(db, releaseId, cancellationToken);
                if (next is null || (next.Release.OwnerLibraryId is not null && next.Release.OwnerLibraryId != command.LibraryId))
                {
                    return ItemErrors.ReleaseNotFound;
                }

                var current = await ItemProjection.LoadReleaseWithWorkAsync(db, item.ReleaseId, cancellationToken);
                if (current is null)
                {
                    return ItemErrors.ReleaseNotFound;
                }

                if (current.Work.Id != next.Work.Id)
                {
                    return new DomainError(
                        ErrorKind.Conflict,
                        "item.barcode_other_work",
                        $"That barcode belongs to {TitleCaser.ToTitleCase(next.Work.Title)}.");
                }

                item.ReleaseId = releaseId;
                item.EditionVerified = true;
                await learner.LearnAsync(releaseId, id, cancellationToken);
                return await SaveAndProjectAsync(db, clock, users, command.LibraryId, item, cancellationToken);
            }

            /// <summary>
            /// The release the barcode names: the catalogue first (a known code spends no provider call), then the
            /// providers. Barcode-proof means exactly one release, the T0 bar: a shared barcode or a close call is
            /// refused rather than guessed at.
            /// </summary>
            private async Task<Result<Guid>> ResolveAsync(Identifier id, CancellationToken cancellationToken)
            {
                var local = await candidates.FindLocalAsync([id], cancellationToken);
                if (local.TryGetValue(id.Value, out var releaseIds))
                {
                    return releaseIds.Count == 1
                        ? releaseIds[0]
                        : new DomainError(
                            ErrorKind.Conflict,
                            "item.barcode_ambiguous",
                            "That barcode matches several editions, so it can't verify this one on its own.");
                }

                var answer = await external.ResolveAsync(id, cancellationToken);
                if (answer.Verdict == ResolutionVerdict.Pending)
                {
                    return new DomainError(
                        ErrorKind.Conflict, "item.barcode_pending", "The catalog couldn't answer in time. Scan it again.");
                }

                if (answer.Verdict == ResolutionVerdict.NotFound)
                {
                    return new DomainError(ErrorKind.NotFound, "item.barcode_not_found", "No edition has that barcode yet.");
                }

                if (answer.Verdict != ResolutionVerdict.Matched || answer.Options.Count != 1)
                {
                    return new DomainError(
                        ErrorKind.Conflict,
                        "item.barcode_ambiguous",
                        "That barcode matches several editions, so it can't verify this one on its own.");
                }

                var option = answer.Options[0];
                return option.LocalReleaseId
                    ?? (await releases.EnsureAsync(option.Provider!, cancellationToken)).Id;
            }
        }
    }

    private static async Task<Result<Versioned<ItemDto>>> SaveAndProjectAsync(
        IAppDbContext db, TimeProvider clock, IUserDirectory? users, Guid libraryId, Item item, CancellationToken cancellationToken)
    {
        item.UpdatedAt = clock.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ItemErrors.PreconditionFailed;
        }

        var tagIds = await ItemProjection.TagIdsAsync(db, libraryId, item.Id, cancellationToken);
        return new Versioned<ItemDto>(await ItemProjection.ToDtoAsync(item, tagIds, users, cancellationToken), item.Version);
    }
}
