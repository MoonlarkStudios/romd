using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Romd.Admin.Application.Enrichment;
using Romd.Contracts.Management.Enrichment;
using Romd.Domain.Catalog;
using Romd.Persistence;
using Romd.PostgreSql.TestSupport;
using Shouldly;

using EnrichmentRequirement = Romd.Domain.Catalog.EnrichmentRequirement;

namespace Romd.Infrastructure.Tests.Enrichment.Helpers;

/// <summary>
///     Reads the schema-42 (seven argument) and schema-43 (eight argument) facts functions directly and proves that
///     schema 43 only adds cause text, satisfaction text and disabled-provider identity rows.
/// </summary>
internal static class EnrichmentFactsParity
{
    public const string SchemaFortyTwo = "20260922180618_OptionalMissingLogo";
    private static readonly Func<EnrichmentRequirementFacts, EnrichmentRequirement> Classify = EnrichmentAssessmentPolicy.Classify.Compile();

    public sealed record ProviderSet(string[] Enabled, string[] Configured, string[] Disabled)
    {
        public override string ToString() =>
            $"enabled [{string.Join(",", Enabled)}] configured [{string.Join(",", Configured)}] disabled [{string.Join(",", Disabled)}]";
    }

    public sealed record FactRow(int TitleId, int Kind, string Subject, bool? Satisfied, bool? RequiresDecision, bool? Blocked,
        bool? Processing, string? Cause, string? SatisfiedBy)
    {
        public (int TitleId, int Kind, string Subject) Key => (TitleId, Kind, Subject);
        public (bool? Satisfied, bool? RequiresDecision, bool? Blocked, bool? Processing) Booleans =>
            (Satisfied, RequiresDecision, Blocked, Processing);
        public EnrichmentRequirement Requirement => Classify(new EnrichmentRequirementFacts
        {
            TitleId = TitleId,
            Kind = (EnrichmentRequirementKind)Kind,
            Subject = Subject,
            // SQL CASE treats NULL as false, exactly like the translated Classify expression.
            Satisfied = Satisfied ?? false,
            RequiresDecision = RequiresDecision ?? false,
            Blocked = Blocked ?? false,
            Processing = Processing ?? false,
            Cause = Cause,
            SatisfiedBy = SatisfiedBy
        });
    }

    public static readonly ProviderSet[] ProviderSets =
    [
        new(["igdb", "steamgriddb"], ["igdb", "steamgriddb"], []),
        new(["igdb"], ["igdb"], ["steamgriddb"]),
        new(["steamgriddb"], ["steamgriddb"], ["igdb"]),
        new(["igdb", "steamgriddb"], ["steamgriddb"], []),
        new(["igdb"], [], ["steamgriddb"]),
        new([], [], ["igdb", "steamgriddb"])
    ];

