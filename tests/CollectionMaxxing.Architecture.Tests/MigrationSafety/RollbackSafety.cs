using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace CollectionMaxxing.Architecture.Tests.MigrationSafety;

/// <summary>What a migration does that the previous release's code cannot live with.</summary>
public enum RollbackRisk
{
    /// <summary>Adds a NOT NULL column with no database default: the previous release's INSERTs do not mention it.</summary>
    RequiredColumnWithoutDefault,

    /// <summary>Drops or renames a column or table: the previous release's queries still name it.</summary>
    DropsOrRenames,

    /// <summary>Removes the default of a NOT NULL column: the previous release's INSERTs rely on it.</summary>
    DropsDefaultOfRequiredColumn,

    /// <summary>Turns a nullable column into a NOT NULL one: the previous release may still write NULL.</summary>
    MakesColumnRequired,
}

/// <summary>One thing in one migration that breaks the rollback premise.</summary>
internal sealed record RollbackFinding(RollbackRisk Risk, string Operation);

/// <summary>One migration the check is told to accept, for the named risks only, and why.</summary>
internal sealed record RollbackWaiver(string MigrationId, RollbackRisk[] Risks, string Reason);

/// <summary>
/// Reads the up operations of a migration and reports what the previous release's code could not survive. The operation model
/// is read as it is; only raw SQL is read as text, with comments and string literals blanked so words inside them do not count.
/// </summary>
internal static partial class RollbackSafety
{
    /// <summary>The annotation a scaffolded migration puts on an identity or serial column; the provider's own constant is internal.</summary>
    private const string ValueGenerationStrategyAnnotation = "Npgsql:ValueGenerationStrategy";

    public static IReadOnlyList<RollbackFinding> Check(IReadOnlyList<MigrationOperation> upOperations)
    {
        var createdHere = upOperations.OfType<CreateTableOperation>().Select(table => (table.Schema, table.Name)).ToHashSet();
        var addedNullableHere = new HashSet<(string? Schema, string Table, string Name)>();
        var findings = new List<RollbackFinding>();

        foreach (var operation in upOperations)
        {
            switch (operation)
            {
                case AddColumnOperation add when !add.IsNullable && !HasDatabaseDefault(add) && !createdHere.Contains((add.Schema, add.Table)):
                    findings.Add(new(RollbackRisk.RequiredColumnWithoutDefault, $"AddColumn {add.Table}.{add.Name}: NOT NULL with no database default, so the previous release's INSERT fails"));
                    break;

                case AddColumnOperation add when add.IsNullable && !createdHere.Contains((add.Schema, add.Table)):
                    addedNullableHere.Add((add.Schema, add.Table, add.Name));
                    break;

                case DropColumnOperation drop:
                    findings.Add(new(RollbackRisk.DropsOrRenames, $"DropColumn {drop.Table}.{drop.Name}: the previous release still reads and writes it"));
                    break;

                case RenameColumnOperation rename:
                    findings.Add(new(RollbackRisk.DropsOrRenames, $"RenameColumn {rename.Table}.{rename.Name} to {rename.NewName}: the previous release still uses the old name"));
                    break;

                case DropTableOperation drop:
                    findings.Add(new(RollbackRisk.DropsOrRenames, $"DropTable {drop.Name}: the previous release still uses it"));
                    break;

                case RenameTableOperation rename:
                    findings.Add(new(RollbackRisk.DropsOrRenames, $"RenameTable {rename.Name} to {rename.NewName}: the previous release still uses the old name"));
                    break;

                case AlterColumnOperation alter:
                    findings.AddRange(CheckAlter(alter, addedNullableHere.Contains((alter.Schema, alter.Table, alter.Name))));
                    break;

                case SqlOperation sql:
                    findings.AddRange(CheckSql(sql.Sql));
                    break;
            }
        }

        return findings;
    }

    /// <summary>The findings no waiver for this migration accepts.</summary>
    public static IReadOnlyList<RollbackFinding> Unwaived(string migrationId, IReadOnlyList<RollbackFinding> findings, IReadOnlyList<RollbackWaiver> waivers)
    {
        var accepted = waivers.Where(waiver => waiver.MigrationId == migrationId).SelectMany(waiver => waiver.Risks).ToHashSet();
        return [.. findings.Where(finding => !accepted.Contains(finding.Risk))];
    }

    /// <param name="alter">The column change.</param>
    /// <param name="addedNullableByThisMigration">
    /// The migration adds the column nullable, backfills it and only then makes it required. EF cannot know the column was nullable
    /// (it was never in the database), so the operation does not say; the end state is a NOT NULL column added with no default.
    /// </param>
    private static IEnumerable<RollbackFinding> CheckAlter(AlterColumnOperation alter, bool addedNullableByThisMigration)
    {
        var where = $"{alter.Table}.{alter.Name}";

        if (addedNullableByThisMigration)
        {
            if (!alter.IsNullable && !HasDatabaseDefault(alter))
            {
                yield return new(RollbackRisk.RequiredColumnWithoutDefault, $"AlterColumn {where}: added nullable, then made NOT NULL with no database default, so the previous release's INSERT fails");
            }

            yield break;
        }

        if (!alter.IsNullable && alter.OldColumn.IsNullable)
        {
            yield return new(RollbackRisk.MakesColumnRequired, $"AlterColumn {where}: nullable to NOT NULL, so the previous release's NULL writes fail (a default does not help an explicit NULL)");
        }

        if (!alter.IsNullable && HasDatabaseDefault(alter.OldColumn) && !HasDatabaseDefault(alter))
        {
            yield return new(RollbackRisk.DropsDefaultOfRequiredColumn, $"AlterColumn {where}: drops the default of a NOT NULL column, so the previous release's INSERT that leaves it out fails");
        }
    }

