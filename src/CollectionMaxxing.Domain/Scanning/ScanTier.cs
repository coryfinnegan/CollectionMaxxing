using System.Text.Json.Serialization;

namespace CollectionMaxxing.Domain.Scanning;

/// <summary>
/// The confidence tier of a scanned candidate (docs/16 §6): T0 auto-adds a barcode-proof hit, T1 will auto-add a
/// high-confidence spine or cover (issue #343), and T2 is reviewed by the person. Spelled as the design writes it.
/// </summary>
public enum ScanTier
{
    [JsonStringEnumMemberName("T0")]
    T0,

    [JsonStringEnumMemberName("T1")]
    T1,

    [JsonStringEnumMemberName("T2")]
    T2,
}
