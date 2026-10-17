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
