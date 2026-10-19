using System.Text.Json;
using System.Text.Json.Serialization;
using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Problems;

namespace CollectionMaxxing.Api.Foundation;

/// <summary>The plumbing every feature shares: JSON, Problem Details, authentication, authorization and the OpenAPI document.</summary>
public static class ApiFoundation
{
    public const string OpenApiDocumentName = "v1";

    public static IServiceCollection AddApiFoundation(this IServiceCollection services)
    {
        // JSON: camelCase (the web default), enums as lowercase text.
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
            options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict; // numbers are numbers, never "12"
        });

        // Errors: RFC 9457 Problem Details everywhere, including bare 401/403/404 and unexpected exceptions.
        services.AddProblemDetails();
        // A malformed body is a 400 in every environment (by default Development throws instead, which becomes a 500).
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
        services.AddExceptionHandler<UniqueViolationExceptionHandler>();
        services.AddExceptionHandler<ForeignKeyViolationExceptionHandler>();
        services.AddExceptionHandler<ConcurrentUpdateExceptionHandler>();
        services.AddExceptionHandler<MalformedFormExceptionHandler>();
        services.AddExceptionHandler<CollectionMaxxing.Api.Auth.SessionStoreUnavailableExceptionHandler>();

        services.AddApiAuth();

        services.AddOpenApi(OpenApiDocumentName, options =>
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "CollectionMaxxing API";
                document.Info.Version = "1.0.0";
                document.Servers?.Clear(); // the document must not depend on the host that served it
                return Task.CompletedTask;
            }));

        return services;
    }

    /// <summary>The middleware pipeline, in order. Feature endpoints are mapped by the caller afterwards.</summary>
    public static WebApplication UseApiFoundation(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages(); // 401/403/404 with no body become Problem Details
        app.UseApiAuth();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi().AllowAnonymous();
        }

        return app;
    }
}
