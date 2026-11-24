using System.Buffers.Binary;
using System.Text;
using Backend.Tests.Integration;
using Xunit;

namespace Backend.Tests.Unit;

public sealed class PostgreSqlProtocolParserTests
{
    private static byte[] CreateFramedMessage(byte type, byte[] payload)
    {
        return new PostgreSqlProtocolMessage(type, payload).ToBytes();
    }

    private static byte[] CreateQueryMessage(string sql)
    {
        var sqlBytes = Encoding.UTF8.GetBytes(sql + "\0");
        return CreateFramedMessage((byte)'Q', sqlBytes);
    }

    private static byte[] CreateCommandCompleteMessage(string tag)
    {
        var tagBytes = Encoding.ASCII.GetBytes(tag + "\0");
        return CreateFramedMessage((byte)'C', tagBytes);
    }

    private static byte[] CreateParsePayload(
        string? statementName,
        bool includeStmtNul,
        string? query,
        bool includeQueryNul,
        short? parameterCount,
        int[]? oids,
        byte[]? extraBytes = null)
    {
        using var ms = new MemoryStream();

        if (statementName != null)
        {
            ms.Write(Encoding.UTF8.GetBytes(statementName));
        }
        if (includeStmtNul)
        {
            ms.WriteByte(0);
        }

        if (query != null)
        {
            ms.Write(Encoding.UTF8.GetBytes(query));
        }
        if (includeQueryNul)
        {
            ms.WriteByte(0);
        }

        if (parameterCount.HasValue)
        {
            Span<byte> countSpan = stackalloc byte[2];
            BinaryPrimitives.WriteInt16BigEndian(countSpan, parameterCount.Value);
            ms.Write(countSpan);
        }

        if (oids != null)
        {
            Span<byte> oidSpan = stackalloc byte[4];
            foreach (var oid in oids)
            {
                BinaryPrimitives.WriteInt32BigEndian(oidSpan, oid);
                ms.Write(oidSpan);
            }
        }

        if (extraBytes != null && extraBytes.Length > 0)
        {
            ms.Write(extraBytes);
        }

        return ms.ToArray();
    }

    #region 3.1 Query (Q) Tests

