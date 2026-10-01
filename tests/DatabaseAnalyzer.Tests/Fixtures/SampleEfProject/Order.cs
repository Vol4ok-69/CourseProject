namespace DatabaseAnalyzer.Tests.Fixtures.SampleEfProject;

public sealed class Order
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public User User { get; set; } = null!;
}