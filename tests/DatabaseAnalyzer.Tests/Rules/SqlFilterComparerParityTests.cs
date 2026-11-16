using DatabaseAnalyzer.Rules;

namespace DatabaseAnalyzer.Tests.Rules;

public sealed class SqlFilterComparerParityTests
{
    [Theory]
    [InlineData("IS NULL", "= NULL")]
    [InlineData("IsDeleted IS NULL", "IsDeleted = NULL")]
    [InlineData("Status IS NOT NULL", "Status != NULL")]
    [InlineData("Status IS NOT NULL", "Status <> NULL")]
    public void SqlNull_Comparison_IsNotEquivalent(string filter1, string filter2)
    {
        var equivalent = SqlFilterComparer.AreFiltersEquivalent(filter1, filter2, out var isAmbiguous);

        Assert.False(equivalent);
        Assert.False(isAmbiguous);
    }

    [Fact]
    public void QuotedIdentifiers_PostgreSql_AreCaseSensitive()
    {
        var filter1 = "\"Status\" = 1";
        var filter2 = "\"status\" = 1";

        var equivalent = SqlFilterComparer.AreFiltersEquivalent(filter1, filter2, out var isAmbiguous);

        Assert.False(equivalent);
        Assert.False(isAmbiguous);
    }

    [Fact]
    public void UnquotedIdentifiers_AreCaseInsensitive()
    {
        var filter1 = "IsDeleted = 0";
        var filter2 = "isdeleted = 0";

        var equivalent = SqlFilterComparer.AreFiltersEquivalent(filter1, filter2, out var isAmbiguous);

        Assert.True(equivalent);
        Assert.False(isAmbiguous);
    }

    [Fact]
    public void QuoteDialects_AreNotEquivalent()
    {
        var pg = "\"Status\" = 1";
        var tsql = "[Status] = 1";
        var mysql = "`Status` = 1";

        Assert.False(SqlFilterComparer.AreFiltersEquivalent(pg, tsql, out _));
        Assert.False(SqlFilterComparer.AreFiltersEquivalent(pg, mysql, out _));
        Assert.False(SqlFilterComparer.AreFiltersEquivalent(tsql, mysql, out _));
    }

    [Theory]
    [InlineData("Status = 'active '", "Status = 'active'")]
    [InlineData("Status = 'Active'", "Status = 'active'")]
    public void StringLiterals_PreserveExactWhitespaceAndCase(string filter1, string filter2)
    {
        var equivalent = SqlFilterComparer.AreFiltersEquivalent(filter1, filter2, out var isAmbiguous);

        Assert.False(equivalent);
        Assert.False(isAmbiguous);
    }

    [Fact]
    public void EscapedQuotes_InStringLiterals_HandledCorrectly()
    {
        var filter1 = "Name = 'O''Reilly'";
        var filter2 = "(Name = 'O''Reilly')";

        var equivalent = SqlFilterComparer.AreFiltersEquivalent(filter1, filter2, out var isAmbiguous);

        Assert.True(equivalent);
        Assert.False(isAmbiguous);
    }

    [Theory]
    [InlineData("IsDeleted = 0", "(IsDeleted = 0)")]
    [InlineData("((IsDeleted = 0))", "IsDeleted = 0")]
    [InlineData("(((IsDeleted = 0)))", "((IsDeleted = 0))")]
    [InlineData("(A = 1) AND (B = 2)", "((A = 1) AND (B = 2))")]
    public void OuterParentheses_AreStrippedSafely(string filter1, string filter2)
    {
        var equivalent = SqlFilterComparer.AreFiltersEquivalent(filter1, filter2, out var isAmbiguous);

        Assert.True(equivalent);
        Assert.False(isAmbiguous);
    }

    [Fact]
    public void DisconnectedOuterParentheses_AreNotStripped()
    {
        var expr = "(A = 1) AND (B = 2)";
        var stripped = SqlFilterComparer.StripOuterParentheses(expr);

        Assert.Equal("(A = 1) AND (B = 2)", stripped);
    }

    [Theory]
    [InlineData("Name = 'unclosed literal")]
    [InlineData("Status = \"unclosed identifier")]
    public void UnclosedQuotes_AreMarkedAmbiguous(string invalidFilter)
    {
        var equivalent = SqlFilterComparer.AreFiltersEquivalent(invalidFilter, "Status = 1", out var isAmbiguous);

        Assert.False(equivalent);
        Assert.True(isAmbiguous);
    }

    [Fact]
    public void NullAndWhitespaceFilters_HandledCorrectly()
    {
        Assert.True(SqlFilterComparer.AreFiltersEquivalent(null, null, out _));
        Assert.True(SqlFilterComparer.AreFiltersEquivalent("", "  ", out _));
        Assert.False(SqlFilterComparer.AreFiltersEquivalent("IsDeleted = 0", null, out _));
        Assert.False(SqlFilterComparer.AreFiltersEquivalent(null, "IsDeleted = 0", out _));
    }
}
