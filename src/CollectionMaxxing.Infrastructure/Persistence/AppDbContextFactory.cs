using CollectionMaxxing.Application.Common;
using Microsoft.EntityFrameworkCore.Design;

namespace CollectionMaxxing.Infrastructure.Persistence;

/// <summary>Lets <c>dotnet ef</c> build the model without starting the app or touching a real database.</summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(AppDbContextOptions.Create("Host=localhost;Database=design_time_only"), new CurrentUser());
}
