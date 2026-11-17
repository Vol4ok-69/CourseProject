
using Npgsql;
using System.Data;

namespace DatabaseAnalyzer.Runner;

/// <summary>
/// Поставщик охранных блокировок на базе PostgreSQL session-level advisory locks
/// и файловых блокировок рабочего пространства (TD-003).
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
            throw new ArgumentException(
                "Не указан токен выполнения анализа.",
                nameof(executionToken));
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            NpgsqlConnection? connection = null;
            FileStream? workspaceFileLock = null;

            var lockAttemptStarted = false;
            var lockAttemptCompleted = false;
            var postgresLockAcquired = false;

            try
            {
                connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);

                await using (var lockCommand = connection.CreateCommand())
                {
                    lockCommand.CommandText = """
                        SELECT pg_try_advisory_lock_shared(
                            hashtext('netscope_analysis_workspace'),
                            @analysisId);
                        """;

                    lockCommand.Parameters.AddWithValue("analysisId", analysisId);

                    lockAttemptStarted = true;

                    var lockResult = await lockCommand.ExecuteScalarAsync(cancellationToken);

                    lockAttemptCompleted = true;
                    postgresLockAcquired = lockResult is true;
                }

                if (!postgresLockAcquired)
                {
                    await ReleaseResourcesAsync(
                        connection,
                        workspaceFileLock,
                        analysisId,
                        lockAttemptStarted,
                        lockAttemptCompleted,
                        postgresLockAcquired);

                    connection = null;
                    workspaceFileLock = null;

                    return CreateNotAcquiredGuard(analysisId, executionToken);
                }

                workspaceFileLock = _workspaceFileLockProvider.TryAcquireShared(analysisId);

                if (workspaceFileLock == null)
                {
                    await ReleaseResourcesAsync(
                        connection,
                        workspaceFileLock,
                        analysisId,
                        lockAttemptStarted,
                        lockAttemptCompleted,
                        postgresLockAcquired);

                    connection = null;
                    workspaceFileLock = null;

                    return CreateNotAcquiredGuard(analysisId, executionToken);
                }

                var guard = new PostgreSqlAnalysisLeaseGuard(
                    analysisId,
                    executionToken,
                    isAcquired: true,
                    connection,
                    workspaceFileLock);

                // Guard принимает владение обоими ресурсами.
                connection = null;
                workspaceFileLock = null;

                return guard;
            }
            catch (NpgsqlException) when (attempt == 0)
            {
                await ReleaseResourcesAsync(
                    connection,
                    workspaceFileLock,
                    analysisId,
                    lockAttemptStarted,
                    lockAttemptCompleted,
                    postgresLockAcquired);
            }
            catch
            {
                await ReleaseResourcesAsync(
                    connection,
                    workspaceFileLock,
                    analysisId,
                    lockAttemptStarted,
                    lockAttemptCompleted,
                    postgresLockAcquired);

                throw;
            }
        }

        return CreateNotAcquiredGuard(analysisId, executionToken);
    }

    private static PostgreSqlAnalysisLeaseGuard CreateNotAcquiredGuard(
        int analysisId,
        Guid executionToken)
    {
        return new PostgreSqlAnalysisLeaseGuard(
            analysisId,
            executionToken,
            isAcquired: false,
            connection: null,
            workspaceFileLock: null);
    }

    private static async Task ReleaseResourcesAsync(
        NpgsqlConnection? connection,
        FileStream? workspaceFileLock,
        int analysisId,
        bool lockAttemptStarted,
        bool lockAttemptCompleted,
        bool postgresLockAcquired)
    {
        // Сначала освобождаем файловый дескриптор.
        if (workspaceFileLock != null)
        {
            try
            {
                await workspaceFileLock.DisposeAsync();
            }
            catch
            {
                try
                {
                    workspaceFileLock.Dispose();
                }
                catch
                {
                    // Продолжаем освобождение PostgreSQL-соединения.
                }
            }
        }

        if (connection == null)
        {
            return;
        }

        try
        {
            // Если запрос был отправлен, но результат не получен,
            // состояние session-level lock неизвестно.
            var mustClearPool = lockAttemptStarted && !lockAttemptCompleted;

            if (postgresLockAcquired)
            {
                if (connection.State == ConnectionState.Open)
                {
                    try
                    {
                        await using var unlockCommand = connection.CreateCommand();

                        unlockCommand.CommandText = """
                            SELECT pg_advisory_unlock_shared(
                                hashtext('netscope_analysis_workspace'),
                                @analysisId);
                            """;

                        unlockCommand.Parameters.AddWithValue("analysisId", analysisId);

                        // Завершившийся запрос подтверждает, что сервер обработал unlock.
                        // false означает, что блокировка уже отсутствовала.
                        await unlockCommand.ExecuteScalarAsync(CancellationToken.None);
                    }
                    catch
                    {
                        mustClearPool = true;
                    }
                }
                else
                {
                    mustClearPool = true;
                }
            }

            if (mustClearPool)
            {
                try
                {
                    // Соединение с неизвестным состоянием блокировки не должно
                    // возвращаться в пул для повторного использования.
                    NpgsqlConnection.ClearPool(connection);
                }
                catch
                {
                    // Не прерываем обязательное закрытие соединения.
                }
            }
        }
        finally
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch
            {
                // Не скрываем исходное исключение ошибкой освобождения соединения.
            }
        }
    }
}
