using System.Buffers.Binary;
using CollectionMaxxing.Application.Accounts.Deletion;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Photo;
using CollectionMaxxing.Infrastructure.Catalog;
using CollectionMaxxing.Infrastructure.Persistence;
using CollectionMaxxing.Infrastructure.Providers.UpcItemDb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace CollectionMaxxing.Infrastructure.Scanning;

public static class ScanningStorage
{
    /// <summary>Registers the scanner's ports: the session lock (Postgres advisory locks) and the UPC product lookup (UPCitemdb).</summary>
    public static IServiceCollection AddScanningStorage(this IServiceCollection services)
    {
        services.AddScoped<ISessionLock, PostgresSessionLock>();
        services.AddScoped<IProductLookup, UpcProductLookup>();

        // Uploaded photos live in the cover folder. Deleting uses IAccountFiles (registered by AddAccountPurge, over the same folder).
        services.TryAddSingleton<IPhotoStore>(provider => new FilePhotoStore(
            provider.GetRequiredService<IConfiguration>()[CatalogInfrastructure.CoversRootKey] ?? Path.Combine("data", "covers"),
            provider.GetRequiredService<IAccountFiles>()));
        return services;
    }
}

/// <summary>
/// A Postgres advisory lock named after the session, held on a connection of its own for as long as the lock is held. It works
/// across requests and across processes, never blocks anything else, and is released when disposed (or when the connection
/// drops, so a crashed request can't leave a session locked).
/// </summary>
public sealed class PostgresSessionLock(AppDbContext db) : ISessionLock
{
    public async Task<IAsyncDisposable> AcquireAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var key = KeyFor(sessionId);
        var connection = new NpgsqlConnection(db.Database.GetConnectionString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("select pg_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", key);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new Held(connection, key);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>The first eight bytes of the session id: a 64-bit lock name that is the same for every process.</summary>
    internal static long KeyFor(Guid sessionId)
    {
        Span<byte> bytes = stackalloc byte[16];
        sessionId.TryWriteBytes(bytes);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes[..8]);
    }

    private sealed class Held(NpgsqlConnection connection, long key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand("select pg_advisory_unlock(@key)", connection);
                command.Parameters.AddWithValue("key", key);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
            finally
            {
                // Closing the connection also ends its session, which drops the lock if the unlock above could not run.
                await connection.DisposeAsync();
            }
        }
    }
}

/// <summary>UPCitemdb as the scanner's product lookup. It reports why there was no answer: unknown code, quota spent, or provider down.</summary>
public sealed class UpcProductLookup(UpcItemDbProvider upc) : IProductLookup
{
    public async Task<ProductLookupResult> LookupAsync(Domain.Identifiers.Identifier id, CancellationToken cancellationToken)
    {
        var result = await upc.LookupWithStatusAsync(id, cancellationToken);
        return result.Status switch
        {
            UpcLookupStatus.Found when result.Release is { } release => ProductLookupResult.Found(release),
            UpcLookupStatus.NotFound or UpcLookupStatus.Found => ProductLookupResult.NotFound,
            UpcLookupStatus.QuotaExhausted => ProductLookupResult.QuotaExhausted,
            _ => ProductLookupResult.Unavailable,
        };
    }
}
