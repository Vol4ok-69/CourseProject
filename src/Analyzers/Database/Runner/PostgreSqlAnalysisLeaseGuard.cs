using System.Data;
using Npgsql;

namespace DatabaseAnalyzer.Runner;

/// <summary>
/// Реализация lease guard на базе shared session-level advisory lock PostgreSQL (TD-003).
/// Владеет выделенным физическим соединением NpgsqlConnection, удерживающим блокировку,
/// и файловым дескриптором совместной блокировки на протяжении защищённых операций с рабочим каталогом анализа.
/// </summary>
public sealed class PostgreSqlAnalysisLeaseGuard : IAnalysisLeaseGuard
{
    private readonly NpgsqlConnection? _connection;
    private readonly FileStream? _workspaceFileLock;
    private readonly int? _connectionProcessId;
    private int _disposed;

    public PostgreSqlAnalysisLeaseGuard(
        int analysisId,
        Guid executionToken,
        bool isAcquired,
        NpgsqlConnection? connection,
        FileStream? workspaceFileLock)
    {
        AnalysisId = analysisId;
        ExecutionToken = executionToken;
        IsAcquired = isAcquired;
        _connection = connection;
        _workspaceFileLock = workspaceFileLock;

        try
        {
            _connectionProcessId = connection?.ProcessID;
        }
        catch
        {
            _connectionProcessId = null;
        }
    }

    /// <inheritdoc />
    public int AnalysisId { get; }

    /// <inheritdoc />
    public Guid ExecutionToken { get; }

    /// <inheritdoc />
    public bool IsAcquired { get; }

    /// <inheritdoc />
    public bool RequiresSessionAdvisoryLock => true;

    /// <summary>
    /// Физическое соединение NpgsqlConnection, удерживающее shared session-level advisory lock.
    /// </summary>
    public System.Data.Common.DbConnection? Connection =>
        _disposed == 0 && _connection?.State == ConnectionState.Open ? _connection : null;

    /// <summary>
    /// Идентификатор серверного процесса PostgreSQL (backend PID) удерживаемого соединения.
    /// </summary>
    public int? ConnectionProcessId
    {
        get
        {
            if (_disposed != 0 || _connection == null)
            {
                return null;
            }

            return _connectionProcessId > 0 ? _connectionProcessId : null;
        }
    }

    /// <inheritdoc />
    public Task<bool> ValidateOwnershipAsync(CancellationToken cancellationToken = default)
        => ValidateOwnershipAsync([1, 2, 3], cancellationToken);

    /// <inheritdoc />
    public async Task<bool> ValidateOwnershipAsync(int[] allowedStatuses, CancellationToken cancellationToken = default)
    {
        if (_disposed != 0 || !IsAcquired || _connection == null)
        {
            return false;
        }

        if (_workspaceFileLock == null ||
            !_workspaceFileLock.CanRead ||
            _workspaceFileLock.SafeFileHandle.IsClosed ||
            _workspaceFileLock.SafeFileHandle.IsInvalid)
        {
            return false;
        }

        if (_connection.State != ConnectionState.Open)
        {
            return false;
        }

        if (allowedStatuses == null || allowedStatuses.Length == 0)
        {
            allowedStatuses = [1, 2, 3];
        }

        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT CASE
                    WHEN EXISTS (
                        SELECT 1 FROM pg_locks
                        WHERE locktype = 'advisory'
                          AND classid = hashtext('netscope_analysis_workspace')
                          AND objid = @analysisId
                          AND objsubid = 2
                          AND pid = pg_backend_pid()
                          AND mode = 'ShareLock'
                          AND granted = true
                    ) AND EXISTS (
                        SELECT 1 FROM "Analyses"
                        WHERE "Id" = @analysisId
                          AND "ExecutionToken" = @executionToken
                          AND "Status" = ANY(@allowedStatuses)
                    ) THEN 1
                    ELSE 0
                END;
                """;

            cmd.Parameters.AddWithValue("analysisId", AnalysisId);
            cmd.Parameters.AddWithValue("executionToken", ExecutionToken);
            cmd.Parameters.AddWithValue("allowedStatuses", allowedStatuses);

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result is 1 or 1L;
        }
        catch
        {
            // При сбое сети, обрыве сокета или потере соединения подтверждение владения возвращает false
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_connection != null)
            {
                try
                {
                    if (IsAcquired && _connection.State == ConnectionState.Open)
                    {
                        await using var unlockCmd = _connection.CreateCommand();
                        unlockCmd.CommandText = "SELECT pg_advisory_unlock_shared(hashtext('netscope_analysis_workspace'), @analysisId);";
                        unlockCmd.Parameters.AddWithValue("analysisId", AnalysisId);
                        await unlockCmd.ExecuteScalarAsync(CancellationToken.None);
                    }
                }
                catch
                {
                    // При ошибке снятия блокировки принудительно очищаем пул для этого соединения,
                    // чтобы исключить передачу загрязнённого физического соединения другим клиентам.
                    try
                    {
                        NpgsqlConnection.ClearPool(_connection);
                    }
                    catch
                    {
                    }
                }
                finally
                {
                    // Закрываем физическое соединение и возвращаем в пул
                    await _connection.DisposeAsync();
                }
            }
        }
        finally
        {
            if (_workspaceFileLock != null)
            {
                try
                {
                    await _workspaceFileLock.DisposeAsync();
                }
                catch
                {
                    _workspaceFileLock.Dispose();
                }
            }
        }
    }
}
