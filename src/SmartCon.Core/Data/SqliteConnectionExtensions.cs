using System.Data;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Common;

namespace SmartCon.Core.Data;

public static class SqliteConnectionExtensions
{
    public static async Task<int> ExecuteAsync(
        this SqliteConnection connection,
        string sql,
        IEnumerable<SqliteParameter>? parameters = null,
        CancellationToken ct = default)
    {
        Guard.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("SQL cannot be empty.", nameof(sql));

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        if (parameters is not null)
        {
            foreach (var p in parameters)
            {
                cmd.Parameters.Add(p);
            }
        }
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T?> ExecuteScalarAsync<T>(
        this SqliteConnection connection,
        string sql,
        IEnumerable<SqliteParameter>? parameters = null,
        CancellationToken ct = default)
    {
        Guard.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("SQL cannot be empty.", nameof(sql));

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        if (parameters is not null)
        {
            foreach (var p in parameters)
            {
                cmd.Parameters.Add(p);
            }
        }
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is null or DBNull) return default;
        return (T)Convert.ChangeType(result, typeof(T));
    }

    public static async Task<IReadOnlyList<T>> QueryAsync<T>(
        this SqliteConnection connection,
        string sql,
        Func<SqliteDataReader, T> map,
        IEnumerable<SqliteParameter>? parameters = null,
        CancellationToken ct = default)
    {
        Guard.ThrowIfNull(connection);
        Guard.ThrowIfNull(map);
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("SQL cannot be empty.", nameof(sql));

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        if (parameters is not null)
        {
            foreach (var p in parameters)
            {
                cmd.Parameters.Add(p);
            }
        }
        var list = new List<T>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(map(reader));
        }
        return list;
    }

    public static async Task<T?> QuerySingleOrDefaultAsync<T>(
        this SqliteConnection connection,
        string sql,
        Func<SqliteDataReader, T> map,
        IEnumerable<SqliteParameter>? parameters = null,
        CancellationToken ct = default)
    {
        Guard.ThrowIfNull(connection);
        Guard.ThrowIfNull(map);
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("SQL cannot be empty.", nameof(sql));

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        if (parameters is not null)
        {
            foreach (var p in parameters)
            {
                cmd.Parameters.Add(p);
            }
        }
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return default;
        return map(reader);
    }
}
