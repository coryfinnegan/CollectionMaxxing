using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using Assembly = System.Reflection.Assembly;

namespace CollectionMaxxing.Architecture.Tests;

/// <summary>
/// The dependency rules of docs/01, "Dependency rules":
///
///   Api ──► Application ──► Domain
///    │            ▲
///    └──► Infrastructure ──┘   (Infrastructure implements Application's ports)
/// </summary>
public class DependencyRulesTests
{
    private static readonly Assembly DomainAssembly = typeof(DomainError).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(ICurrentUser).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(AppDbContext).Assembly;
    private static readonly Assembly ApiAssembly = typeof(Program).Assembly;

    private static readonly ArchUnitNET.Domain.Architecture Architecture = new ArchLoader()
        .LoadAssemblies(
            DomainAssembly,
            ApplicationAssembly,
            InfrastructureAssembly,
            ApiAssembly,
            // The frameworks the rules forbid must be loaded too, or "should not depend on" would match nothing.
            typeof(DbContext).Assembly,
            typeof(NpgsqlConnection).Assembly,
            typeof(HttpContext).Assembly,
            typeof(WebApplication).Assembly,
            typeof(IEndpointRouteBuilder).Assembly)
        .Build();

    private static readonly IObjectProvider<IType> Domain =
        Types().That().ResideInAssembly(DomainAssembly).As("Domain");

    private static readonly IObjectProvider<IType> Application =
        Types().That().ResideInAssembly(ApplicationAssembly).As("Application");

    private static readonly IObjectProvider<IType> Infrastructure =
        Types().That().ResideInAssembly(InfrastructureAssembly).As("Infrastructure");

    private static readonly IObjectProvider<IType> Api =
        Types().That().ResideInAssembly(ApiAssembly).As("Api");

    // Frameworks that must stay out of the inner layers.
    private static readonly IObjectProvider<IType> AspNetCore =
        Types().That().ResideInNamespaceMatching(@"^Microsoft\.AspNetCore(\..*)?$").As("ASP.NET Core");

    private static readonly IObjectProvider<IType> EntityFramework =
        Types().That().ResideInNamespaceMatching(@"^Microsoft\.EntityFrameworkCore(\..*)?$").As("EF Core");

    private static readonly IObjectProvider<IType> NpgsqlDriver =
        Types().That().ResideInNamespaceMatching(@"^Npgsql(\..*)?$").As("Npgsql");

    [Fact]
    public void Domain_depends_on_no_other_layer()
    {
        Types().That().Are(Domain).Should().NotDependOnAnyTypesThat().Are(Application)
            .AndShould().NotDependOnAnyTypesThat().Are(Infrastructure)
            .AndShould().NotDependOnAnyTypesThat().Are(Api)
            .Check(Architecture);
    }

    [Fact]
    public void Domain_uses_no_framework_only_the_base_class_library()
    {
        Types().That().Are(Domain).Should().NotDependOnAnyTypesThat().Are(EntityFramework)
            .AndShould().NotDependOnAnyTypesThat().Are(AspNetCore)
            .AndShould().NotDependOnAnyTypesThat().Are(NpgsqlDriver)
            .Check(Architecture);
    }

    [Fact]
    public void Domain_references_no_assemblies_except_the_base_class_library()
    {
        var references = DomainAssembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => !name.StartsWith("System", StringComparison.Ordinal) && name != "netstandard" && name != "mscorlib")
            .ToList();

        references.ShouldBeEmpty();
    }

    [Fact]
    public void Application_depends_only_on_Domain_inside_the_solution()
    {
        Types().That().Are(Application).Should().NotDependOnAnyTypesThat().Are(Infrastructure)
            .AndShould().NotDependOnAnyTypesThat().Are(Api)
            .Check(Architecture);
    }

    [Fact]
    public void Application_knows_neither_the_web_nor_the_database_driver()
    {
        // IAppDbContext exposes EF's DbSet<T> (docs/01), so EF Core itself is allowed; Npgsql and ASP.NET are not.
        Types().That().Are(Application).Should().NotDependOnAnyTypesThat().Are(AspNetCore)
            .AndShould().NotDependOnAnyTypesThat().Are(NpgsqlDriver)
            .Check(Architecture);
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_Api_or_the_web()
    {
        Types().That().Are(Infrastructure).Should().NotDependOnAnyTypesThat().Are(Api)
            .AndShould().NotDependOnAnyTypesThat().Are(AspNetCore)
            .Check(Architecture);
    }

    [Fact]
    public void Endpoint_classes_call_application_handlers_and_never_touch_the_database()
    {
        var endpointClasses = Types().That().Are(Api).And().HaveNameEndingWith("Endpoints").As("endpoint classes");

        endpointClasses.Should().NotDependOnAnyTypesThat().Are(EntityFramework)
            .AndShould().NotDependOnAnyTypesThat().Are(NpgsqlDriver)
            .AndShould().NotDependOnAnyTypesThat().Are(Infrastructure)
            .AndShould().NotDependOnAnyTypesThat().HaveFullName(typeof(IAppDbContext).FullName!)
            .Check(Architecture);
    }

    [Fact]
    public void The_framework_namespaces_the_rules_forbid_are_actually_present_in_the_loaded_architecture()
    {
        // If these matched nothing, the "should not depend on" rules above would pass vacuously.
        EntityFramework.GetObjects(Architecture).ShouldNotBeEmpty();
        AspNetCore.GetObjects(Architecture).ShouldNotBeEmpty();
        NpgsqlDriver.GetObjects(Architecture).ShouldNotBeEmpty();
    }

    [Fact]
    public void There_are_endpoint_classes_for_the_rule_above_to_check()
    {
        // Guards against the rule above silently matching nothing after a rename.
        Types().That().Are(Api).And().HaveNameEndingWith("Endpoints").GetObjects(Architecture).ShouldNotBeEmpty();
    }

    [Fact]
    public void Use_cases_are_named_Handler_and_live_in_Application()
    {
        var handlers = Types().That().HaveName("Handler").GetObjects(Architecture).ToList();

        handlers.ShouldNotBeEmpty();
        handlers.ShouldAllBe(handler => handler.Assembly.FullName!.StartsWith("CollectionMaxxing.Application", StringComparison.Ordinal));
    }
}
