using Mailcoded.Core.Providers;
using Microsoft.Data.Sqlite;

namespace Mailcoded.Core.Store;

public sealed partial class SqliteStore
{
    /// <summary>VACUUM INTO on its own read-only connection: a long copy never blocks the writer thread.</summary>
    public Task BackupToAsync(string destinationPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ThrowIfDisposed();

        var fullPath = Path.GetFullPath(destinationPath);
        if (File.Exists(fullPath) && new FileInfo(fullPath).Length > 0)
            throw new StoreException(FailureCategory.Protocol, "The backup destination already exists.");

        var directory = Path.GetDirectoryName(fullPath);
        if (directory is not null) Directory.CreateDirectory(directory);

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var connection = new SqliteConnection(_snapshotConnectionString);
                connection.Open();

                using var timeout = connection.CreateCommand();
                timeout.CommandText = Pragma("busy_timeout", _options.BusyTimeoutMs);
                timeout.ExecuteNonQuery();

                using var command = connection.CreateCommand();
                command.CommandText = "VACUUM INTO $destination";
                command.Parameters.AddWithValue("$destination", fullPath);

                // Declared after the connection so it is disposed first: the callback never sees a closed handle.
                using var interrupt = ct.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
                command.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ct.IsCancellationRequested && ex.SqliteErrorCode == SqliteInterrupt)
            {
                TryDelete(fullPath);
                throw new OperationCanceledException(ct);
            }
            catch (SqliteException ex)
            {
                throw Db.Translate(ex);
            }
        }, ct);
    }

    private const int SqliteInterrupt = 9;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static int LatestSchemaVersion => Migrator.LatestVersion;
}
