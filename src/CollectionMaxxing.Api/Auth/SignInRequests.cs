using System.Text.Json;

namespace CollectionMaxxing.Api.Auth;

// Bodies of the passkey ceremony endpoints (docs/07). The browser's WebAuthn API produces the credential JSON.

/// <summary>Asks for WebAuthn options to create a passkey: while setting up the owner, joining by invite, recovering, or adding one.</summary>
public sealed record PasskeyCreationOptionsRequest(string? DisplayName = null, string? SetupCode = null, string? InviteToken = null);

/// <summary>Finishes creating a passkey with the credential the browser made. The invite token, if any, is in the <c>invite</c> query.</summary>
public sealed record PasskeyRegistrationRequest(JsonElement? Credential = null, string? Name = null);

/// <summary>The browser's passkey assertion, to sign in.</summary>
public sealed record PasskeyLoginRequest(JsonElement? Credential = null);

/// <summary>Body of <c>POST /setup</c>: the one-time setup code printed by the server and the owner's name.</summary>
public sealed record SetupRequest(string? Code = null, string? DisplayName = null);

/// <summary>Body of <c>POST /account/recover</c>: the recovery code from the PC's command line.</summary>
public sealed record RecoverRequest(string? Code = null);
