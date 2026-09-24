using CodexQuota.Core.Quota;
using CodexQuota.Storage.Database;
using CodexQuota.Storage.History;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexQuota.Storage.Tests;

/// <summary>
/// The Bridge database owns exactly one SQLite connection, which is what makes
/// <c>Data Source=:memory:</c> work — and what makes concurrent use of it unsafe. Before the host
/// can serve history reads while the persistence worker writes, every operation on that connection
/// has to be serialized.
/// </summary>
/// <remarks>
/// These tests use a temporary file database on purpose: <c>:memory:</c> gives every connection its
/// own private database, so it cannot express "two callers, one connection".
/// </remarks>
public class BridgeDatabaseConcurrencyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task OperationsOnTheSharedConnectionNeverOverlap()
    {
        var path = NewDatabasePath();
        try
        {
            await using var database = new BridgeDatabase(new SqliteConnection($"Data Source={path}"));

            var inFlight = 0;
            var maxInFlight = 0;

            var operations = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
                database.ExecuteAsync(
                    async (connection, token) =>
                    {
                        var now = Interlocked.Increment(ref inFlight);
                        InterlockedMax(ref maxInFlight, now);

                        try
                        {
                            await using var command = connection.CreateCommand();
                            command.CommandText = "SELECT 1;";
                            await command.ExecuteScalarAsync(token);

                            // Hold the connection across an await. A gate that only covered the
                            // synchronous part would still let two commands overlap right here.
                            await Task.Delay(2, token);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref inFlight);
                        }
                    },
                    CancellationToken.None)));

            await Task.WhenAll(operations);

            Assert.Equal(1, maxInFlight);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task ConcurrentHistoryReadsAndWritesStayConsistent()
    {
        var path = NewDatabasePath();
        try
        {
            await using var database = new BridgeDatabase(new SqliteConnection($"Data Source={path}"));
            var repository = new SqliteHistoryRepository(database);

            var writers = Enumerable.Range(1, 64).Select(index => Task.Run(() =>
                repository.AppendSnapshotAsync(
                    HistoryFixtures.Update(null, HistoryFixtures.Snapshot(100 - index, 50)),
                    CancellationToken.None)));

            var readers = Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
                repository.ReadHistoryAsync(
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.MaxValue,
                    CancellationToken.None)));

            await Task.WhenAll(writers.Concat<Task>(readers)).WaitAsync(Timeout);

            var all = await repository.ReadHistoryAsync(
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.MaxValue,
                CancellationToken.None);

            Assert.Equal(64, all.Count);
        }
        finally
        {
            Delete(path);
        }
    }

    private static string NewDatabasePath()
        => Path.Combine(Path.GetTempPath(), $"codexquota-concurrency-{Guid.NewGuid():N}.db");

    /// <summary>
    /// Best-effort cleanup. SQLite can still hold the file handle briefly after the connection is
    /// disposed, so a failed delete must never be reported as a test failure — the scratch file
    /// lives in the temp directory either way.
    /// </summary>
    private static void Delete(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);

        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);

            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
