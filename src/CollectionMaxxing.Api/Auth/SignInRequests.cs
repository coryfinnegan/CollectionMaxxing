namespace CollectionMaxxing.Api.Auth;

// Bodies of the sign-in endpoints (docs/14, issue #228).

/// <summary>Body of <c>POST /setup</c>: the one-time setup code printed by the server.</summary>
public sealed record SetupRequest(string? Code = null);

/// <summary>Body of <c>POST /account/recover</c>: the recovery code from the PC's command line.</summary>
public sealed record RecoverRequest(string? Code = null);
