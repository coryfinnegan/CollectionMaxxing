using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace CollectionMaxxing.Api.Tests.Auth;

/// <summary>
/// A WebAuthn authenticator in software, standing in for Windows Hello or Face ID: it makes the credential JSON a browser
/// would send for a passkey creation (attestation "none") and for a sign-in (assertion). The server verifies these for real.
/// </summary>
public sealed class SoftwareAuthenticator(string rpId = "localhost", string origin = "https://localhost")
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private uint _signCount;

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);

    public string CredentialIdText => WebEncoders.Base64UrlEncode(CredentialId);

    /// <summary>The user handle the server gave when this passkey was created (returned on sign-in).</summary>
    public string? UserHandle { get; private set; }

    /// <summary>Answers the creation options a server sent with a new passkey, like <c>navigator.credentials.create()</c>.</summary>
    public string Create(string creationOptionsJson)
    {
        using var options = JsonDocument.Parse(creationOptionsJson);
        var challenge = options.RootElement.GetProperty("challenge").GetString()!;
        UserHandle = options.RootElement.GetProperty("user").GetProperty("id").GetString();

        var clientData = ClientData("webauthn.create", challenge);
        var authData = AuthenticatorData(flags: 0x45, includeCredential: true);
        var attestationObject = AttestationObject(authData);

        return JsonSerializer.Serialize(new
        {
            id = CredentialIdText,
            rawId = CredentialIdText,
            type = "public-key",
            authenticatorAttachment = "platform",
            clientExtensionResults = new { },
            response = new
            {
                clientDataJSON = WebEncoders.Base64UrlEncode(clientData),
                attestationObject = WebEncoders.Base64UrlEncode(attestationObject),
                transports = new[] { "internal" },
            },
        });
    }

    /// <summary>Answers a sign-in request, like <c>navigator.credentials.get()</c>.</summary>
    public string Sign(string requestOptionsJson)
    {
        using var options = JsonDocument.Parse(requestOptionsJson);
        var challenge = options.RootElement.GetProperty("challenge").GetString()!;

        var clientData = ClientData("webauthn.get", challenge);
        var authData = AuthenticatorData(flags: 0x05, includeCredential: false);
        var signedData = authData.Concat(SHA256.HashData(clientData)).ToArray();
        var signature = _key.SignData(signedData, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return JsonSerializer.Serialize(new
        {
            id = CredentialIdText,
            rawId = CredentialIdText,
            type = "public-key",
            authenticatorAttachment = "platform",
            clientExtensionResults = new { },
            response = new
            {
                clientDataJSON = WebEncoders.Base64UrlEncode(clientData),
                authenticatorData = WebEncoders.Base64UrlEncode(authData),
                signature = WebEncoders.Base64UrlEncode(signature),
                userHandle = UserHandle,
            },
        });
    }

    private byte[] ClientData(string type, string challenge) =>
        JsonSerializer.SerializeToUtf8Bytes(new { type, challenge, origin, crossOrigin = false });

    private byte[] AuthenticatorData(byte flags, bool includeCredential)
    {
        var data = new List<byte>();
        data.AddRange(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
        data.Add(flags);
        var counter = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(counter, ++_signCount);
        data.AddRange(counter);
        if (includeCredential)
        {
            data.AddRange(new byte[16]); // AAGUID: none
            var length = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)CredentialId.Length);
            data.AddRange(length);
            data.AddRange(CredentialId);
            data.AddRange(CoseKey());
        }

        return [.. data];
    }

    /// <summary>The public key as a COSE_Key map: kty EC2, alg ES256, crv P-256, x, y.</summary>
    private byte[] CoseKey()
    {
        var parameters = _key.ExportParameters(includePrivateParameters: false);
        var cose = new List<byte> { 0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20 };
        cose.AddRange(parameters.Q.X!);
        cose.AddRange([0x22, 0x58, 0x20]);
        cose.AddRange(parameters.Q.Y!);
        return [.. cose];
    }

    /// <summary>{ "fmt": "none", "attStmt": {}, "authData": h'…' } as CBOR.</summary>
    private static byte[] AttestationObject(byte[] authData)
    {
        var cbor = new List<byte> { 0xA3 };
        cbor.Add(0x63);
        cbor.AddRange("fmt"u8.ToArray());
        cbor.Add(0x64);
        cbor.AddRange("none"u8.ToArray());
        cbor.Add(0x67);
        cbor.AddRange("attStmt"u8.ToArray());
        cbor.Add(0xA0);
        cbor.Add(0x68);
        cbor.AddRange("authData"u8.ToArray());
        cbor.AddRange([0x59, (byte)(authData.Length >> 8), (byte)authData.Length]);
        cbor.AddRange(authData);
        return [.. cbor];
    }
}
