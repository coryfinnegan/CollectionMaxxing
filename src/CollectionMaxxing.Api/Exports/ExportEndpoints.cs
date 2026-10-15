using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Exports;
using CollectionMaxxing.Infrastructure.Exports;
using Microsoft.AspNetCore.DataProtection;

namespace CollectionMaxxing.Api.Exports;

/// <summary>
/// Export my data (docs/04, "Account and session"). <c>GET /me/export</c> starts a background job and answers 202 with a handle;
/// <c>GET /me/export/{id}</c> polls it and, once ready, hands out a short-lived download link; the link itself needs the same
/// sign-in as everything else and only works for the person it was made for.
/// </summary>
public static class ExportEndpoints
{
    /// <summary>Registers this feature's handlers, file store and jobs.</summary>
    public static IServiceCollection AddExports(this IServiceCollection services)
    {
        services.AddExportStorage();
        services.AddSingleton<ExportLinks>();
        services.AddScoped<StartExport.Handler>();
        services.AddScoped<GetExport.Handler>();
        services.AddScoped<OpenExport.Handler>();
        return services;
    }

    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/me/export", async (ICurrentUser user, StartExport.Handler handler, CancellationToken cancellationToken) =>
            {
                var started = await handler.HandleAsync(user.UserId!.Value, cancellationToken);
                return Results.Accepted($"/api/v1/me/export/{started.JobId}", started);
            })
            .WithName("StartExport").WithTags("Account")
            .Produces<ExportDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        app.MapGet("/api/v1/me/export/{id:guid}", async (Guid id, ICurrentUser user, GetExport.Handler handler, ExportLinks links, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(user.UserId!.Value, id, cancellationToken);
                if (!result.IsSuccess)
                {
                    return result.Error!.ToProblem();
                }

                var export = result.Value;
                return Results.Ok(export.Status == ExportStatus.Ready ? export with { DownloadUrl = links.Create(id, user.UserId!.Value) } : export);
            })
            .WithName("GetExport").WithTags("Account")
            .Produces<ExportDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        app.MapGet("/api/v1/me/export/{id:guid}/download", async (Guid id, string? token, HttpContext http, ICurrentUser user, OpenExport.Handler handler, ExportLinks links, CancellationToken cancellationToken) =>
            {
                var userId = user.UserId!.Value;
                var stream = links.IsValid(token, id, userId) ? await handler.HandleAsync(userId, id, cancellationToken) : null;
                if (stream is null)
                {
                    return ExportErrors.NotFound.ToProblem();
                }

                http.Response.Headers.CacheControl = "no-store";
                return Results.File(stream, "application/zip", "collectionmaxxing-export.zip");
            })
            .WithName("DownloadExport").WithTags("Account")
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        return app;
    }
}

/// <summary>
/// Signs download links with ASP.NET Data Protection: a link says "this export, for this person, until this moment" and nobody can forge or
/// extend one. It is not a login: the request still has to be signed in as that person.
/// </summary>
public sealed class ExportLinks(IDataProtectionProvider dataProtection, ExportSettings settings, TimeProvider time)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("CollectionMaxxing.Export.v1");

    public string Create(Guid exportId, Guid userId)
    {
        var expires = (time.GetUtcNow() + settings.LinkLifetime).ToUnixTimeSeconds();
        var token = _protector.Protect($"{exportId:N}|{userId:N}|{expires}");
        return $"/api/v1/me/export/{exportId}/download?token={Uri.EscapeDataString(token)}";
    }

    public bool IsValid(string? token, Guid exportId, Guid userId)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        try
        {
            var parts = _protector.Unprotect(token).Split('|');
            return parts.Length == 3
                && parts[0] == exportId.ToString("N")
                && parts[1] == userId.ToString("N")
                && long.TryParse(parts[2], out var expires)
                && time.GetUtcNow().ToUnixTimeSeconds() < expires;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }
}
