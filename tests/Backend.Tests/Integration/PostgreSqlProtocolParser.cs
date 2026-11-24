using System.Buffers.Binary;
using System.Text;

namespace Backend.Tests.Integration;

/// <summary>
/// Представление разобранного сообщения PostgreSQL wire protocol 3.0.
/// </summary>
public sealed class PostgreSqlProtocolMessage
{
    public byte Type { get; }
    public byte[] Payload { get; }
    public int Length => Payload.Length + 4;

    public PostgreSqlProtocolMessage(byte type, byte[] payload)
    {
        Type = type;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    /// <summary>
    /// Возвращает полный фрейм сообщения (1 байт типа + 4 байта длины + payload).
    /// </summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[1 + 4 + Payload.Length];
        bytes[0] = Type;
        int msgLen = 4 + Payload.Length;
        bytes[1] = (byte)(msgLen >> 24);
        bytes[2] = (byte)(msgLen >> 16);
        bytes[3] = (byte)(msgLen >> 8);
        bytes[4] = (byte)msgLen;
        if (Payload.Length > 0)
        {
            Array.Copy(Payload, 0, bytes, 5, Payload.Length);
        }
        return bytes;
    }
}

/// <summary>
/// Потоковый парсер PostgreSQL wire protocol 3.0.
/// Обрабатывает частичные чтения (partial reads), склеенные сообщения (coalesced messages),
/// валидацию фрейминга с защитой от переполнений и структурную валидацию сообщений.
/// </summary>
public sealed class PostgreSqlProtocolParser
{
    public const int MaxAllowedMessageLength = 16 * 1024 * 1024; // 16 MB

    private byte[] _buffer = new byte[8192];
    private int _start = 0;
    private int _length = 0;

    public int AvailableBytes => _length;

    public void Append(byte[] data, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (offset < 0 || count < 0 || offset + count > data.Length)
            throw new ArgumentOutOfRangeException(nameof(count));

        if (count == 0) return;

        if (_buffer.Length - (_start + _length) < count)
        {
            if (_length + count > _buffer.Length)
            {
                var newCapacity = Math.Max(_buffer.Length * 2, _length + count);
                var newBuffer = new byte[newCapacity];
                Array.Copy(_buffer, _start, newBuffer, 0, _length);
                _buffer = newBuffer;
                _start = 0;
            }
            else
            {
                Array.Copy(_buffer, _start, _buffer, 0, _length);
                _start = 0;
            }
        }

        Array.Copy(data, offset, _buffer, _start + _length, count);
        _length += count;
    }

    /// <summary>
    /// Пытается прочитать следующее стандартное PostgreSQL 3.0 сообщение (1 байт типа + 4 байта длины + payload).
    /// Выполняет проверку фрейминга (framing validation) с защитой от целочисленного переполнения.
    /// </summary>
    public bool TryReadMessage(out PostgreSqlProtocolMessage? message)
    {
        if (_length < 5)
        {
            message = null;
            return false;
        }

        byte type = _buffer[_start];
        long msgLen = ((long)_buffer[_start + 1] << 24) |
                      ((long)_buffer[_start + 2] << 16) |
                      ((long)_buffer[_start + 3] << 8) |
                       (long)_buffer[_start + 4];

        if (msgLen < 4)
        {
            throw new InvalidDataException($"Invalid PostgreSQL message length {msgLen} (must be >= 4).");
        }

        long totalMessageBytes = 1L + msgLen;
        if (totalMessageBytes > (long)MaxAllowedMessageLength + 1L)
        {
            throw new InvalidDataException($"PostgreSQL message length {msgLen} exceeds maximum allowed size ({MaxAllowedMessageLength} bytes).");
        }

        if (_length < totalMessageBytes)
        {
            message = null;
            return false;
        }

        long payloadLength = msgLen - 4;
        var payload = new byte[payloadLength];
        if (payloadLength > 0)
        {
            Array.Copy(_buffer, _start + 5, payload, 0, (int)payloadLength);
        }

        _start += (int)totalMessageBytes;
        _length -= (int)totalMessageBytes;

        message = new PostgreSqlProtocolMessage(type, payload);
        return true;
    }

    /// <summary>
    /// Пытается прочитать начальное сообщение подключения (StartupMessage или SSLRequest),
    /// не имеющее 1-байтового префикса типа.
    /// </summary>
    public bool TryReadStartupMessage(out byte[]? startupMessage)
    {
        if (_length < 4)
        {
            startupMessage = null;
            return false;
        }

        long len = ((long)_buffer[_start] << 24) |
                   ((long)_buffer[_start + 1] << 16) |
                   ((long)_buffer[_start + 2] << 8) |
                    (long)_buffer[_start + 3];

        if (len < 8 || len > MaxAllowedMessageLength)
        {
            throw new InvalidDataException($"Invalid PostgreSQL startup message length {len}.");
        }

        if (_length < len)
        {
            startupMessage = null;
            return false;
        }

        startupMessage = new byte[len];
        Array.Copy(_buffer, _start, startupMessage, 0, (int)len);
        _start += (int)len;
        _length -= (int)len;
        return true;
    }

