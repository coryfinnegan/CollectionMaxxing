using System.Security.Cryptography;
using System.Text;
using CollectionMaxxing.Domain.Accounts;
using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Accounts;

/// <summary>How one-time codes look, how they are stored (a hash only) and how long they last.</summary>
public static class OneTimeCodes
{
    // No 0/O or 1/I/L: the code is read off a screen and typed by a person.
    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    private const int Groups = 4;
    private const int GroupLength = 4;

    public static TimeSpan LifetimeOf(SetupCodePurpose purpose) => purpose switch
    {
        SetupCodePurpose.Setup => TimeSpan.FromHours(24),
        SetupCodePurpose.Recovery => TimeSpan.FromMinutes(30),
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

    /// <summary>A new random code such as <c>7KQ2-M9XD-4HTA-RW8E</c>: 16 characters from a 31-letter alphabet, about 79 bits.</summary>
    public static string Generate()
    {
        var groups = Enumerable.Range(0, Groups)
            .Select(_ => string.Create(GroupLength, 0, static (span, _) =>
            {
                for (var i = 0; i < span.Length; i++)
                {
                    span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
                }
            }));
        return string.Join('-', groups);
    }

    /// <summary>Ignores case, dashes and spaces, so how it was typed doesn't matter.</summary>
    public static string Normalize(string code) =>
        new(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>
    /// What the database stores. The codes are random and long, so a plain SHA-256 is enough (there is nothing to
    /// brute-force); the point of hashing is that a copy of the database can't be used to set up or recover the server.
    /// </summary>
    public static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code))));
}

/// <summary>Why a code, or the ceremony it started, was refused.</summary>
public static class AccountErrors
{
    public static readonly DomainError CodeRequired =
        new(ErrorKind.Validation, "code.required", "Enter the code.", "code");

    public static readonly DomainError CodeInvalid =
        new(ErrorKind.Validation, "code.invalid", "That code isn't right.", "code");

    public static readonly DomainError CodeExpired =
        new(ErrorKind.Conflict, "code.expired", "That code has expired. Ask the server for a new one.");

    public static readonly DomainError CodeUsed =
        new(ErrorKind.Conflict, "code.used", "That code has already been used.");

    public static readonly DomainError SetupClosed =
        new(ErrorKind.NotFound, "setup.closed", "Setup is finished.");

    public static readonly DomainError NoOwner =
        new(ErrorKind.NotFound, "recovery.no_owner", "There is no account to recover yet.");

    public static readonly DomainError AlreadyLinked =
        new(ErrorKind.Conflict, "account.already_linked", "That sign-in already belongs to an account.");

    public static readonly DomainError SignInMethodNotFound =
        new(ErrorKind.NotFound, "account.sign_in_method_not_found", "That sign-in method doesn't exist.");

    public static readonly DomainError LastSignInMethod =
        new(ErrorKind.Conflict, "account.last_sign_in_method", "That is your only way to sign in. Add another before removing it, or you'd be locked out.");
}
