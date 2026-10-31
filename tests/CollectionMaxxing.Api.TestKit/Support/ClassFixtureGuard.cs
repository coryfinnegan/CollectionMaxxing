using System.Reflection;
using Xunit;

namespace CollectionMaxxing.Api.Tests.Support;

/// <summary>
/// Finds test classes that take an <see cref="ApiFixture"/> without declaring <c>IClassFixture&lt;ApiFixture&gt;</c> (deriving from
/// <see cref="ApiTest"/> declares it). Such a class would not get a database of its own: in an assembly that registers a shared
/// <see cref="ApiFixture"/> it would quietly share that one with every other such class, and race with them now that classes run in parallel.
/// </summary>
public static class ClassFixtureGuard
{
    public static IReadOnlyList<string> Offenders(Assembly tests, params string[] allowed) =>
    [
        .. tests.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false, IsNested: false })
            .Where(type => !typeof(IClassFixture<ApiFixture>).IsAssignableFrom(type))
            .Where(type => type.GetConstructors().Any(ctor => ctor.GetParameters().Any(p => p.ParameterType == typeof(ApiFixture))))
            .Select(type => type.Name)
            .Except(allowed)
            .Order(StringComparer.Ordinal),
    ];
}