    /// <summary>
    /// Базовая операция чтения нуль-терминированной C-string из среза байтов.
    /// Смещает offset за завершающий нулевой байт.
    /// </summary>
    public static bool TryReadNullTerminatedString(
        ReadOnlySpan<byte> payload,
        scoped ref int offset,
        out ReadOnlySpan<byte> value)
    {
        value = default;

        if (offset < 0 || offset >= payload.Length)
        {
            return false;
        }

        var remaining = payload[offset..];
        var terminatorIndex = remaining.IndexOf((byte)0);

        if (terminatorIndex < 0)
        {
            return false;
        }

        value = remaining[..terminatorIndex];
        offset += terminatorIndex + 1;

        return true;
    }

    /// <summary>
    /// Проверяет структурную корректность payload сообщения Simple Query 'Q':
    /// строка Query, оканчивающаяся на NUL, без каких-либо дополнительных байтов после NUL.
    /// </summary>
    public static bool IsValidQueryPayload(ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> query)
    {
        query = default;
        var offset = 0;

        if (!TryReadNullTerminatedString(payload, ref offset, out query))
        {
            return false;
        }

        if (offset != payload.Length)
        {
            return false; // Запрещены дополнительные байты после завершающего NUL
        }

        return true;
    }

    public static bool IsValidQueryPayload(ReadOnlySpan<byte> payload) => IsValidQueryPayload(payload, out _);

    /// <summary>
    /// Проверяет структурную корректность payload сообщения Parse 'P':
    /// StatementName\0, Query\0, ParameterCount (Int16 big-endian >= 0), ровно ParameterCount * 4 байтов OID,
    /// и строгое отсутствие лишних байтов после последнего OID.
    /// </summary>
    public static bool IsValidParsePayload(
        ReadOnlySpan<byte> payload,
        out ReadOnlySpan<byte> statementName,
        out ReadOnlySpan<byte> query,
        out short parameterCount)
    {
        statementName = default;
        query = default;
        parameterCount = 0;

        var offset = 0;

        if (!TryReadNullTerminatedString(payload, ref offset, out statementName))
        {
            return false;
        }

        if (!TryReadNullTerminatedString(payload, ref offset, out query))
        {
            return false;
        }

        if (payload.Length - offset < sizeof(short))
        {
            return false;
        }

        parameterCount = BinaryPrimitives.ReadInt16BigEndian(payload.Slice(offset, sizeof(short)));
        offset += sizeof(short);

        if (parameterCount < 0)
        {
            return false;
        }

        long expectedOidBytes = (long)parameterCount * sizeof(int);
        if (payload.Length - offset != expectedOidBytes)
        {
            return false;
        }

        return true;
    }

    public static bool IsValidParsePayload(ReadOnlySpan<byte> payload) =>
        IsValidParsePayload(payload, out _, out _, out _);

    /// <summary>
    /// Проверяет структурную корректность payload сообщения CommandComplete 'C':
    /// строка CommandTag, оканчивающаяся на NUL, без каких-либо дополнительных байтов после NUL.
    /// </summary>
    public static bool IsValidCommandCompletePayload(ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> commandTag)
    {
        commandTag = default;
        var offset = 0;

        if (!TryReadNullTerminatedString(payload, ref offset, out commandTag))
        {
            return false;
        }

        if (offset != payload.Length)
        {
            return false; // Запрещены дополнительные байты после завершающего NUL
        }

        return true;
    }

    public static bool IsValidCommandCompletePayload(ReadOnlySpan<byte> payload) =>
        IsValidCommandCompletePayload(payload, out _);

    /// <summary>
    /// Проверяет, является ли сообщение клиентской командой фиксации транзакции COMMIT
    /// в Simple Query Protocol ('Q') со строгой валидацией структуры фрейма.
    /// В соответствии с семантикой протокола PostgreSQL сообщения Extended Query Protocol (Parse 'P', Bind 'B')
    /// лишь подготавливают оператор, но не исполняют его, и поэтому не являются командой выполнения COMMIT.
    /// </summary>
    public static bool IsCommitCommand(PostgreSqlProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Simple Query 'Q': payload - C-string (null-terminated)
        if (message.Type == (byte)'Q')
        {
            if (!IsValidQueryPayload(message.Payload, out var querySpan))
            {
                return false;
            }

            var sql = Encoding.UTF8.GetString(querySpan).Trim();
            return sql.TrimEnd(';').Equals("COMMIT", StringComparison.OrdinalIgnoreCase);
        }

        // Parse 'P' лишь подготавливает оператор, но не исполняет его.
        // Сообщения Extended Query Protocol не признаются командой исполнения COMMIT.
        return false;
    }

    /// <summary>
    /// Проверяет, является ли серверное сообщение подтверждением успешного COMMIT
    /// (CommandComplete 'C' с тегом COMMIT) со строгой валидацией структуры фрейма.
    /// </summary>
    public static bool IsCommitCommandComplete(PostgreSqlProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Type != (byte)'C')
        {
            return false;
        }

        if (!IsValidCommandCompletePayload(message.Payload, out var tagSpan))
        {
            return false;
        }

        var tag = Encoding.ASCII.GetString(tagSpan).Trim();
        return tag.Equals("COMMIT", StringComparison.OrdinalIgnoreCase);
    }
}
