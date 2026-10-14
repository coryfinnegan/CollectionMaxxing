using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Identifiers;

namespace CollectionMaxxing.Application.Ownership;

/// <summary>Use case: "do I own this?" by barcode, release or work. Give exactly one.</summary>
public static class GetOwnership
{
    public sealed record Query(Guid LibraryId, string? Code, Guid? ReleaseId, Guid? WorkId);

    public sealed class Handler(OwnershipQuery ownership)
    {
        public async Task<Result<OwnershipDto>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            var given = (string.IsNullOrWhiteSpace(query.Code) ? 0 : 1) + (query.ReleaseId is null ? 0 : 1) + (query.WorkId is null ? 0 : 1);
            if (given != 1)
            {
                return new DomainError(
                    ErrorKind.Validation, "ownership.one_key_required", "Give exactly one of code, releaseId or workId.", "code");
            }

            if (query.ReleaseId is { } releaseId)
            {
                return await ownership.ForReleaseAsync(query.LibraryId, releaseId, cancellationToken);
            }

            if (query.WorkId is { } workId)
            {
                return await ownership.ForWorkAsync(query.LibraryId, workId, cancellationToken);
            }

            if (!Identifier.TryParse(query.Code, out var identifier))
            {
                return new DomainError(
                    ErrorKind.Validation, "ownership.code_invalid", "That isn't a valid barcode. Check the digits and try again.", "code");
            }

            return await ownership.ForCodeAsync(query.LibraryId, identifier, cancellationToken);
        }
    }
}
