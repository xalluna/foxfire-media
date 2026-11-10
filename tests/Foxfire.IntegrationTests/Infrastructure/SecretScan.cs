using System.Text;
using Npgsql;

namespace Foxfire.IntegrationTests.Infrastructure;

/// <summary>Helpers that look for secret values anywhere they must never appear.</summary>
public static class SecretScan
{
    /// <summary>Every row of every table in the database, rendered as text.</summary>
    public static async Task<string> DumpDatabaseAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var tables = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'", connection))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        var dump = new StringBuilder();
        foreach (var table in tables)
        {
            await using var cmd = new NpgsqlCommand($"SELECT t::text FROM \"{table}\" t", connection);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                dump.Append(table).Append(": ").AppendLine(reader.GetString(0));
            }
        }

        return dump.ToString();
    }

    public static async Task<string> RenderAsync(HttpResponseMessage response)
    {
        var text = new StringBuilder();
        text.AppendLine(((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            text.Append(header.Key).Append(": ").AppendLine(string.Join(", ", header.Value));
        }

        text.AppendLine(await response.Content.ReadAsStringAsync());
        return text.ToString();
    }

    public static void AssertContainsNone(string haystack, IEnumerable<string> secrets, string where)
    {
        foreach (var secret in secrets)
        {
            Assert.False(haystack.Contains(secret, StringComparison.Ordinal), $"A secret value leaked into {where}.");
        }
    }
}
