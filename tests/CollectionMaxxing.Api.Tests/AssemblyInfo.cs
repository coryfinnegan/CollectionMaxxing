using CollectionMaxxing.Api.Tests.Support;

// One Postgres container and one API host for the whole assembly, so tests run one after another.
[assembly: AssemblyFixture(typeof(ApiFixture))]
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