    [Fact]
    public void Query_ValidWithTerminatingNulAndCommit_RecognizedAsCommit()
    {
        var bytes = CreateQueryMessage("COMMIT");
        var parser = new PostgreSqlProtocolParser();
        parser.Append(bytes, 0, bytes.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.True(PostgreSqlProtocolParser.IsValidQueryPayload(msg.Payload));
        Assert.True(PostgreSqlProtocolParser.IsCommitCommand(msg));
    }

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("BEGIN")]
    [InlineData("ROLLBACK")]
    [InlineData("UPDATE Analyses SET Status = 2")]
    public void Query_ValidWithTerminatingNulAndOtherSql_CommitNotRecognized(string sql)
    {
        var bytes = CreateQueryMessage(sql);
        var parser = new PostgreSqlProtocolParser();
        parser.Append(bytes, 0, bytes.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.True(PostgreSqlProtocolParser.IsValidQueryPayload(msg.Payload));
        Assert.False(PostgreSqlProtocolParser.IsCommitCommand(msg));
    }

    [Fact]
    public void Query_WithoutTerminatingNul_RejectedAsMalformed()
    {
        // Строка "COMMIT" без завершающего '\0'
        var rawQuery = Encoding.UTF8.GetBytes("COMMIT");
        var framed = CreateFramedMessage((byte)'Q', rawQuery);

        var parser = new PostgreSqlProtocolParser();
        parser.Append(framed, 0, framed.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.False(PostgreSqlProtocolParser.IsValidQueryPayload(msg.Payload));
        Assert.False(PostgreSqlProtocolParser.IsCommitCommand(msg));
    }

    [Fact]
    public void Query_WithNulInsideStringAndExtraBytesAfter_RejectedAsMalformed()
    {
        // "COMMIT\0EXTRA" - после NUL идут посторонние байты
        var rawQuery = Encoding.UTF8.GetBytes("COMMIT\0EXTRA");
        var framed = CreateFramedMessage((byte)'Q', rawQuery);

        var parser = new PostgreSqlProtocolParser();
        parser.Append(framed, 0, framed.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.False(PostgreSqlProtocolParser.IsValidQueryPayload(msg.Payload));
        Assert.False(PostgreSqlProtocolParser.IsCommitCommand(msg));
    }

    [Fact]
    public void Query_EmptyWithNul_StructurallyValid_CommitNotRecognized()
    {
        // Пустой SQL запрос: "\0"
        var rawQuery = new byte[] { 0 };
        var framed = CreateFramedMessage((byte)'Q', rawQuery);

        var parser = new PostgreSqlProtocolParser();
        parser.Append(framed, 0, framed.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.True(PostgreSqlProtocolParser.IsValidQueryPayload(msg.Payload));
        Assert.False(PostgreSqlProtocolParser.IsCommitCommand(msg));
    }

    [Fact]
    public void Query_PartialPayload_NotAcceptedUntilFull()
    {
        var fullBytes = CreateQueryMessage("COMMIT");
        var parser = new PostgreSqlProtocolParser();

        // Передаём заголовок (5 байт) и часть тела (не до конца)
        parser.Append(fullBytes, 0, fullBytes.Length - 2);
        Assert.False(parser.TryReadMessage(out var msg1));
        Assert.Null(msg1);

        // Досылаем оставшиеся 2 байта
        parser.Append(fullBytes, fullBytes.Length - 2, 2);
        Assert.True(parser.TryReadMessage(out var msgComplete));
        Assert.NotNull(msgComplete);
        Assert.True(PostgreSqlProtocolParser.IsCommitCommand(msgComplete));
    }

    #endregion

    #region 3.2 Parse (P) Tests

    [Fact]
    public void Parse_EmptyStatementNameAndValidCommitQuery_StructurallyValid_NotRecognizedAsCommitExecution()
    {
        var payload = CreateParsePayload("", true, "COMMIT", true, 0, []);
        var framed = CreateFramedMessage((byte)'P', payload);

        var parser = new PostgreSqlProtocolParser();
        parser.Append(framed, 0, framed.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.True(PostgreSqlProtocolParser.IsValidParsePayload(msg.Payload));
        // Parse лишь подготавливает оператор, но не исполняет его: IsCommitCommand обязан вернуть false!
        Assert.False(PostgreSqlProtocolParser.IsCommitCommand(msg));
    }

    [Fact]
    public void Parse_NonEmptyStatementNameAndValidCommitQuery_StructurallyValid_NotRecognizedAsCommitExecution()
    {
        var payload = CreateParsePayload("stmt_commit_1", true, "COMMIT;", true, 0, []);
        var framed = CreateFramedMessage((byte)'P', payload);

        var parser = new PostgreSqlProtocolParser();
        parser.Append(framed, 0, framed.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.True(PostgreSqlProtocolParser.IsValidParsePayload(msg.Payload));
        // Parse лишь подготавливает оператор, но не исполняет его: IsCommitCommand обязан вернуть false!
        Assert.False(PostgreSqlProtocolParser.IsCommitCommand(msg));
    }

    [Fact]
    public void Bind_Message_NeverRecognizedAsCommitCommand()
    {
        var payload = new byte[] { 0, 0, 0, 0 };
        var framed = CreateFramedMessage((byte)'B', payload);

        var parser = new PostgreSqlProtocolParser();
        parser.Append(framed, 0, framed.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.False(PostgreSqlProtocolParser.IsCommitCommand(msg));
    }

    [Fact]
    public void Parse_MissingFirstNul_RejectedAsMalformed()
    {
        var payload = CreateParsePayload("stmt1", false, "COMMIT", true, 0, []);
        var framed = CreateFramedMessage((byte)'P', payload);

        var parser = new PostgreSqlProtocolParser();
        parser.Append(framed, 0, framed.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.False(PostgreSqlProtocolParser.IsValidParsePayload(msg.Payload));
        Assert.False(PostgreSqlProtocolParser.IsCommitCommand(msg));
    }

    [Fact]
    public void Parse_MissingSecondNul_RejectedAsMalformed()
    {
        var payload = CreateParsePayload("", true, "COMMIT", false, 0, []);
        var framed = CreateFramedMessage((byte)'P', payload);

        var parser = new PostgreSqlProtocolParser();
        parser.Append(framed, 0, framed.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.False(PostgreSqlProtocolParser.IsValidParsePayload(msg.Payload));
        Assert.False(PostgreSqlProtocolParser.IsCommitCommand(msg));
    }

    [Fact]
    public void Parse_MissingParameterCount_RejectedAsMalformed()
    {
        var payload = CreateParsePayload("", true, "COMMIT", true, null, null);
        var framed = CreateFramedMessage((byte)'P', payload);

        var parser = new PostgreSqlProtocolParser();
        parser.Append(framed, 0, framed.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.False(PostgreSqlProtocolParser.IsValidParsePayload(msg.Payload));
        Assert.False(PostgreSqlProtocolParser.IsCommitCommand(msg));
    }

    [Fact]
    public void Parse_ParameterCountNegative_RejectedAsMalformed()
    {
        var payload = CreateParsePayload("", true, "COMMIT", true, -1, []);
        var framed = CreateFramedMessage((byte)'P', payload);

        var parser = new PostgreSqlProtocolParser();
        parser.Append(framed, 0, framed.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.False(PostgreSqlProtocolParser.IsValidParsePayload(msg.Payload));
        Assert.False(PostgreSqlProtocolParser.IsCommitCommand(msg));
    }

    [Fact]
    public void Parse_ParameterCountZeroWithNoOids_ValidStructure()
    {
        var payload = CreateParsePayload("", true, "SELECT 1", true, 0, []);
        var framed = CreateFramedMessage((byte)'P', payload);

        var parser = new PostgreSqlProtocolParser();
        parser.Append(framed, 0, framed.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.True(PostgreSqlProtocolParser.IsValidParsePayload(msg.Payload));
        Assert.False(PostgreSqlProtocolParser.IsCommitCommand(msg));
    }

    [Fact]
    public void Parse_ParameterCountOneWithOneOid_ValidStructure()
    {
        var payload = CreateParsePayload("", true, "SELECT $1", true, 1, [23]); // 23 = INT4OID
        var framed = CreateFramedMessage((byte)'P', payload);

        var parser = new PostgreSqlProtocolParser();
        parser.Append(framed, 0, framed.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.True(PostgreSqlProtocolParser.IsValidParsePayload(msg.Payload));
    }

    [Fact]
    public void Parse_ParameterCountOneWithOidMissing_RejectedAsMalformed()
    {
        var payload = CreateParsePayload("", true, "COMMIT", true, 1, []); // ParameterCount=1, но 0 OIDs
        var framed = CreateFramedMessage((byte)'P', payload);

        var parser = new PostgreSqlProtocolParser();
        parser.Append(framed, 0, framed.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.False(PostgreSqlProtocolParser.IsValidParsePayload(msg.Payload));
        Assert.False(PostgreSqlProtocolParser.IsCommitCommand(msg));
    }

    [Fact]
    public void Parse_ParameterCountTwoWithOneOid_RejectedAsMalformed()
    {
        var payload = CreateParsePayload("", true, "COMMIT", true, 2, [23]); // ParameterCount=2, но 1 OID
        var framed = CreateFramedMessage((byte)'P', payload);

        var parser = new PostgreSqlProtocolParser();
        parser.Append(framed, 0, framed.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.False(PostgreSqlProtocolParser.IsValidParsePayload(msg.Payload));
        Assert.False(PostgreSqlProtocolParser.IsCommitCommand(msg));
    }

    [Fact]
    public void Parse_ParameterCountOneWithTwoOids_RejectedAsMalformed()
    {
        var payload = CreateParsePayload("", true, "COMMIT", true, 1, [23, 25]); // ParameterCount=1, но 2 OIDs
        var framed = CreateFramedMessage((byte)'P', payload);

        var parser = new PostgreSqlProtocolParser();
        parser.Append(framed, 0, framed.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.False(PostgreSqlProtocolParser.IsValidParsePayload(msg.Payload));
        Assert.False(PostgreSqlProtocolParser.IsCommitCommand(msg));
    }

    [Fact]
    public void Parse_ExtraBytesAfterOid_RejectedAsMalformed()
    {
        var payload = CreateParsePayload("", true, "COMMIT", true, 1, [23], extraBytes: [0xDE, 0xAD]);
        var framed = CreateFramedMessage((byte)'P', payload);

        var parser = new PostgreSqlProtocolParser();
        parser.Append(framed, 0, framed.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.False(PostgreSqlProtocolParser.IsValidParsePayload(msg.Payload));
        Assert.False(PostgreSqlProtocolParser.IsCommitCommand(msg));
    }

    #endregion

    #region 3.3 CommandComplete (C) Tests

    [Fact]
    public void CommandComplete_ValidWithCommitTag_RecognizedAsCommit()
    {
        var bytes = CreateCommandCompleteMessage("COMMIT");
        var parser = new PostgreSqlProtocolParser();
        parser.Append(bytes, 0, bytes.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.True(PostgreSqlProtocolParser.IsValidCommandCompletePayload(msg.Payload));
        Assert.True(PostgreSqlProtocolParser.IsCommitCommandComplete(msg));
    }

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("INSERT 0 1")]
    [InlineData("UPDATE 5")]
    [InlineData("DELETE 1")]
    [InlineData("ROLLBACK")]
    public void CommandComplete_ValidWithOtherTag_CommitNotRecognized(string tag)
    {
        var bytes = CreateCommandCompleteMessage(tag);
        var parser = new PostgreSqlProtocolParser();
        parser.Append(bytes, 0, bytes.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.True(PostgreSqlProtocolParser.IsValidCommandCompletePayload(msg.Payload));
        Assert.False(PostgreSqlProtocolParser.IsCommitCommandComplete(msg));
    }

    [Fact]
    public void CommandComplete_WithoutTerminatingNul_RejectedAsMalformed()
    {
        var raw = Encoding.ASCII.GetBytes("COMMIT"); // без '\0'
        var framed = CreateFramedMessage((byte)'C', raw);

        var parser = new PostgreSqlProtocolParser();
        parser.Append(framed, 0, framed.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.False(PostgreSqlProtocolParser.IsValidCommandCompletePayload(msg.Payload));
        Assert.False(PostgreSqlProtocolParser.IsCommitCommandComplete(msg));
    }

    [Fact]
    public void CommandComplete_WithNulAndExtraBytes_RejectedAsMalformed()
    {
        var raw = Encoding.ASCII.GetBytes("COMMIT\0EXTRA");
        var framed = CreateFramedMessage((byte)'C', raw);

        var parser = new PostgreSqlProtocolParser();
        parser.Append(framed, 0, framed.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.False(PostgreSqlProtocolParser.IsValidCommandCompletePayload(msg.Payload));
        Assert.False(PostgreSqlProtocolParser.IsCommitCommandComplete(msg));
    }

    [Fact]
    public void CommandComplete_EmptyTagWithNul_StructurallyValid_NotCommit()
    {
        var raw = new byte[] { 0 };
        var framed = CreateFramedMessage((byte)'C', raw);

        var parser = new PostgreSqlProtocolParser();
        parser.Append(framed, 0, framed.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.True(PostgreSqlProtocolParser.IsValidCommandCompletePayload(msg.Payload));
        Assert.False(PostgreSqlProtocolParser.IsCommitCommandComplete(msg));
    }

    [Fact]
    public void CommandComplete_PartialPayload_NotAcceptedUntilFull()
    {
        var fullBytes = CreateCommandCompleteMessage("COMMIT");
        var parser = new PostgreSqlProtocolParser();

        parser.Append(fullBytes, 0, fullBytes.Length - 1);
        Assert.False(parser.TryReadMessage(out var msg1));

        parser.Append(fullBytes, fullBytes.Length - 1, 1);
        Assert.True(parser.TryReadMessage(out var msgFinal));
        Assert.NotNull(msgFinal);
        Assert.True(PostgreSqlProtocolParser.IsCommitCommandComplete(msgFinal));
    }

    #endregion

    #region 3.4 Framing Tests

    [Fact]
    public void Parse_HeaderSplitAcrossChunks_ReturnsMessageWhenComplete()
    {
        var parser = new PostgreSqlProtocolParser();
        var fullBytes = CreateQueryMessage("COMMIT");

        parser.Append(fullBytes, 0, 2);
        Assert.False(parser.TryReadMessage(out var msg1));

        parser.Append(fullBytes, 2, 3);
        Assert.False(parser.TryReadMessage(out var msg2));

        parser.Append(fullBytes, 5, fullBytes.Length - 5);
        Assert.True(parser.TryReadMessage(out var msgFinal));
        Assert.NotNull(msgFinal);
        Assert.Equal((byte)'Q', msgFinal.Type);
        Assert.True(PostgreSqlProtocolParser.IsCommitCommand(msgFinal));
    }

    [Fact]
    public void Parse_PayloadSplitAcrossChunks_ReturnsMessageWhenComplete()
    {
        var parser = new PostgreSqlProtocolParser();
        var fullBytes = CreateCommandCompleteMessage("COMMIT");

        parser.Append(fullBytes, 0, 5);
        Assert.False(parser.TryReadMessage(out var msg1));

        int half = (fullBytes.Length - 5) / 2;
        parser.Append(fullBytes, 5, half);
        Assert.False(parser.TryReadMessage(out var msg2));

        parser.Append(fullBytes, 5 + half, fullBytes.Length - (5 + half));
        Assert.True(parser.TryReadMessage(out var msgFinal));
        Assert.NotNull(msgFinal);
        Assert.Equal((byte)'C', msgFinal.Type);
        Assert.True(PostgreSqlProtocolParser.IsCommitCommandComplete(msgFinal));
    }

    [Fact]
    public void Parse_MultipleMessagesCoalescedInSingleBuffer_ParsesAllSequentially()
    {
        var parser = new PostgreSqlProtocolParser();

        var msg1Bytes = CreateQueryMessage("BEGIN");
        var msg2Bytes = CreateQueryMessage("UPDATE Analyses SET Status = 2");
        var msg3Bytes = CreateQueryMessage("COMMIT");

        var combined = new byte[msg1Bytes.Length + msg2Bytes.Length + msg3Bytes.Length];
        Array.Copy(msg1Bytes, 0, combined, 0, msg1Bytes.Length);
        Array.Copy(msg2Bytes, 0, combined, msg1Bytes.Length, msg2Bytes.Length);
        Array.Copy(msg3Bytes, 0, combined, msg1Bytes.Length + msg2Bytes.Length, msg3Bytes.Length);

        parser.Append(combined, 0, combined.Length);

        Assert.True(parser.TryReadMessage(out var m1));
        Assert.NotNull(m1);
        Assert.Equal((byte)'Q', m1.Type);
        Assert.False(PostgreSqlProtocolParser.IsCommitCommand(m1));

        Assert.True(parser.TryReadMessage(out var m2));
        Assert.NotNull(m2);
        Assert.Equal((byte)'Q', m2.Type);
        Assert.False(PostgreSqlProtocolParser.IsCommitCommand(m2));

        Assert.True(parser.TryReadMessage(out var m3));
        Assert.NotNull(m3);
        Assert.Equal((byte)'Q', m3.Type);
        Assert.True(PostgreSqlProtocolParser.IsCommitCommand(m3));

        Assert.False(parser.TryReadMessage(out var m4));
        Assert.Null(m4);
        Assert.Equal(0, parser.AvailableBytes);
    }

    [Fact]
    public void Parse_Combination_ValidQuery_MalformedQuery_ValidCommandComplete()
    {
        // Проверяем комбинацию [корректный Query] [некорректный Query без NUL] [корректный CommandComplete]
        var parser = new PostgreSqlProtocolParser();

        var validQuery = CreateQueryMessage("BEGIN");

        // Некорректный Query: без завершающего '\0'
        var malformedQueryRaw = Encoding.UTF8.GetBytes("COMMIT");
        var malformedQuery = CreateFramedMessage((byte)'Q', malformedQueryRaw);

        var validComplete = CreateCommandCompleteMessage("COMMIT");

        var combined = new byte[validQuery.Length + malformedQuery.Length + validComplete.Length];
        Array.Copy(validQuery, 0, combined, 0, validQuery.Length);
        Array.Copy(malformedQuery, 0, combined, validQuery.Length, malformedQuery.Length);
        Array.Copy(validComplete, 0, combined, validQuery.Length + malformedQuery.Length, validComplete.Length);

        parser.Append(combined, 0, combined.Length);

        // 1. Корректный BEGIN
        Assert.True(parser.TryReadMessage(out var m1));
        Assert.NotNull(m1);
        Assert.True(PostgreSqlProtocolParser.IsValidQueryPayload(m1.Payload));
        Assert.False(PostgreSqlProtocolParser.IsCommitCommand(m1));

        // 2. Повреждённый Query (без NUL) - фрейминг прочитан, но структурная проверка отвергает
        Assert.True(parser.TryReadMessage(out var m2));
        Assert.NotNull(m2);
        Assert.False(PostgreSqlProtocolParser.IsValidQueryPayload(m2.Payload));
        Assert.False(PostgreSqlProtocolParser.IsCommitCommand(m2)); // Не распознаётся как COMMIT!

        // 3. Корректный CommandComplete('COMMIT')
        Assert.True(parser.TryReadMessage(out var m3));
        Assert.NotNull(m3);
        Assert.True(PostgreSqlProtocolParser.IsValidCommandCompletePayload(m3.Payload));
        Assert.True(PostgreSqlProtocolParser.IsCommitCommandComplete(m3));
    }

    [Fact]
    public void Parse_InvalidLengthSmallerThanHeader_ThrowsException()
    {
        var parser = new PostgreSqlProtocolParser();
        var invalidBytes = new byte[] { (byte)'Q', 0x00, 0x00, 0x00, 0x02 }; // length = 2 < 4
        parser.Append(invalidBytes, 0, invalidBytes.Length);

        Assert.Throws<InvalidDataException>(() => parser.TryReadMessage(out _));
    }

    [Fact]
    public void Parse_LengthExceedsMaximum_ThrowsExceptionWithoutOverflow()
    {
        var parser = new PostgreSqlProtocolParser();
        int excessiveLength = 100 * 1024 * 1024;
        var invalidBytes = new byte[]
        {
            (byte)'Q',
            (byte)(excessiveLength >> 24),
            (byte)(excessiveLength >> 16),
            (byte)(excessiveLength >> 8),
            (byte)excessiveLength
        };
        parser.Append(invalidBytes, 0, invalidBytes.Length);

        var ex = Assert.Throws<InvalidDataException>(() => parser.TryReadMessage(out _));
        Assert.Contains("exceeds maximum allowed size", ex.Message);
    }

    [Fact]
    public void Parse_Int32MaxValueLength_ThrowsExceptionWithoutIntegerOverflow()
    {
        var parser = new PostgreSqlProtocolParser();
        // int.MaxValue = 0x7FFFFFFF: при вычислении 1 + len в int произошло бы переполнение в отрицательное число
        var invalidBytes = new byte[]
        {
            (byte)'Q',
            0x7F, 0xFF, 0xFF, 0xFF
        };
        parser.Append(invalidBytes, 0, invalidBytes.Length);

        var ex = Assert.Throws<InvalidDataException>(() => parser.TryReadMessage(out _));
        Assert.Contains("exceeds maximum allowed size", ex.Message);
    }

    [Fact]
    public void Parse_UnsupportedMessageType_ReturnsMessageWithoutThrowing()
    {
        var parser = new PostgreSqlProtocolParser();
        var terminateBytes = new byte[] { (byte)'X', 0x00, 0x00, 0x00, 0x04 };
        parser.Append(terminateBytes, 0, terminateBytes.Length);

        Assert.True(parser.TryReadMessage(out var msg));
        Assert.NotNull(msg);
        Assert.Equal((byte)'X', msg.Type);
        Assert.Empty(msg.Payload);
        Assert.False(PostgreSqlProtocolParser.IsCommitCommand(msg));
        Assert.False(PostgreSqlProtocolParser.IsCommitCommandComplete(msg));
    }

    #endregion
}
