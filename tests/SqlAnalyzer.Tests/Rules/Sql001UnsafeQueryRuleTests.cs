using Microsoft.CodeAnalysis.CSharp;
using SqlAnalyzer.Rules;
using Xunit;

namespace SqlAnalyzer.Tests.Rules;

public sealed class Sql001UnsafeQueryRuleTests
{
    private readonly Sql001UnsafeQueryRule _rule = new();

    [Fact]
    public void FromSqlRaw_WithDirectInterpolatedString_ReturnsWarning()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db, string name)
            {
                var users = db.Users.FromSqlRaw($"SELECT * FROM Users WHERE Name = '{name}'").ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Single(findings);
        Assert.Equal("SQL001", findings[0].Rule);
        Assert.Equal(5, findings[0].LineNumber);
        Assert.Contains("FromSqlRaw", findings[0].Message);
    }

    [Fact]
    public void FromSqlRaw_WithBinaryConcatenation_ReturnsWarning()
    {
        var source = """
        public class OrderRepository
        {
            public void GetOrders(AppDbContext db, int status)
            {
                var query = "SELECT * FROM Orders WHERE Status = " + status;
                var orders = db.Orders.FromSqlRaw(query).ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "OrderRepository.cs");

        Assert.Single(findings);
        Assert.Equal("SQL001", findings[0].Rule);
        Assert.Equal(6, findings[0].LineNumber);
        Assert.Contains("FromSqlRaw", findings[0].Message);
    }

    [Fact]
    public void ExecuteSqlRaw_WithDirectInterpolatedString_ReturnsWarning()
    {
        var source = """
        public class UserRepository
        {
            public void UpdateUser(AppDbContext db, int roleId)
            {
                db.Database.ExecuteSqlRaw($"UPDATE Users SET Active = 1 WHERE RoleId = {roleId}");
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Single(findings);
        Assert.Equal("SQL001", findings[0].Rule);
        Assert.Equal(5, findings[0].LineNumber);
        Assert.Contains("ExecuteSqlRaw", findings[0].Message);
    }

    [Fact]
    public void ExecuteSqlRawAsync_WithDirectInterpolatedString_ReturnsWarning()
    {
        var source = """
        public class UserRepository
        {
            public async Task UpdateUserAsync(AppDbContext db, int roleId)
            {
                await db.Database.ExecuteSqlRawAsync($"UPDATE Users SET Active = 1 WHERE RoleId = {roleId}");
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Single(findings);
        Assert.Equal("SQL001", findings[0].Rule);
        Assert.Equal(5, findings[0].LineNumber);
        Assert.Contains("ExecuteSqlRawAsync", findings[0].Message);
    }

    [Fact]
    public void SqlQueryRaw_WithDirectInterpolatedString_ReturnsWarning()
    {
        var source = """
        public class UserRepository
        {
            public void GetIds(AppDbContext db, string role)
            {
                var ids = db.Database.SqlQueryRaw<int>($"SELECT Id FROM Users WHERE Role = '{role}'").ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Single(findings);
        Assert.Equal("SQL001", findings[0].Rule);
        Assert.Equal(5, findings[0].LineNumber);
        Assert.Contains("SqlQueryRaw", findings[0].Message);
    }

    [Fact]
    public void FromSqlRaw_WithStringFormatAndDynamicParameter_ReturnsWarning()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db, string name)
            {
                var query = string.Format("SELECT * FROM Users WHERE Name = '{0}'", name);
                var users = db.Users.FromSqlRaw(query).ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Single(findings);
        Assert.Equal("SQL001", findings[0].Rule);
        Assert.Equal(6, findings[0].LineNumber);
        Assert.Contains("FromSqlRaw", findings[0].Message);
    }

    [Fact]
    public void FromSqlRaw_WithStringConcat_FirstArgumentDynamic_ReturnsWarning()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db, string dynamicTable)
            {
                var query = string.Concat(dynamicTable, " WHERE 1=1");
                var users = db.Users.FromSqlRaw(query).ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Single(findings);
        Assert.Equal("SQL001", findings[0].Rule);
        Assert.Equal(6, findings[0].LineNumber);
        Assert.Contains("FromSqlRaw", findings[0].Message);
    }

    [Fact]
    public void FromSqlRaw_WithStringJoin_DynamicArgument_ReturnsWarning()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db, string[] filters)
            {
                var query = string.Join(" AND ", filters);
                var users = db.Users.FromSqlRaw(query).ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Single(findings);
        Assert.Equal("SQL001", findings[0].Rule);
        Assert.Equal(6, findings[0].LineNumber);
    }

    [Fact]
    public void FromSqlRaw_WithLocalVariable_ReassignedToDynamicValue_ReturnsWarning()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db, string name)
            {
                string sql = "SELECT * FROM Users";
                sql = $"SELECT * FROM Users WHERE Name = '{name}'";
                var users = db.Users.FromSqlRaw(sql).ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Single(findings);
        Assert.Equal("SQL001", findings[0].Rule);
        Assert.Equal(7, findings[0].LineNumber);
    }

    [Fact]
    public void FromSqlRaw_WithLocalVariable_DynamicAddAssignment_ReturnsWarning()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db, string name, bool filter)
            {
                string sql = "SELECT * FROM Users WHERE 1=1";
                if (filter)
                {
                    sql += $" AND Name = '{name}'";
                }
                var users = db.Users.FromSqlRaw(sql).ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Single(findings);
        Assert.Equal("SQL001", findings[0].Rule);
        Assert.Equal(10, findings[0].LineNumber);
    }

    [Fact]
    public void FromSqlRaw_WithScopedLocalVariables_DoesNotConfuseScopes()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db, string name, bool condition)
            {
                if (condition)
                {
                    var sql = $"SELECT * FROM Users WHERE Name = '{name}'";
                }

                var query = "SELECT * FROM Users";
                var users = db.Users.FromSqlRaw(query).ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void FromSqlRaw_WithClassLevelConstString_DoesNotReturnWarning()
    {
        var source = """
        public class UserRepository
        {
            private const string BaseQuery = "SELECT * FROM Users WHERE IsActive = TRUE";

            public void GetUsers(AppDbContext db)
            {
                var users = db.Users.FromSqlRaw(BaseQuery).ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void FromSqlInterpolated_WithInterpolatedString_DoesNotReturnWarning()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db, string name)
            {
                var users = db.Users.FromSqlInterpolated($"SELECT * FROM Users WHERE Name = {name}").ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void ExecuteSqlInterpolatedAsync_WithInterpolatedString_DoesNotReturnWarning()
    {
        var source = """
        public class UserRepository
        {
            public async Task UpdateUserAsync(AppDbContext db, int roleId)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Users SET Active = 1 WHERE RoleId = {roleId}");
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void FromSqlRaw_WithLiteralString_DoesNotReturnWarning()
    {
        var source = """
        public class UserRepository
        {
            public void GetActiveUsers(AppDbContext db)
            {
                var allActive = db.Users.FromSqlRaw("SELECT * FROM Users WHERE IsActive = TRUE").ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void FromSqlRaw_WithConstantConcatenation_DoesNotReturnWarning()
    {
        var source = """
        public class UserRepository
        {
            public void GetActiveUsers(AppDbContext db)
            {
                var allActive = db.Users.FromSqlRaw("SELECT * FROM Users " + "WHERE IsActive = TRUE").ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void FromSqlRaw_WithConstVariable_DoesNotReturnWarning()
    {
        var source = """
        public class UserRepository
        {
            public void GetActiveUsers(AppDbContext db)
            {
                const string query = "SELECT * FROM Users WHERE IsActive = TRUE";
                var allActive = db.Users.FromSqlRaw(query).ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void FromSqlRaw_WithExplicitParameters_DoesNotReturnWarning()
    {
        var source = """
        public class UserRepository
        {
            public void GetUsers(AppDbContext db, string name)
            {
                var param = new NpgsqlParameter("@name", name);
                var users = db.Users.FromSqlRaw("SELECT * FROM Users WHERE Name = @name", param).ToList();
            }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "UserRepository.cs");

        Assert.Empty(findings);
    }

    [Fact]
    public void UnrelatedMethod_WithSameNameOrOther_DoesNotReturnWarning()
    {
        var source = """
        public class CustomHelper
        {
            public void ProcessData(string name)
            {
                CustomProcess($"Data: {name}");
            }

            private void CustomProcess(string data) { }
        }
        """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var findings = _rule.Analyze(root, "CustomHelper.cs");

        Assert.Empty(findings);
    }
}
