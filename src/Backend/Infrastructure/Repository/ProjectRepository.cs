using Application.Interfaces.Repositories;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repository;

public class ProjectRepository(AppDbContext context) : IProjectRepository
{
    public async Task<Project?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await context.Projects
            .Include(project => project.Owner)
            .FirstOrDefaultAsync(project => project.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Project>> GetByOwnerIdAsync(int ownerId, CancellationToken cancellationToken = default)
    {
        return await context.Projects
            .AsNoTracking()
            .Where(project => project.OwnerId == ownerId)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Project project, CancellationToken cancellationToken = default)
    {
        try
        {
            await context.Projects.AddAsync(project, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception, "IX_Projects_OwnerId_Name"))
        {
            throw new InvalidOperationException("Проект с таким именем уже существует.", exception);
        }
    }

    public async Task UpdateAsync(Project project, CancellationToken cancellationToken = default)
    {
        try
        {
            context.Projects.Update(project);
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception, "IX_Projects_OwnerId_Name"))
        {
            throw new InvalidOperationException("Проект с таким именем уже существует.", exception);
        }
    }

    public async Task DeleteAsync(Project project, CancellationToken cancellationToken = default)
    {
        context.Projects.Remove(project);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception, string constraintName)
    {
        return exception.InnerException is Npgsql.PostgresException postgresException &&
               postgresException.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation &&
               (postgresException.ConstraintName == constraintName ||
                postgresException.ConstraintName?.Contains(constraintName, StringComparison.OrdinalIgnoreCase) == true);
    }
}
