using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.TestSupport;

// One Postgres container for the whole assembly, migrated once into a template database. Every test class clones its own database
// from it and gets its own API host (ApiFixture is a class fixture of ApiTest), so classes run in parallel while the tests inside one
// class run one after another. A class that must not run alongside others goes in a collection defined with DisableParallelization
// (see RunsAlone in the test kit).
//
// At most eight classes at a time: on a 32-thread PC the suites were no faster with more (Api.Tests took 188 s with eight and 181 s with 32),
// while more only starved the other test projects that the local gate runs at the same moment into timing failures. xUnit's own default is
// one per processor.
[assembly: AssemblyFixture(typeof(PostgresServerLifetime))]
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerClass)]
[assembly: Xunit.v3.Parallelization(MaxThreads = 8)]

// The one ApiFixture that is shared by the whole assembly, only for the locked MatrixCompletenessTests: it names ApiFixture but declares no
// class fixture, and it only reads the app's endpoint metadata. Every other class here gets its own (ApiClassFixtureTests checks that).
[assembly: AssemblyFixture(typeof(ApiFixture))]
