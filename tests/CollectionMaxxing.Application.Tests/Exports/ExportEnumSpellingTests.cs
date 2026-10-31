using System.Text.Json;
using System.Text.Json.Serialization;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;

namespace CollectionMaxxing.Application.Tests.Exports;

// An export spells a kind, a format, a status, a condition, a completeness and an identifier type the way the JSON API
// writes them (lower snake case, unless the member says otherwise), so a file read back by the API means the same thing.
public class ExportEnumSpellingTests
{
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static TheoryData<Type> ExportedEnums => new(
        typeof(WorkKind), typeof(ReleaseFormat), typeof(ItemStatus), typeof(ItemCondition), typeof(ItemCompleteness), typeof(IdentifierType));

    [Theory]
    [MemberData(nameof(ExportedEnums))]
    public void Every_member_is_spelled_as_the_json_api_writes_it(Type enumType)
    {
        var wrong = new List<string>();
        foreach (var member in Enum.GetValues(enumType))
        {
            var written = JsonSerializer.Serialize(member, enumType, Api).Trim('"');
            var spelled = (string)typeof(EnumText).GetMethod(nameof(EnumText.ToText))!.MakeGenericMethod(enumType).Invoke(null, [member])!;
            if (written != spelled)
            {
                wrong.Add($"{enumType.Name}.{member}: the API writes '{written}', the export spells '{spelled}'");
            }
        }

        wrong.ShouldBeEmpty();
    }
}
