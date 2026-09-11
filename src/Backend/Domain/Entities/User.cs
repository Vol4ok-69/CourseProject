namespace Domain.Entities;

public class User
{
    public int Id { get; set; }

    public string Username { get; set; } = null!;

    public string PasswordHash { get; set; } = null!;

    public int RoleId { get; set; }

    public Role Role { get; set; } = null!;

    public ICollection<Project> Projects { get; set; } = new List<Project>();
}