using Npgsql;

namespace DatabaseAnalyzer.Runner;

/// <summary>
/// Поставщик охранных блокировок на базе PostgreSQL session-level advisory locks и файловых блокировок рабочего пространства (TD-003).
/// Обеспечивает защиту рабочего каталога анализа через shared session-level advisory locks для воркеров и исключение очистителя.
/// </summary>
public sealed class PostgreSqlAnalysisLeaseGuardProvider : IAnalysisLeaseGuardProvider
{
    private readonly string _connectionString;
    private readonly Application.Interfaces.IWorkspaceFileLockProvider _workspaceFileLockProvider;

    public PostgreSqlAnalysisLeaseGuardProvider(
        string connectionString,
        Application.Interfaces.IWorkspaceFileLockProvider workspaceFileLockProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;
        _workspaceFileLockProvider = workspaceFileLockProvider
            ?? throw new ArgumentNullException(nameof(workspaceFileLockProvider));
    }

    /// <inheritdoc />
    public async Task<IAnalysisLeaseGuard> AcquireGuardAsync(
        int analysisId,
        Guid executionToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(analysisId);
        if (executionToken == Guid.Empty)
        {
            throw new ArgumentException("Не указан токен выполнения анализа.", nameof(executionToken));
        }

        NpgsqlConnection? connection = null;
        FileStream? workspaceFileLock = null;

        // Попытка с защитой от разорванных пуловых соединений (stale pooled connections)
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);

                // 4. Получить PostgreSQL SharedLock
                await using var lockCmd = connection.CreateCommand();
                lockCmd.CommandText = "SELECT pg_try_advisory_lock_shared(hashtext('netscope_analysis_workspace'), @analysisId);";
                lockCmd.Parameters.AddWithValue("analysisId", analysisId);

                var lockResult = await lockCmd.ExecuteScalarAsync(cancellationToken);
                var isLocked = lockResult is bool b && b;

                // 5. Если PostgreSQL SharedLock не получен
                if (!isLocked)
                {
                    await connection.DisposeAsync();
                    return new PostgreSqlAnalysisLeaseGuard(analysisId, executionToken, isAcquired: false, null, null);
                }

                // 6. Если PostgreSQL SharedLock получен, вызвать TryAcquireShared
                workspaceFileLock = _workspaceFileLockProvider.TryAcquireShared(analysisId);

                // 7. Если файловая блокировка не получена
                if (workspaceFileLock == null)
                {
                    try
                    {
                        await using var unlockCmd = connection.CreateCommand();
                        unlockCmd.CommandText = "SELECT pg_advisory_unlock_shared(hashtext('netscope_analysis_workspace'), @analysisId);";
                        unlockCmd.Parameters.AddWithValue("analysisId", analysisId);
                        await unlockCmd.ExecuteScalarAsync(CancellationToken.None);
                    }
                    catch
                    {
                    }
                    await connection.DisposeAsync();
                    return new PostgreSqlAnalysisLeaseGuard(analysisId, executionToken, isAcquired: false, null, null);
                }

                // 8. Если обе блокировки получены, создать guard
                return new PostgreSqlAnalysisLeaseGuard(
                    analysisId,
                    executionToken,
                    isAcquired: true,
                    connection,
                    workspaceFileLock);
            }
            catch (NpgsqlException) when (attempt == 0)
            {
                if (workspaceFileLock != null)
                {
                    await workspaceFileLock.DisposeAsync();
                    workspaceFileLock = null;
                }
                if (connection != null)
                {
                    await connection.DisposeAsync();
                    connection = null;
                }
            }
            catch
            {
                if (workspaceFileLock != null)
                {
                    try { await workspaceFileLock.DisposeAsync(); } catch { }
                    workspaceFileLock = null;
                }
                if (connection != null)
                {
                    try { await connection.DisposeAsync(); } catch { }
                    connection = null;
                }
                throw;
            }
        }

        return new PostgreSqlAnalysisLeaseGuard(analysisId, executionToken, isAcquired: false, null, null);
    }
}
