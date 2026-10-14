using System.Globalization;
using CollectionMaxxing.Application.Accounts.Devices;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CollectionMaxxing.Infrastructure.Identity.Pairing;

/// <summary>
/// A paired phone is one OpenIddict authorization of the user; every access and refresh token of the phone hangs from it. Revoking
/// the authorization kills them all, and the API checks the authorization on each request, so that takes effect at once.
/// </summary>
internal sealed class OpenIddictDeviceAuthorizations(
    AppDbContext db,
    IOpenIddictAuthorizationManager authorizations,
    IOpenIddictTokenManager tokens,
    TimeProvider time) : IDeviceAuthorizations
{
    /// <summary>The scope of the API (the same value as <c>OpenIddictSetup.ApiScope</c>).</summary>
    private const string ApiScope = "api";

    public async Task<Guid?> CreateAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!await UserIsActiveAsync(userId, cancellationToken))
        {
            return null;
        }

        var authorization = await authorizations.CreateAsync(
            new OpenIddictAuthorizationDescriptor
            {
                Subject = Subject(userId),
                Type = AuthorizationTypes.Permanent,
                Status = Statuses.Valid,
                CreationDate = time.GetUtcNow(),
                Scopes = { ApiScope },
            },
            cancellationToken);
        return Guid.Parse((await authorizations.GetIdAsync(authorization, cancellationToken))!);
    }

    public Task<bool> UserIsActiveAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(u => u.Id == userId && u.DeletedAt == null, cancellationToken);

    public async Task<IReadOnlyList<DeviceSession>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        var found = new List<(Guid Id, DateTimeOffset Created, DateTimeOffset? LastSeen, bool Revoked)>();
        var archivedNames = new Dictionary<Guid, string>();
        await foreach (var authorization in authorizations.FindBySubjectAsync(Subject(userId), cancellationToken))
        {
            var idText = (await authorizations.GetIdAsync(authorization, cancellationToken))!;
            var created = await authorizations.GetCreationDateAsync(authorization, cancellationToken) ?? DateTimeOffset.MinValue;
            var revoked = await authorizations.GetStatusAsync(authorization, cancellationToken) != Statuses.Valid;

            // The phone was last heard from when it last got a token (it refreshes about every ten minutes while the app is in use).
            DateTimeOffset? lastSeen = null;
            var anyToken = false;
            var anyUsableToken = false;
            await foreach (var token in tokens.FindByAuthorizationIdAsync(idText, cancellationToken))
            {
                var issued = await tokens.GetCreationDateAsync(token, cancellationToken);
                lastSeen = issued > lastSeen || lastSeen is null ? issued : lastSeen;
                anyToken = true;
                anyUsableToken |= await tokens.GetStatusAsync(token, cancellationToken) != Statuses.Revoked;
            }

            // A replayed refresh token revokes every token of the phone but leaves a permanent authorization standing, so a phone
            // whose tokens have all been revoked is a revoked phone.
            found.Add((Guid.Parse(idText), created, lastSeen, revoked || (anyToken && !anyUsableToken)));
            if (await DeviceNames.ReadAsync(authorizations, authorization, cancellationToken) is { } archived)
            {
                archivedNames[Guid.Parse(idText)] = archived;
            }
        }

        var ids = found.Select(f => f.Id).ToList();
        var names = await db.DevicePairings.AsNoTracking()
            .Where(p => p.AuthorizationId != null && ids.Contains(p.AuthorizationId.Value))
            .Select(p => new { Id = p.AuthorizationId!.Value, p.DeviceName })
            .ToDictionaryAsync(p => p.Id, p => p.DeviceName, cancellationToken);

        return [.. found.Select(f => new DeviceSession(
            f.Id,
            names.GetValueOrDefault(f.Id) ?? archivedNames.GetValueOrDefault(f.Id) ?? RedeemPairing.DefaultDeviceName,
            f.Created,
            f.LastSeen,
            f.Revoked))];
    }

    public async Task<bool> RevokeAsync(Guid userId, Guid authorizationId, CancellationToken cancellationToken)
    {
        var idText = authorizationId.ToString(null, CultureInfo.InvariantCulture);
        var authorization = await authorizations.FindByIdAsync(idText, cancellationToken);
        if (authorization is null || await authorizations.GetSubjectAsync(authorization, cancellationToken) != Subject(userId))
        {
            return false;
        }

        await authorizations.TryRevokeAsync(authorization, cancellationToken);
        await tokens.RevokeByAuthorizationIdAsync(idText, cancellationToken);
        return true;
    }

    /// <summary>The OpenIddict <c>sub</c> of a user: their id as plain lowercase text.</summary>
    private static string Subject(Guid userId) => userId.ToString(null, CultureInfo.InvariantCulture);
}
