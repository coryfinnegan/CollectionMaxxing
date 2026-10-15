using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Application.Audit;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Operations;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Audit;

/// <summary>Settings for the audit trail.</summary>
public sealed class AuditOptions
{
    /// <summary>
    /// Secret salt for hashing addresses (configuration key <c>Audit:IpSalt</c>). Without one, a random salt is made per
    /// process: hashes then only match within one run, which is the safe default.
    /// </summary>
    public string? IpSalt { get; set; }
}

public sealed class AuditLog(
    IAppDbContext db,
    ICurrentUser user,
    IRequestOrigin origin,
    TimeProvider clock,
    IIdGenerator ids,
    IOptions<AuditOptions> options) : IAuditLog
{
    private static readonly byte[] ProcessSalt = RandomNumberGenerator.GetBytes(32);

    public void Record(string action, string? entityType, Guid? entityId, Guid? libraryId, IReadOnlyDictionary<string, object>? details = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        string? detailsJson = null;
        if (details is { Count: > 0 })
        {
            foreach (var (key, value) in details)
            {
                if (value is not (Guid or int or long or bool))
                {
                    throw new ArgumentException($"Audit detail '{key}' must be an id, a count or a flag, not free text.", nameof(details));
                }
            }

            detailsJson = JsonSerializer.Serialize(details);
        }

        db.AuditEvents.Add(new AuditEvent
        {
            Id = ids.NewId(),
            ActorUserId = user.UserId,
            LibraryId = libraryId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            At = clock.GetUtcNow(),
            IpHash = Hash(origin.Address),
            DetailsJson = detailsJson,
        });
    }

    private string? Hash(string? address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return null;
        }

        var key = string.IsNullOrEmpty(options.Value.IpSalt) ? ProcessSalt : Encoding.UTF8.GetBytes(options.Value.IpSalt);
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(address)));
    }
}

public sealed class NoRequestOrigin : IRequestOrigin
{
    public string? Address => null;
}
