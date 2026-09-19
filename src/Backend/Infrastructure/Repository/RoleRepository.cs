using Application.Interfaces.Repositories;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repository;

public class RoleRepository(AppDbContext context) : IRoleRepository
{
    public async Task<Role?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await context.Roles
            .AsNoTracking()
            .FirstOrDefaultAsync(role => role.Id == id, cancellationToken);
    }
}