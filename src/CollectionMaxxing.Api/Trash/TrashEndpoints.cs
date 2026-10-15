using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Libraries;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Audit;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Trash;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.AspNetCore.Mvc;

namespace CollectionMaxxing.Api.Trash;

/// <summary>The "Recently deleted" bin (docs/04, "Items"). Restoring reuses <c>POST .../items/{id}:restore</c>.</summary>
public static class TrashEndpoints
{
    public static IServiceCollection AddTrash(this IServiceCollection services)
    {
        // Program.cs also calls AddAudit() and AddTrashPurge() (Infrastructure); endpoint classes may not reference Infrastructure.
        services.AddHttpContextAccessor();
        services.AddScoped<IRequestOrigin, HttpRequestOrigin>();
        services.AddScoped<ListDeletedItems.Handler>();
        return services;
    }

    public static IEndpointRouteBuilder MapTrashEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapLibraryGroup().MapGet("/items:deleted", List)
            .WithName("ListDeletedItems").WithTags("Trash")
            .Produces<Page<DeletedItemDto>>()
            .ProducesValidationProblem()
            .RequireLibraryRole(LibraryRole.Editor);
        return app;
    }

    private static async Task<IResult> List(
        Guid libraryId, [FromQuery] int? limit, [FromQuery] string? cursor, ListDeletedItems.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ListDeletedItems.Query(libraryId, limit, cursor), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }

    private sealed class HttpRequestOrigin(IHttpContextAccessor accessor) : IRequestOrigin
    {
        public string? Address => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
    }
}
