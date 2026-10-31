using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Evals;

/// <summary>Use case: list the pictures in the golden folder (<c>GET /api/admin/evals/golden</c>).</summary>
public static class ListGoldenPhotos
{
    public sealed class Handler(IGoldenSet golden)
    {
        public Result<IReadOnlyList<GoldenPhotoDto>> Handle() =>
            golden.IsAvailable ? Result<IReadOnlyList<GoldenPhotoDto>>.Success(golden.List()) : EvalErrors.NotFound;
    }
}

/// <summary>
/// Use case: one golden picture's bytes (<c>GET /api/admin/evals/golden/{id}/image</c>). The id must be a plain id that the
/// folder's own listing has; anything else is a 404 and nothing is opened.
/// </summary>
public static class GetGoldenImage
{
    public sealed class Handler(IGoldenSet golden)
    {
        public Result<GoldenImage> Handle(string? id)
        {
            if (!golden.IsAvailable || !EvalIds.IsValid(id))
            {
                return EvalErrors.NotFound;
            }

            return golden.OpenImage(id!) is { } image ? image : EvalErrors.NotFound;
        }
    }
}

/// <summary>
/// Use case: add one golden picture from the phone (<c>POST /api/admin/evals/golden/{set}/{id}</c>). The API reads the bytes, enforces the size
/// cap and decides the kind from them; this checks the names and has the folder create the file.
/// </summary>
public static class AddGoldenPhoto
{
    public sealed class Handler(IGoldenSet golden)
    {
        /// <summary>What can be known before the body is read: the evals are on, the set is one of the two, and the id is a usable name.</summary>
        public Result Check(string? set, string? id)
        {
            if (!golden.IsAvailable)
            {
                return Result.Failure(EvalErrors.NotFound);
            }

            if (set is null || !GoldenSets.All.Contains(set))
            {
                return Result.Failure(EvalErrors.SetInvalid);
            }

            return EvalIds.IsValid(id) && !GoldenPhotoNames.IsReserved(id!) ? Result.Success() : Result.Failure(EvalErrors.IdInvalid);
        }

        public Result<GoldenPhotoDto> Handle(string? set, string? id, GoldenPhotoFormat format, ReadOnlyMemory<byte> content)
        {
            var check = Check(set, id);
            if (!check.IsSuccess)
            {
                return check.Error!;
            }

            var added = golden.Add(set!, id!, format, content);
            return added.Outcome switch
            {
                GoldenAddOutcome.Added => added.Photo!,
                GoldenAddOutcome.IdTaken => EvalErrors.IdTaken,
                GoldenAddOutcome.Unavailable => EvalErrors.NotFound,
                _ => EvalErrors.GoldenNotWritable,
            };
        }
    }
}

/// <summary>Use case: the summary of the latest uploaded run (<c>GET /api/admin/evals/runs/latest</c>).</summary>
public static class GetLatestEvalRun
{
    public sealed class Handler(IGoldenSet golden, IEvalResults results)
    {
        public Result<EvalSummary> Handle()
        {
            if (!golden.IsAvailable)
            {
                return EvalErrors.NotFound;
            }

            return results.LoadLatestSummary() is { } summary ? summary : EvalErrors.NoRunYet;
        }
    }
}
