using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.AppDownload;

namespace CollectionMaxxing.Api.AppDownload;

/// <summary>
/// Settings > Get the iPhone app (OPS-4). The deployer syncs the checked .ipa into the files volume; these routes hand it to anyone signed in
/// (any role, the same people who can read the library). The file is fixed: nothing in the request chooses what is read.
/// </summary>
public static class AppDownloadEndpoints
{
    public static IEndpointRouteBuilder MapAppDownloadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/app/ipa/info", (HttpContext http, GetIpaInfo.Handler handler) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return Results.Ok(handler.Handle());
            })
            .WithName("GetIpaInfo").WithTags("App")
            .Produces<IpaStatusDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        static IResult Download(HttpContext http, OpenIpa.Handler handler)
        {
            if (handler.Handle() is not { } ipa)
            {
                return IpaErrors.NotAvailable.ToProblem();
            }

            http.Response.Headers.CacheControl = "no-store";
            return Results.File(ipa.Content, "application/octet-stream", "CollectionMaxxing.ipa", ipa.Info.ModifiedAt, enableRangeProcessing: true);
        }

        app.MapGet("/api/v1/app/ipa", Download)
            .WithName("DownloadIpa").WithTags("App")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        // HEAD answers the size and date (Content-Length, Last-Modified) without the body. Its own route, so the API description keeps one operation.
        app.MapMethods("/api/v1/app/ipa", [HttpMethods.Head], Download)
            .ExcludeFromDescription()
            .RequireAuthorization();

        return app;
    }
}
