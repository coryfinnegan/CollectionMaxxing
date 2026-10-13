using CollectionMaxxing.Api.Accounts;
using CollectionMaxxing.Api.Foundation;
using CollectionMaxxing.Api.Locations;
using CollectionMaxxing.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Shared plumbing, then one AddX() per feature.
builder.AddServiceDefaults();
builder.Services.AddApiFoundation();
builder.Services.AddInfrastructure();
builder.Services.AddAccounts();
builder.Services.AddLocations();

var app = builder.Build();

app.UseApiFoundation();

// Then one MapX() per feature.
app.MapDefaultEndpoints();
app.MapAccountEndpoints();
app.MapLocationEndpoints();

app.Run();

/// <summary>Lets the test project start the real app.</summary>
public partial class Program;
