using CollectionMaxxing.Api.Accounts;
using CollectionMaxxing.Api.Admin;
using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Catalog;
using CollectionMaxxing.Api.Evals;
using CollectionMaxxing.Api.Foundation;
using CollectionMaxxing.Api.Invites;
using CollectionMaxxing.Api.Items;
using CollectionMaxxing.Api.Libraries;
using CollectionMaxxing.Api.Locations;
using CollectionMaxxing.Api.Ownership;
using CollectionMaxxing.Api.Recognition;
using CollectionMaxxing.Api.Scanning;
using CollectionMaxxing.Api.Security;
using CollectionMaxxing.Api.Tags;
using CollectionMaxxing.Api.Trash;
using CollectionMaxxing.Infrastructure;
using CollectionMaxxing.Infrastructure.Audit;
using CollectionMaxxing.Infrastructure.Jobs;
using CollectionMaxxing.Infrastructure.Trash;

var builder = WebApplication.CreateBuilder(args);

// Shared plumbing, then one AddX() per feature.
builder.AddServiceDefaults();
builder.Services.AddApiFoundation();
builder.Services.AddSecurityHeaders();
builder.Services.AddInfrastructure();
builder.Services.AddJobs();
builder.Services.AddAccounts();
builder.Services.AddSignIn();
builder.Services.AddInvites();
builder.Services.AddLibraries();
builder.Services.AddLocations();
builder.Services.AddItems();
builder.Services.AddAudit();
builder.Services.AddTrash();
builder.Services.AddTrashPurge();
builder.Services.AddTags();
builder.Services.AddOwnership();
builder.Services.AddScanning();
builder.Services.AddRecognition();
builder.Services.AddCatalog();
builder.Services.AddAdmin();
builder.Services.AddEvals();

var app = builder.Build();

app.UseSecurityHeaders();
app.UseApiFoundation();
app.UseApiLimits();

// Then one MapX() per feature.
app.MapDefaultEndpoints();
app.MapAccountEndpoints();
app.MapSignInEndpoints();
app.MapInviteEndpoints();
app.MapLibraryEndpoints();
app.MapLocationEndpoints();
app.MapItemEndpoints();
app.MapTrashEndpoints();
app.MapTagEndpoints();
app.MapOwnershipEndpoints();
app.MapScanningEndpoints();
app.MapRecognition();
app.MapCatalogEndpoints();
app.MapAdminEndpoints();
app.MapEvals();

// `dotnet run --project src/CollectionMaxxing.Api -- --recovery-code` prints a one-time recovery code and exits instead of serving.
if (await app.TryRunAdminCommandAsync(args))
{
    return;
}

app.Run();

/// <summary>Lets the test project start the real app.</summary>
public partial class Program;
