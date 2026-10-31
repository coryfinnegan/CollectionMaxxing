namespace CollectionMaxxing.Api.Tests.Support;

/// <summary>
/// The name of the collection for a test class that must not run alongside any other class (a time limit so short that machine load
/// alone can break it, say). Put <c>[Collection(RunsAlone.Name)]</c> on the class. xUnit finds collection definitions only in the test
/// assembly itself, so each assembly that uses it declares <c>[CollectionDefinition(RunsAlone.Name, DisableParallelization = true)]</c>
/// on an empty class (see Api.Tests.Library's <c>RunsAloneDefinition</c>). xUnit runs the classes of such a collection one after another,
/// after all the parallel ones have finished, so nothing else in the test process is running meanwhile.
/// </summary>
public static class RunsAlone
{
    public const string Name = "serial";
}
