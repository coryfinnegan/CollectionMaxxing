namespace CollectionMaxxing.Api.Tests.Support;

/// <summary>The collection of the classes that must run alone (see <see cref="RunsAlone"/>).</summary>
[CollectionDefinition(RunsAlone.Name, DisableParallelization = true)]
public sealed class RunsAloneDefinition;
