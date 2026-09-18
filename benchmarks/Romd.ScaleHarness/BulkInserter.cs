using Npgsql;
using NpgsqlTypes;

namespace Romd.ScaleHarness;

/// <summary>
///     Binary COPY bulk loader. Column types are read from the catalog once per table so the
///     generator can keep passing plain CLR values (ints, longs, bools, strings, byte arrays,
///     Guids) and every value is written with the column's exact PostgreSQL type.
/// </summary>
public sealed class BulkInserter : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly string _table;
    private readonly string[] _columns;
    private NpgsqlDbType[]? _types;
    private NpgsqlBinaryImporter? _importer;

    public BulkInserter(NpgsqlConnection connection, string table, string[] columns)
    {
        _connection = connection;
        _table = table;
        _columns = columns;
    }

    public long RowsInserted { get; private set; }

    public async Task AddAsync(object?[] values, CancellationToken cancellationToken = default)
    {
        if (_importer is null)
        {
            _types = await ResolveColumnTypesAsync(cancellationToken);
            string columnList = string.Join(", ", _columns.Select(column => $"\"{column}\""));
            _importer = await _connection.BeginBinaryImportAsync(
                $"COPY {PostgreSqlSchema.Table(_table)} ({columnList}) FROM STDIN (FORMAT BINARY)",
                cancellationToken);
        }

        await _importer.StartRowAsync(cancellationToken);
        for (int i = 0; i < _columns.Length; i++)
        {
            object? value = values[i];
            if (value is null)
            {
                await _importer.WriteNullAsync(cancellationToken);
                continue;
            }

            NpgsqlDbType type = _types![i];
            await _importer.WriteAsync(Coerce(value, type), type, cancellationToken);
        }

        RowsInserted++;
    }

    public async ValueTask DisposeAsync()
    {
        if (_importer is not null)
        {
            await _importer.CompleteAsync();
            await _importer.DisposeAsync();
            _importer = null;
        }
    }

    private async Task<NpgsqlDbType[]> ResolveColumnTypesAsync(CancellationToken cancellationToken)
    {
        var dataTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(
            """
            SELECT column_name, data_type
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table
            """,
            _connection);
        command.Parameters.AddWithValue("schema", PostgreSqlSchema.Name);
        command.Parameters.AddWithValue("table", _table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            dataTypes[reader.GetString(0)] = reader.GetString(1);
        }

        // Only the loaded columns need a mapping; generated columns such as tsvector are never written.
        return _columns
            .Select(column => dataTypes.TryGetValue(column, out string? dataType)
                ? MapType(dataType)
                : throw new InvalidOperationException($"Column {_table}.{column} does not exist in the schema."))
            .ToArray();
    }

    private static NpgsqlDbType MapType(string dataType) => dataType switch
    {
        "integer" => NpgsqlDbType.Integer,
        "bigint" => NpgsqlDbType.Bigint,
        "smallint" => NpgsqlDbType.Smallint,
        "boolean" => NpgsqlDbType.Boolean,
        "uuid" => NpgsqlDbType.Uuid,
        "bytea" => NpgsqlDbType.Bytea,
        "text" or "character varying" or "character" => NpgsqlDbType.Text,
        "double precision" => NpgsqlDbType.Double,
        "real" => NpgsqlDbType.Real,
        "numeric" => NpgsqlDbType.Numeric,
        "timestamp with time zone" => NpgsqlDbType.TimestampTz,
        "timestamp without time zone" => NpgsqlDbType.Timestamp,
        "date" => NpgsqlDbType.Date,
        "jsonb" => NpgsqlDbType.Jsonb,
        "json" => NpgsqlDbType.Json,
        _ => throw new NotSupportedException($"Unsupported column type '{dataType}' for bulk loading.")
    };

    private static object Coerce(object value, NpgsqlDbType type) => type switch
    {
        NpgsqlDbType.Integer => Convert.ToInt32(value),
        NpgsqlDbType.Bigint => Convert.ToInt64(value),
        NpgsqlDbType.Smallint => Convert.ToInt16(value),
        NpgsqlDbType.Boolean => value is bool flag ? flag : Convert.ToInt64(value) != 0,
        NpgsqlDbType.Uuid => value is Guid guid ? guid : Guid.Parse(value.ToString()!),
        NpgsqlDbType.Double => Convert.ToDouble(value),
        NpgsqlDbType.Real => Convert.ToSingle(value),
        NpgsqlDbType.Numeric => Convert.ToDecimal(value),
        NpgsqlDbType.Text or NpgsqlDbType.Json or NpgsqlDbType.Jsonb => value.ToString()!,
        NpgsqlDbType.Date => value is DateOnly date
            ? date
            : DateOnly.Parse(value.ToString()!, System.Globalization.CultureInfo.InvariantCulture),
        _ => value
    };
}

/// <summary>Schema-qualified, quoted identifiers for raw SQL against the application schema.</summary>
public static class PostgreSqlSchema
{
    public const string Name = Romd.Persistence.PostgreSqlConfiguration.SchemaName;

    public static string Table(string table) => $"{Name}.\"{table}\"";
}
