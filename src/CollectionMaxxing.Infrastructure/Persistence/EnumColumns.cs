using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CollectionMaxxing.Infrastructure.Persistence;

/// <summary>Stores an enum as the same lower snake_case text the JSON API uses (see <see cref="EnumText"/>).</summary>
public sealed class SnakeCaseEnumConverter<TEnum>()
    : ValueConverter<TEnum, string>(value => EnumText.ToText(value), text => EnumText.Parse<TEnum>(text))
    where TEnum : struct, Enum;

public static class EnumColumns
{
    private const int ColumnLength = 32;

    /// <summary>Maps an enum property to a text column of snake_case values. Pair it with <see cref="CheckIn{TEnum}"/>.</summary>
    public static PropertyBuilder<TEnum> HasSnakeCaseEnum<TEnum>(this PropertyBuilder<TEnum> property)
        where TEnum : struct, Enum =>
        property.HasConversion<SnakeCaseEnumConverter<TEnum>>().HasMaxLength(ColumnLength);

    public static PropertyBuilder<TEnum?> HasSnakeCaseEnum<TEnum>(this PropertyBuilder<TEnum?> property)
        where TEnum : struct, Enum =>
        property.HasConversion<SnakeCaseEnumConverter<TEnum>>().HasMaxLength(ColumnLength);

    /// <summary>The SQL for a check constraint that only lets the enum's values into <paramref name="column"/>.</summary>
    public static string CheckIn<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"\"{column}\" in ({string.Join(", ", EnumText.All<TEnum>().Select(value => $"'{value}'"))})";
}
