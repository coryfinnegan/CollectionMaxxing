using CollectionMaxxing.Application.Libraries;
using CollectionMaxxing.Application.Libraries.Members;
using CollectionMaxxing.Infrastructure.Libraries;

namespace CollectionMaxxing.Api.Libraries;

/// <summary>The services behind libraries and members (kept out of the endpoint class, which never touches Infrastructure).</summary>
internal static class LibraryServices
{
    public static IServiceCollection Add(IServiceCollection services)
    {
        services.AddLibraryStores();
        services.AddOptions<LibraryOptions>().BindConfiguration(LibraryOptions.SectionName);
        services.AddScoped<ListLibraries.Handler>();
        services.AddScoped<GetLibrary.Handler>();
        services.AddScoped<CreateLibrary.Handler>();
        services.AddScoped<UpdateLibrary.Handler>();
        services.AddScoped<LeaveLibrary.Handler>();
        services.AddScoped<ListMembers.Handler>();
        services.AddScoped<UpdateMember.Handler>();
        services.AddScoped<RemoveMember.Handler>();
        return services;
    }
}
