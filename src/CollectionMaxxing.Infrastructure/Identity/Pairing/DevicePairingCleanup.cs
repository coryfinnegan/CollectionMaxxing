using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;

namespace CollectionMaxxing.Infrastructure.Identity.Pairing;

/// <summary>Settings under <c>Pairing:Cleanup:</c>.</summary>
public sealed class PairingCleanupOptions
{
    public const string SectionName = "Pairing:Cleanup";

    /// <summary>How long a spent or expired pairing row is kept before it is removed.</summary>
    public int RetentionHours { get; set; } = 24;

    /// <summary>How often the cleanup looks.</summary>
    public int IntervalMinutes { get; set; } = 30;
}

/// <summary>
/// Removes spent and expired <c>DevicePairing</c> rows once they are older than the retention window. Open pairings are never
/// touched. A spent row is where a phone's name is remembered, so the name is copied onto the phone's authorization before the row
/// goes, and the devices list reads it from there afterwards.
/// </summary>
internal sealed class DevicePairingCleanup(
    AppDbContext db,
    TimeProvider time,
    IOptions<PairingCleanupOptions> options) : IDevicePairingCleanup
{
    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow() - TimeSpan.FromHours(Math.Max(1, options.Value.RetentionHours));
        var old = db.DevicePairings.Where(p =>
            (p.UsedAt != null && p.UsedAt < cutoff) || (p.UsedAt == null && p.ExpiresAt < cutoff));

        var names = await old
            .Where(p => p.AuthorizationId != null && p.DeviceName != null)
            .Select(p => new { Id = p.AuthorizationId!.Value, Name = p.DeviceName! })
            .ToListAsync(cancellationToken);
        foreach (var device in names)
        {
            await DeviceNames.SaveAsync(db, device.Id, device.Name, cancellationToken);
        }

        return await old.ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>
/// Where a phone's name lives once the pairing row that first held it has been cleaned up: in the properties of the phone's OpenIddict
/// authorization. It is written with one statement instead of through OpenIddict's manager, because the manager rewrites the whole
/// authorization (and fails on a stale copy), while this touches nothing but the one property.
/// </summary>
internal static class DeviceNames
{
    private const string Key = "device_name";

    public static async Task SaveAsync(AppDbContext db, Guid authorizationId, string name, CancellationToken cancellationToken)
    {
        var current = await db.Database
            .SqlQuery<string?>($"SELECT properties AS \"Value\" FROM openiddict_authorizations WHERE id = {authorizationId}")
            .FirstOrDefaultAsync(cancellationToken);
        var properties = string.IsNullOrWhiteSpace(current) ? [] : JsonNode.Parse(current)?.AsObject() ?? [];
        properties[Key] = name;
        var json = properties.ToJsonString();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE openiddict_authorizations SET properties = {json} WHERE id = {authorizationId}",
            cancellationToken);
    }

    public static async Task<string?> ReadAsync(IOpenIddictAuthorizationManager authorizations, object authorization, CancellationToken cancellationToken)
    {
        ImmutableDictionary<string, JsonElement> properties = await authorizations.GetPropertiesAsync(authorization, cancellationToken);
        return properties.TryGetValue(Key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}

/// <summary>Runs <see cref="IDevicePairingCleanup"/> every <see cref="PairingCleanupOptions.IntervalMinutes"/> minutes for as long as the app is up.</summary>
internal sealed partial class PairingCleanupService(
    IServiceScopeFactory scopes,
    TimeProvider time,
    IOptions<PairingCleanupOptions> options,
    ILogger<PairingCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Made before the first await, so the clock is already ticking when the host has started.
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.IntervalMinutes)), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The app is stopping.
        }
    }

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var removed = await scope.ServiceProvider.GetRequiredService<IDevicePairingCleanup>().PurgeAsync(stoppingToken);
            if (removed > 0)
            {
                LogRemoved(removed);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only the type: exception text can carry SQL and values. The next sweep tries again.
            LogFailed(ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} old pairing rows.")]
    private partial void LogRemoved(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cleaning up old pairing rows failed ({Reason}); it will be tried again.")]
    private partial void LogFailed(string reason);
}
