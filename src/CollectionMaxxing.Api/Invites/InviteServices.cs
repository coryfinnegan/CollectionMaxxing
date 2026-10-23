using System.Text;
using System.Text.Json;
using CollectionMaxxing.Application.Invites;
using CollectionMaxxing.Infrastructure.Invites;

namespace CollectionMaxxing.Api.Invites;

/// <summary>The services behind invites and joining (kept out of the endpoint class, which never touches Infrastructure).</summary>
internal static class InviteServices
{
    public static IServiceCollection Add(IServiceCollection services)
    {
        services.AddInviteStores();
        services.AddScoped<CreateInvite.Handler>();
        services.AddScoped<ListInvites.Handler>();
        services.AddScoped<RevokeInvite.Handler>();
        services.AddScoped<GetInvitePreview.Handler>();
        return services;
    }
}

/// <summary>
/// The one answer for an invite token that can't be used. It is written from fixed text, with no trace id or anything else that
/// varies, so an unknown, an expired, a used and a revoked token are indistinguishable by body as well as by status.
/// </summary>
public static class InviteProblems
{
    private static readonly string Body = JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["type"] = Problems.ProblemResults.TypeFor(InviteErrors.Invalid.Code),
        ["title"] = "Not found",
        ["status"] = StatusCodes.Status404NotFound,
        ["detail"] = InviteErrors.Invalid.Message,
        ["code"] = InviteErrors.Invalid.Code,
    });

    public static IResult Invalid() =>
        Results.Text(Body, "application/problem+json", Encoding.UTF8, StatusCodes.Status404NotFound);
}