    public static async Task<IReadOnlyList<FactRow>> ReadAsync(DbConnection connection, int[] titleIds, bool media,
        ProviderSet providers, bool current)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = current
            ? """
              SELECT "TitleId","Kind","Subject","Satisfied","RequiresDecision","Blocked","Processing","Cause","SatisfiedBy"
              FROM romd.enrichment_requirement_facts_set(@ids,@version,@media,@media,@media,@enabled,@configured,@disabled)
              """
            : """
              SELECT "TitleId","Kind","Subject","Satisfied","RequiresDecision","Blocked","Processing",NULL::text,NULL::text
              FROM romd.enrichment_requirement_facts_set(@ids,@version,@media,@media,@media,@enabled,@configured)
              """;
        Add(command, "ids", titleIds);
        Add(command, "version", ProviderIdentityPolicy.Version);
        Add(command, "media", media);
        Add(command, "enabled", providers.Enabled);
        Add(command, "configured", providers.Configured);
        if (current)
            Add(command, "disabled", providers.Disabled);
        var rows = new List<FactRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), Flag(reader, 3), Flag(reader, 4), Flag(reader, 5),
                Flag(reader, 6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
        return rows;
    }

    /// <summary>Asserts schema-42 parity and returns the rows only schema 43 emits.</summary>
    public static IReadOnlyList<FactRow> AssertParity(IReadOnlyList<FactRow> legacy, IReadOnlyList<FactRow> current, ProviderSet providers)
    {
        var label = providers.ToString();
        legacy.Select(row => row.Key).ShouldBeUnique(label);
        current.Select(row => row.Key).ShouldBeUnique(label);
        var currentByKey = current.ToDictionary(row => row.Key);
        foreach (var row in legacy)
        {
            currentByKey.TryGetValue(row.Key, out var match).ShouldBeTrue($"{label} lost {row.Key}");
            match!.Booleans.ShouldBe(row.Booleans, $"{label} {row.Key}");
        }
        string.Join(Environment.NewLine, Flags(current)).ShouldBe(string.Join(Environment.NewLine, Flags(legacy)), label);
        var legacyKeys = legacy.Select(row => row.Key).ToHashSet();
        var added = current.Where(row => !legacyKeys.Contains(row.Key)).ToArray();
        foreach (var row in added)
        {
            row.Kind.ShouldBe((int)EnrichmentRequirementKind.Identity, $"{label} {row.Key}");
            providers.Disabled.ShouldContain(row.Subject, $"{label} {row.Key}");
            (row.Satisfied, row.RequiresDecision, row.Blocked).ShouldBe(((bool?)false, (bool?)false, (bool?)true), $"{label} {row.Key}");
            ((row.Processing, row.Cause) is (true, "Working") or (false, "ProviderDisabled")).ShouldBeTrue($"{label} {row.Key}");
        }
        return added;
    }

    /// <summary>Maps every schema-43 row through the production mapper and checks it with the independent oracle.</summary>
    public static void AssertWellFormed(IReadOnlyList<FactRow> current)
    {
        foreach (var title in current.GroupBy(row => row.TitleId))
        {
            var requirements = title.Select(row => row.Requirement).ToArray();
            var assessment = new EnrichmentAssessment(title.Key.ToString(), "", "", Guid.Empty,
                requirements.Any(x => x.State == EnrichmentRequirementState.NeedsReview),
                requirements.Any(x => x.State == EnrichmentRequirementState.Automatic),
                requirements.Any(x => x.State == EnrichmentRequirementState.Processing),
                requirements.Any(x => x.State == EnrichmentRequirementState.Blocked),
                EnrichmentRequirementMapping.ToContract(requirements), []);
            EnrichmentRequirementInvariants.AssertWellFormed(assessment);
        }
    }

    public static async Task MigrateAsync(PostgreSqlTestDatabase database, string? target)
    {
        await using (var db = database.CreateContext())
            await db.GetService<IMigrator>().MigrateAsync(target);
        // Drop pooled sessions so no cached plan observes the replaced function signature.
        NpgsqlConnection.ClearPool(new NpgsqlConnection(database.ConnectionString));
    }

    public static async Task<IReadOnlyList<string>> FunctionSignaturesAsync(RomdDbContext db) =>
        await db.Database.SqlQueryRaw<string>("""
            SELECT concat_ws(' | ', p.pronargs, p.provolatile, p.proparallel, p.prorows, array_to_string(p.proconfig, ';'),
              pg_get_function_result(p.oid)) AS "Value"
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'romd' AND p.proname = 'enrichment_requirement_facts_set'
            ORDER BY p.pronargs
            """).ToListAsync();

    private static (int TitleId, bool NeedsReview, bool Automatic, bool Processing, bool Blocked)[] Flags(IEnumerable<FactRow> rows) =>
        rows.GroupBy(row => row.TitleId).OrderBy(group => group.Key).Select(group =>
        {
            var states = group.Select(row => row.Requirement.State).ToHashSet();
            return (group.Key, states.Contains(EnrichmentRequirementState.NeedsReview), states.Contains(EnrichmentRequirementState.Automatic),
                states.Contains(EnrichmentRequirementState.Processing), states.Contains(EnrichmentRequirementState.Blocked));
        }).ToArray();

    private static bool? Flag(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
