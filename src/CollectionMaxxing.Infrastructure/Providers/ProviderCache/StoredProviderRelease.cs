using System.Text.Json;
using System.Text.Json.Serialization;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;

namespace CollectionMaxxing.Infrastructure.Providers.Caching;

/// <summary>
/// A provider release as <c>provider_cache</c> keeps it (the permanent ISBN copy and the search hits an add reads back): plain
/// strings and numbers, so a change to <see cref="ProviderRelease"/> never breaks old rows.
/// </summary>
internal sealed record StoredProviderRelease(
    string Provider, string ExternalId, string Title, WorkKind Kind, ReleaseFormat? Format, string? Platform, int? Year,
    List<string> Creators, string? Edition, string? Publisher, string? CoverUrl, string? Synopsis, List<string> Identifiers,
    string? WorkKey = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static StoredProviderRelease From(ProviderRelease r) => new(
        r.Provider, r.ExternalId, r.Title, r.Kind, r.Format, r.Platform, r.Year, [.. r.Creators], r.Edition, r.Publisher,
        r.CoverUrl, r.Synopsis, [.. r.Identifiers.Select(i => i.Value)], r.WorkKey);

    public ProviderRelease ToRelease() => new()
    {
        Provider = Provider,
        ExternalId = ExternalId,
        WorkKey = WorkKey,
        Title = Title,
        Kind = Kind,
        Format = Format,
        Platform = Platform,
        Year = Year,
        Creators = Creators ?? [],
        Edition = Edition,
        Publisher = Publisher,
        CoverUrl = CoverUrl,
        Synopsis = Synopsis,
        Identifiers = ProviderJson.Identifiers(Identifiers ?? []),
    };
}
