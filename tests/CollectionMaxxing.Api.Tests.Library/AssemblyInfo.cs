using CollectionMaxxing.TestSupport;

// One Postgres container for the whole assembly, migrated once into a template database. Every test class clones its own database
// from it and gets its own API host (ApiFixture is a class fixture of ApiTest), so classes run in parallel while the tests inside one
// class run one after another. A class that must not run alongside others goes in a collection defined with DisableParallelization
// (see RunsAlone in the test kit).
//
// At most four classes at a time, which is also what xUnit's default (one per processor) gives a four-core CI runner. On a 32-thread PC more
// buys little (Api.Tests took 221 s with four classes at a time, 188 s with eight, 181 s with 32), and it starves the other test projects that
// scripts/check.* runs at the same moment: with eight or more, every local gate run failed a timing test in Infrastructure.Tests (a 5 s attempt
// timeout); with four it passed.
[assembly: AssemblyFixture(typeof(PostgresServerLifetime))]
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerClass)]
[assembly: Xunit.v3.Parallelization(MaxThreads = 4)]
