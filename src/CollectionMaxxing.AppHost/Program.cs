var builder = DistributedApplication.CreateBuilder(args);

// Postgres 17 in a container, with its data kept in a named volume between runs.
var postgres = builder.AddPostgres("postgres")
    .WithImageTag("17")
    .WithDataVolume();
var database = postgres.AddDatabase("collectionmaxxing");

// The API on its fixed development port. It receives ConnectionStrings__collectionmaxxing from the reference below.
builder.AddProject<Projects.CollectionMaxxing_Api>("api", launchProfileName: null)
    .WithHttpEndpoint(port: 5480, name: "http")
    .WithReference(database)
    .WaitFor(database);

builder.Build().Run();
