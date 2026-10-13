using CollectionMaxxing.TestSupport;

// One Postgres container for the whole assembly, so tests run one after another.
[assembly: AssemblyFixture(typeof(PostgresFixture))]
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