    private static bool HasDatabaseDefault(ColumnOperation column) =>
        column.DefaultValue is not null
        || column.DefaultValueSql is not null
        || column.ComputedColumnSql is not null
        || column[ValueGenerationStrategyAnnotation] is NpgsqlValueGenerationStrategy.IdentityAlwaysColumn
            or NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
            or NpgsqlValueGenerationStrategy.SerialColumn;

    // ---- Raw SQL ----

    private static IEnumerable<RollbackFinding> CheckSql(string sql)
    {
        foreach (var statement in Blanked(sql).Split(';'))
        {
            var text = WhiteSpace().Replace(statement, " ").Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (DropColumnOrTable().IsMatch(text))
            {
                yield return new(RollbackRisk.DropsOrRenames, $"Sql \"{Shorten(text)}\": drops a column or table the previous release still uses");
            }

            if (Rename().IsMatch(text))
            {
                yield return new(RollbackRisk.DropsOrRenames, $"Sql \"{Shorten(text)}\": renames something the previous release still names");
            }

            if (DropDefault().IsMatch(text))
            {
                yield return new(RollbackRisk.DropsDefaultOfRequiredColumn, $"Sql \"{Shorten(text)}\": drops a column default the previous release's INSERT may rely on");
            }

            if (SetNotNull().IsMatch(text))
            {
                yield return new(RollbackRisk.MakesColumnRequired, $"Sql \"{Shorten(text)}\": makes a column NOT NULL, so the previous release's NULL writes fail");
            }

            foreach (var clause in AddedRequiredColumnsWithoutDefault(text))
            {
                yield return new(RollbackRisk.RequiredColumnWithoutDefault, $"Sql \"{Shorten(clause)}\": NOT NULL with no DEFAULT, so the previous release's INSERT fails");
            }
        }
    }

    /// <summary>The ADD COLUMN clauses of a statement that are NOT NULL with nothing to fill them.</summary>
    private static IEnumerable<string> AddedRequiredColumnsWithoutDefault(string statement)
    {
        foreach (Match add in AddColumn().Matches(statement))
        {
            var clause = statement[add.Index..ClauseEnd(statement, add.Index + add.Length)];
            if (NotNull().IsMatch(clause) && !FillsItself().IsMatch(clause))
            {
                yield return clause.Trim();
            }
        }
    }

    /// <summary>Where a clause of an ALTER TABLE ends: at a comma outside parentheses (numeric(10, 2), DEFAULT (...)), or at the end.</summary>
    private static int ClauseEnd(string statement, int from)
    {
        var depth = 0;
        for (var i = from; i < statement.Length; i++)
        {
            switch (statement[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    return i;
            }
        }

        return statement.Length;
    }

    /// <summary>The SQL with comments and string literals replaced by a space, so a word inside them is not read as a statement.</summary>
    private static string Blanked(string sql) => CommentOrLiteral().Replace(sql, " ");

    private static string Shorten(string text) => text.Length <= 120 ? text : text[..117] + "...";

    [GeneratedRegex(@"--[^\r\n]*|/\*.*?\*/|'(?:[^']|'')*'", RegexOptions.Singleline)]
    private static partial Regex CommentOrLiteral();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"\bDROP COLUMN\b|\bDROP TABLE\b", RegexOptions.IgnoreCase)]
    private static partial Regex DropColumnOrTable();

    [GeneratedRegex(@"\bRENAME\b", RegexOptions.IgnoreCase)]
    private static partial Regex Rename();

    [GeneratedRegex(@"\bDROP DEFAULT\b", RegexOptions.IgnoreCase)]
    private static partial Regex DropDefault();

    [GeneratedRegex(@"\bSET NOT NULL\b", RegexOptions.IgnoreCase)]
    private static partial Regex SetNotNull();

    // ADD [COLUMN] [IF NOT EXISTS] name, but not ADD CONSTRAINT / PRIMARY KEY / UNIQUE / CHECK / FOREIGN KEY / EXCLUDE.
    [GeneratedRegex(@"\bADD (?:COLUMN )?(?:IF NOT EXISTS )?(?!(?:CONSTRAINT|PRIMARY|UNIQUE|CHECK|FOREIGN|EXCLUDE|COLUMN)\b)(?:""[^""]+""|\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex AddColumn();

    [GeneratedRegex(@"\bNOT NULL\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotNull();

    // A column the database fills in without being told: a DEFAULT, a generated or identity column, a serial type.
    [GeneratedRegex(@"\bDEFAULT\b|\bGENERATED\b|\b(?:SMALL|BIG)?SERIAL\b", RegexOptions.IgnoreCase)]
    private static partial Regex FillsItself();
}
