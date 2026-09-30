using MCPal.Cloud.Tests.Infrastructure;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MCPal.Cloud.Tests;

/// <summary>
/// One PostgreSQL container per test run (assembly-wide). Each test gets its own database cloned from a migrated template.
/// Tests are marked inconclusive when Docker is unavailable.
/// </summary>
[SetUpFixture]
internal sealed class PostgresFixture
{
    private const string TemplateName = "mcpal_template";
    private static PostgreSqlContainer? container;
    private static string? unavailableReason;

    [OneTimeSetUp]
    public static async Task StartAsync()
    {
        try
        {
            container = new PostgreSqlBuilder("postgres:17").Build();
            await container.StartAsync();
            await using var connection = new NpgsqlConnection(container.GetConnectionString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE {TemplateName}";
            await command.ExecuteNonQueryAsync();
            await TemplateMigrator.MigrateAsync(new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = TemplateName }.ConnectionString);
            NpgsqlConnection.ClearAllPools();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            unavailableReason = ex.ToString();
        }
    }

    [OneTimeTearDown]
    public static async Task StopAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }

    /// <summary>Creates an isolated database and returns its connection string.</summary>
    public static async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        if (container is null || unavailableReason is not null)
        {
            Assert.Inconclusive($"Docker/PostgreSQL unavailable: {unavailableReason}");
        }

        var name = "t_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(container.GetConnectionString()))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE {name} TEMPLATE {TemplateName}";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = name, Pooling = false }.ConnectionString;
    }
}
