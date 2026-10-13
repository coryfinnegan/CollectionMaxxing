namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// Serves the web app's <c>index.html</c> for the pages that start in the browser (<c>/setup</c>, <c>/account/login</c>,
/// <c>/join/{token}</c>); the SPA decides what to show. Until the web build is copied into <c>wwwroot</c> (APP-2b) it serves a
/// plain placeholder, so the server is usable and testable without it.
/// </summary>
public sealed class SpaPage(IWebHostEnvironment environment)
{
    private const string Placeholder =
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
        + "<title>CollectionMaxxing</title></head><body><p>The CollectionMaxxing web app hasn't been installed on this server yet.</p></body></html>";

    public IResult Index()
    {
        var file = environment.WebRootFileProvider.GetFileInfo("index.html");
        return file.Exists
            ? Results.Stream(file.CreateReadStream(), "text/html; charset=utf-8")
            : Results.Content(Placeholder, "text/html; charset=utf-8");
    }
}
