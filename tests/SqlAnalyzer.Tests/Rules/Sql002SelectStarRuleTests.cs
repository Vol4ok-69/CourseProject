using Microsoft.CodeAnalysis.CSharp;
using SqlAnalyzer.Rules;
using Xunit;

namespace SqlAnalyzer.Tests.Rules;

public sealed class Sql002SelectStarRuleTests
{
    private readonly Sql002SelectStarRule _rule = new();

    [Fact]
    public void FromSqlRaw_WithSelectStar_ReturnsInfo()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db)
            {
                var users = db.Users.FromSqlRaw("SELECT * FROM Users").ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Single(findings);
        Assert.Equal("SQL002", findings[0].Rule);
        Assert.Equal(5, findings[0].LineNumber);
    }

    [Fact]
    public void FromSqlRaw_WithSelectStarCaseInsensitive_ReturnsInfo()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db)
            {
                var users = db.Users.FromSqlRaw("select * from Users").ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Single(findings);
        Assert.Equal("SQL002", findings[0].Rule);
    }

    [Fact]
    public void FromSqlRaw_WithSelectAliasStar_ReturnsInfo()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db)
            {
                var users = db.Users.FromSqlRaw("SELECT u.* FROM Users u").ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Single(findings);
        Assert.Equal("SQL002", findings[0].Rule);
    }

    [Fact]
    public void FromSqlRaw_WithSelectQuotedAliasStar_ReturnsInfo()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db)
            {
                var users = db.Users.FromSqlRaw("SELECT \"u\".* FROM Users u").ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Single(findings);
        Assert.Equal("SQL002", findings[0].Rule);
    }

    [Fact]
    public void FromSqlInterpolated_WithSelectStar_ReturnsInfo()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db, int minAge)
            {
                var users = db.Users.FromSqlInterpolated($"SELECT * FROM Users WHERE Age > {minAge}").ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Single(findings);
        Assert.Equal("SQL002", findings[0].Rule);
    }

    [Fact]
    public void SqlQueryRaw_WithSelectStar_ReturnsInfo()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db)
            {
                var users = db.Database.SqlQueryRaw<UserDto>("SELECT * FROM Users").ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Single(findings);
        Assert.Equal("SQL002", findings[0].Rule);
    }

    [Fact]
    public void SqlQuery_WithSelectStar_ReturnsInfo()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db)
            {
                var users = db.Database.SqlQuery<UserDto>($"SELECT * FROM Users").ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Single(findings);
        Assert.Equal("SQL002", findings[0].Rule);
    }

    [Fact]
    public void FromSqlRaw_WithSubqueryInColumns_HavingSelectStar_ReturnsInfo()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db)
            {
                var users = db.Users.FromSqlRaw("SELECT u.Id, (SELECT * FROM Details d WHERE d.UserId = u.Id) FROM Users u").ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Single(findings);
        Assert.Equal("SQL002", findings[0].Rule);
    }

    [Fact]
    public void FromSqlRaw_WithSubqueryInFrom_HavingSelectStar_ReturnsInfo()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db)
            {
                var users = db.Users.FromSqlRaw("SELECT Id FROM (SELECT * FROM Users) AS u").ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Single(findings);
        Assert.Equal("SQL002", findings[0].Rule);
    }

    [Fact]
    public void FromSqlRaw_WithSubqueryHavingExplicitColumns_DoesNotReturnInfo()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db)
            {
                var users = db.Users.FromSqlRaw("SELECT u.Id, (SELECT d.Amount FROM Details d WHERE d.UserId = u.Id) FROM Users u").ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void FromSqlRaw_WithStringLiteralContainingSelectStarInWhereClause_DoesNotReturnInfo()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db)
            {
                var users = db.Users.FromSqlRaw("SELECT Id, Name FROM Users WHERE Note = 'SELECT * FROM Test'").ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void FromSqlRaw_WithStringLiteralContainingAsteriskBanner_DoesNotReturnInfo()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db)
            {
                var users = db.Users.FromSqlRaw("SELECT '***' AS Banner, Id, Name FROM Users").ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void FromSqlRaw_WithClassLevelConstQuery_ReturnsInfo()
    {
        var source = """
        public class UserRepository
        {
            private const string Query = "SELECT * FROM Users";

            public void GetUsers(AppDbContext db)
            {
                var users = db.Users.FromSqlRaw(Query).ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Single(findings);
        Assert.Equal("SQL002", findings[0].Rule);
    }

    [Fact]
    public void FromSqlRaw_WithReassignedLocalVariableConcatenation_ReturnsInfo()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db)
            {
                string sql = "SELECT * ";
                sql += "FROM Users";
                var users = db.Users.FromSqlRaw(sql).ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Single(findings);
        Assert.Equal("SQL002", findings[0].Rule);
    }

    [Fact]
    public void FromSqlRaw_WithExplicitColumns_DoesNotReturnInfo()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db)
            {
                var users = db.Users.FromSqlRaw("SELECT Id, Username, Email FROM Users").ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void FromSqlRaw_WithCountStar_DoesNotReturnInfo()
    {
        var source = """
        public class UserRepository
        {
            public void GetCount(AppDbContext db)
            {
                var count = db.Database.SqlQueryRaw<int>("SELECT COUNT(*) FROM Users").ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void FromSqlRaw_WithCountDistinctStar_DoesNotReturnInfo()
    {
        var source = """
        public class UserRepository
        {
            public void GetCount(AppDbContext db)
            {
                var count = db.Database.SqlQueryRaw<int>("SELECT COUNT(DISTINCT *) FROM Users").ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void FromSqlRaw_WithMultiplication_DoesNotReturnInfo()
    {
        var source = """
        public class OrderRepository
        {
            public void GetTotal(AppDbContext db)
            {
                var totals = db.Database.SqlQueryRaw<decimal>("SELECT Price * Quantity AS Total FROM OrderItems").ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "OrderRepository.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void FromSqlRaw_WithComments_DoesNotReturnInfo()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db)
            {
                var sql = "/* Select all users */ -- with explicit fields\nSELECT Id, Name FROM Users";
                var users = db.Users.FromSqlRaw(sql).ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Empty(findings);
    }
}
