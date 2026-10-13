namespace CollectionMaxxing.Api.Problems;

/// <summary>Endpoint metadata: this endpoint is part of the v1 contract but has no behaviour yet. Remove <c>.IsStub()</c> when you build it.</summary>
public sealed record StubEndpointMetadata;

public static class StubEndpoints
{
    /// <summary>The body of a stub: 501 Not Implemented as Problem Details.</summary>
    public static IResult NotImplemented() => ProblemResults.NotImplemented();

    /// <summary>Marks the endpoint as a stub and documents its 501 in the contract.</summary>
    public static TBuilder IsStub<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new StubEndpointMetadata());
        builder.ProducesProblem(StatusCodes.Status501NotImplemented);
        return builder;
    }
}
