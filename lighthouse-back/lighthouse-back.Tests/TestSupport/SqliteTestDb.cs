using Lighthouse.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Lighthouse.Tests.TestSupport;

/// <summary>
/// Real SQLite (in-memory, one open connection) with the production model: exercises what the
/// EF InMemory provider hides — case-sensitive <c>==</c>, <c>lower()</c> translation, unique and
/// filtered indexes. Seed data (roles admin=1/user=2, user admin=1) comes from the model.
/// </summary>
public sealed class SqliteTestDb : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public SqliteTestDb()
    {
        _connection.Open();
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    public AppDbContext NewContext(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptors)
            .Options);

    /// <summary>Raw SQL on the same database, outside any context (simulates another request).</summary>
    public void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}

/// <summary>Runs an action once, right before the next SaveChanges (a concurrent request winning a race).</summary>
public sealed class BeforeSaveOnce(Action action) : SaveChangesInterceptor
{
    private bool _done;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (!_done)
        {
            _done = true;
            action();
        }
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
