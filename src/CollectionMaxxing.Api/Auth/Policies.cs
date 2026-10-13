namespace CollectionMaxxing.Api.Auth;

/// <summary>Names of the authorization policies that aren't about a library.</summary>
public static class Policies
{
    /// <summary>The platform administrator (the person who runs the server). Used by <c>/api/admin/*</c>.</summary>
    public const string Admin = "admin";
}

/// <summary>Platform roles (not library roles). A library role says what you may do in one library.</summary>
public static class Roles
{
    public const string Admin = "admin";
}
