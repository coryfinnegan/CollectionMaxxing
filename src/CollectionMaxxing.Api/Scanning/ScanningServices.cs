using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Api.Scanning.Events;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Ownership;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Photo;
using CollectionMaxxing.Infrastructure.Jobs;
using CollectionMaxxing.Infrastructure.Scanning;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Api.Scanning;

/// <summary>Registers the scanning feature. Kept apart from the endpoints class, which may not know Infrastructure.</summary>
public static class ScanningServices
{
    /// <summary>Registers this feature's handlers, ports and background job. Called from <c>Program.cs</c>.</summary>
    public static IServiceCollection AddScanning(this IServiceCollection services)
    {
        services.TryAddSingleton<ScanningOptions>();
        services.AddScanningStorage();

        // The ownership query is also registered by AddOwnership(); either order works.
        services.TryAddScoped<OwnershipQuery>();

        services.AddScoped<FindSessionLibrary.Handler>();
        services.AddScoped<IsMySession.Handler>();
        services.AddScoped<ReleaseStore>();
        services.AddScoped<BarcodeResolver>();
        services.AddScoped<TextSearchResolver>();
        services.AddScoped<CandidateResolver>();
        services.AddScoped<CreateScanSession.Handler>();
        services.AddScoped<GetScanSession.Handler>();
        services.AddScoped<SubmitBarcodes.Handler>();
        services.AddScoped<DecideCandidate.Handler>();
        services.AddScoped<SearchForCandidate.Handler>();
        services.AddScoped<CommitScanSession.Handler>();
        services.AddScoped<AbandonScanSession.Handler>();
        services.AddScoped<UploadCandidatePhoto.Handler>(); // SCAN-2c
        services.AddJobHandler<SubmitBarcodes.ResolveJob, ResolvePendingCandidate>(SubmitBarcodes.ResolveJobType);
        services.AddScanFollowUps();
        return services;
    }
}

/// <summary>
/// A scan session belongs to its library <em>and</em> to the user who started it. The library check
/// (<see cref="ScanSessionAccessFilter"/>) has already run; this answers 404 for a session some other member started, so
/// nobody (not even the library's owner) reads or changes another member's scanning. Applies to every endpoint in the
/// scan-session group, the ones not built yet included.
/// </summary>
public sealed class ScanSessionOwnerFilter(IsMySession.Handler mine) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // Runs ahead of the role filter, so keep failing closed here for an endpoint that forgot RequireScanSessionRole.
        if (http.GetEndpoint()?.Metadata.GetMetadata<RequireScanSessionRoleMetadata>() is null)
        {
            throw new InvalidOperationException(
                $"{http.GetEndpoint()?.DisplayName} is in the scan-session route group but doesn't call RequireScanSessionRole.");
        }

        if (!Guid.TryParse(http.Request.RouteValues["id"]?.ToString(), out var sessionId)
            || !await mine.HandleAsync(sessionId, http.RequestAborted))
        {
            return ProblemResults.Problem(StatusCodes.Status404NotFound, "Not found", "That scan session doesn't exist.");
        }

        return await next(context);
    }
}
