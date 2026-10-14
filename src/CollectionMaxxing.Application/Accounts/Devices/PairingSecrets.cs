using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Accounts.Devices;

/// <summary>How pairing tokens and codes look, how they are stored (a hash only) and the rules around them (docs/07, "iPhone: pair once").</summary>
public static class PairingSecrets
{
    /// <summary>A pairing is good for two minutes from the moment the web app makes it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    /// <summary>Wrong guesses of the 6-digit code a pairing survives. The fifth locks it: even the right code then fails.</summary>
    public const int MaxWrongGuesses = 5;

    public const int CodeLength = 6;

    /// <summary>A random token for the QR code: 256 bits, URL-safe text.</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>A random 6-digit code (leading zeros kept) for typing in when the camera can't read the QR code.</summary>
    public static string NewCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    /// <summary>
    /// What the database stores. The token is random and long, so a plain SHA-256 is enough; the point is that a copy of the database
    /// can't pair a phone. (The code has only a million values, but a pairing lives for two minutes and locks after five wrong guesses.)
    /// </summary>
    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>The digits of a typed code (spaces and dashes ignored), or null if it isn't a 6-digit code.</summary>
    public static string? NormalizeCode(string? typed)
    {
        var compact = new string((typed ?? string.Empty).Where(c => c is not (' ' or '-')).ToArray());
        return compact.Length == CodeLength && compact.All(char.IsAsciiDigit) ? compact : null;
    }
}

/// <summary>Why a pairing or a device session request was refused.</summary>
public static class PairingErrors
{
    /// <summary>Wrong, used, expired and locked all look the same to the phone: it says nothing about which.</summary>
    public static readonly DomainError Invalid =
        new(ErrorKind.Validation, "pairing.invalid", "That pairing isn't valid any more. Make a new one in the web app.");

    public static readonly DomainError Missing =
        new(ErrorKind.Validation, "pairing.missing", "Send the pairing token or the 6-digit code.");

    /// <summary>The signed-in user has no account (any more): it was deleted while they were signed in.</summary>
    public static readonly DomainError UserGone =
        new(ErrorKind.NotFound, "user.gone", "That account doesn't exist.");

    /// <summary>The session doesn't exist, or it is somebody else's. The two look the same on purpose.</summary>
    public static readonly DomainError SessionNotFound =
        new(ErrorKind.NotFound, "session.not_found", "That device doesn't exist.");
}
