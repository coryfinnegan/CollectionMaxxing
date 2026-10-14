namespace CollectionMaxxing.Infrastructure.Identity.Pairing;

/// <summary>
/// The port behind the pairing housekeeping: removes <c>DevicePairing</c> rows that are spent or expired and older than the
/// retention window. Open pairings are never touched.
/// </summary>
public interface IDevicePairingCleanup
{
    /// <summary>Removes the old rows and returns how many went.</summary>
    Task<int> PurgeAsync(CancellationToken cancellationToken);
}
