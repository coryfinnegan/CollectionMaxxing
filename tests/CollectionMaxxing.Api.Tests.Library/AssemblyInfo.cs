using CollectionMaxxing.TestSupport;

// One Postgres container for the whole assembly, migrated once into a template database. Every test class clones its own database
// from it and gets its own API host (ApiFixture is a class fixture of ApiTest), so classes run in parallel while the tests inside one
// class run one after another. A class that must not run alongside others goes in a collection defined with DisableParallelization.
[assembly: AssemblyFixture(typeof(PostgresServerLifetime))]
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerClass)]
