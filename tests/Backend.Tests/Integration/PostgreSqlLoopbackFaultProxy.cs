using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace Backend.Tests.Integration;

/// <summary>
/// Реальный in-process TCP proxy для инъекции сетевых сбоев между Npgsql и PostgreSQL.
/// Реализует потоковый парсинг бинарного PostgreSQL wire protocol 3.0 без регулярных выражений,
/// корректно обрабатывает склеенные (coalesced) и частичные (partial) сообщения и управляет
/// жизненным циклом фоновых TCP-задач.
/// </summary>
public sealed class PostgreSqlLoopbackFaultProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly IPEndPoint _targetEndPoint;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Task, bool> _activeConnections = new();
    private Task? _acceptLoopTask;

    public int LocalPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>
    /// Если true, при получении от сервера подтверждения COMMIT (CommandComplete 'C' с тегом COMMIT),
    /// прокси НЕ пересылает пакет клиенту, а немедленно разрывает TCP-сокет клиента с RST.
    /// </summary>
    public volatile bool DropOnCommitAck;

    /// <summary>
    /// Если true, после сброса подтверждения COMMIT (CommitAckIntercepted == true),
    /// все последующие попытки подключения к прокси немедленно сбрасываются с TCP RST.
    /// Позволяет имитировать сценарий недоступности базы данных при recovery-проверке (Scenario C).
    /// </summary>
    public volatile bool RejectNewConnectionsAfterDrop;

    /// <summary>
    /// Если true, при получении от клиента команды COMMIT, прокси разрывает сокет ДО
    /// отправки команды на сервер PostgreSQL (транзакция не фиксируется сервером).
    /// </summary>
    public volatile bool DropOnClientCommit;

    /// <summary>
    /// Флаг фиксации того, что в потоке клиент-сервер была распознана команда COMMIT в разобранном сообщении.
    /// </summary>
    public volatile bool CommitDetected;

    /// <summary>
    /// Флаг фиксации того, что ответ CommandComplete('C') с тегом COMMIT был перехвачен и сброшен.
    /// </summary>
    public volatile bool CommitAckIntercepted;

    /// <summary>
    /// Флаг фиксации того, что клиентский сокет был принудительно разорван через RST (LingerState=0).
    /// </summary>
    public volatile bool ClientSocketAborted;

    public PostgreSqlLoopbackFaultProxy(IPEndPoint targetEndPoint)
    {
        _targetEndPoint = targetEndPoint;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _acceptLoopTask = Task.Run(AcceptLoopAsync);
    }

    public static PostgreSqlLoopbackFaultProxy Create(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var host = builder.Host ?? "127.0.0.1";
        var port = builder.Port > 0 ? builder.Port : 5432;
        var addresses = Dns.GetHostAddresses(host);
        var targetIp = addresses.First(a => a.AddressFamily == AddressFamily.InterNetwork);
        var targetEndPoint = new IPEndPoint(targetIp, port);

        return new PostgreSqlLoopbackFaultProxy(targetEndPoint);
    }

    public string GetProxiedConnectionString(string originalConnectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(originalConnectionString)
        {
            Host = IPAddress.Loopback.ToString(),
            Port = LocalPort,
            Pooling = false, // Отключаем пул для выделенного TCP-соединения
            SslMode = SslMode.Disable // Отключаем SSL для прозрачного парсинга wire protocol
        };
        return builder.ConnectionString;
    }

    public void Reset()
    {
        DropOnCommitAck = false;
        DropOnClientCommit = false;
        RejectNewConnectionsAfterDrop = false;
        CommitDetected = false;
        CommitAckIntercepted = false;
        ClientSocketAborted = false;
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var clientSocket = await _listener.AcceptSocketAsync(_cts.Token);
                if (RejectNewConnectionsAfterDrop && CommitAckIntercepted)
                {
                    try
                    {
                        clientSocket.LingerState = new LingerOption(enable: true, seconds: 0);
                        clientSocket.Close(0);
                    }
                    catch
                    {
                    }
                    continue;
                }
                var connTask = Task.Run(() => HandleClientAsync(clientSocket, _cts.Token));
                _activeConnections.TryAdd(connTask, true);
                _ = connTask.ContinueWith(t => _activeConnections.TryRemove(t, out _));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }

    private async Task HandleClientAsync(Socket clientSocket, CancellationToken ct)
    {
        using var serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sessionToken = sessionCts.Token;

        try
        {
            await serverSocket.ConnectAsync(_targetEndPoint, sessionToken);

            using var clientStream = new NetworkStream(clientSocket, ownsSocket: false);
            using var serverStream = new NetworkStream(serverSocket, ownsSocket: false);

            var clientParser = new PostgreSqlProtocolParser();
            var serverParser = new PostgreSqlProtocolParser();
            var isStartupComplete = false;

            var clientToServer = Task.Run(async () =>
            {
                var buffer = new byte[8192];
                try
                {
                    while (!sessionToken.IsCancellationRequested)
                    {
                        int read;
                        try
                        {
                            read = await clientStream.ReadAsync(buffer, 0, buffer.Length, sessionToken);
                        }
                        catch
                        {
                            break;
                        }

                        if (read == 0) break;

                        clientParser.Append(buffer, 0, read);

                        // Начальная фаза подключения (StartupMessage без 1-байтового типа)
                        if (!isStartupComplete)
                        {
                            if (clientParser.TryReadStartupMessage(out var startupMsg))
                            {
                                isStartupComplete = true;
                                await serverStream.WriteAsync(startupMsg!, 0, startupMsg!.Length, sessionToken);
                                await serverStream.FlushAsync(sessionToken);
                            }
                            else
                            {
                                continue;
                            }
                        }

                        // Потоковый разбор стандартных frontend-сообщений PostgreSQL 3.0
                        while (clientParser.TryReadMessage(out var msg))
                        {
                            if (PostgreSqlProtocolParser.IsCommitCommand(msg!))
                            {
                                CommitDetected = true;

                                // Сброс ДО фиксации коммита в PostgreSQL
                                if (DropOnClientCommit)
                                {
                                    try
                                    {
                                        clientSocket.LingerState = new LingerOption(true, 0);
                                        clientSocket.Close();
                                        ClientSocketAborted = true;
                                    }
                                    catch
                                    {
                                    }
                                    sessionCts.Cancel();
                                    return;
                                }
                            }

                            var frameBytes = msg!.ToBytes();
                            await serverStream.WriteAsync(frameBytes, 0, frameBytes.Length, sessionToken);
                            await serverStream.FlushAsync(sessionToken);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception)
                {
                }
                finally
                {
                    sessionCts.Cancel();
                }
            }, sessionToken);

            var serverToClient = Task.Run(async () =>
            {
                var buffer = new byte[8192];
                try
                {
                    while (!sessionToken.IsCancellationRequested)
                    {
                        int read;
                        try
                        {
                            read = await serverStream.ReadAsync(buffer, 0, buffer.Length, sessionToken);
                        }
                        catch
                        {
                            break;
                        }

                        if (read == 0) break;

                        serverParser.Append(buffer, 0, read);

                        // Потоковый разбор серверных сообщений PostgreSQL 3.0
                        while (serverParser.TryReadMessage(out var msg))
                        {
                            if (PostgreSqlProtocolParser.IsCommitCommandComplete(msg!))
                            {
                                if (DropOnCommitAck && CommitDetected)
                                {
                                    // Пакет CommandComplete('C') с тегом COMMIT перехвачен!
                                    CommitAckIntercepted = true;
                                    try
                                    {
                                        // Немедленно прерываем сокет клиента через TCP RST
                                        clientSocket.LingerState = new LingerOption(true, 0);
                                        clientSocket.Close();
                                        ClientSocketAborted = true;
                                    }
                                    catch
                                    {
                                    }
                                    sessionCts.Cancel();
                                    return;
                                }
                            }

                            var frameBytes = msg!.ToBytes();
                            await clientStream.WriteAsync(frameBytes, 0, frameBytes.Length, sessionToken);
                            await clientStream.FlushAsync(sessionToken);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception)
                {
                }
                finally
                {
                    sessionCts.Cancel();
                }
            }, sessionToken);

            try
            {
                await Task.WhenAll(clientToServer, serverToClient);
            }
            catch
            {
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
        finally
        {
            try { clientSocket.Dispose(); } catch { }
            try { serverSocket.Dispose(); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
        }

        if (_acceptLoopTask != null)
        {
            try
            {
                await _acceptLoopTask;
            }
            catch
            {
            }
        }

        var active = _activeConnections.Keys.ToArray();
        if (active.Length > 0)
        {
            try
            {
                await Task.WhenAll(active);
            }
            catch
            {
            }
        }

        _cts.Dispose();
    }
}
