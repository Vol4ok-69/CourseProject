using Application.DTOs;
using Application.Exceptions;
using Application.Interfaces.Repositories;
using Domain.Entities;

namespace Application.Services;

public class UserService(IUserRepository userRepository, IRoleRepository roleRepository)
{
    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await userRepository.GetAllAsync(cancellationToken);
    }

    public async Task<User?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await userRepository.GetByIdAsync(id, cancellationToken);
    }

    public async Task<User?> UpdateAsync(int id, UpdateUserDto dto, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.GetByIdAsync(id, cancellationToken);

        if (user is null)
        {
            return null;
        }

        var existingUser = await userRepository.GetByUsernameAsync(dto.Username, cancellationToken);

        if (existingUser is not null && existingUser.Id != user.Id)
        {
            throw new InvalidOperationException("Пользователь с таким именем уже существует.");
        }

        user.Username = dto.Username;

        await userRepository.UpdateAsync(user, cancellationToken);

        return await userRepository.GetByIdAsync(user.Id, cancellationToken);
    }

    public async Task<User?> UpdateRoleAsync(int id, UpdateUserRoleDto dto, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.GetByIdAsync(id, cancellationToken);

        if (user is null)
        {
            return null;
        }

        var newRole = await roleRepository.GetByIdAsync(dto.RoleId, cancellationToken)
            ?? throw new InvalidOperationException("Роль не найдена.");

        if (user.RoleId == newRole.Id)
        {
            return user;
        }

        var isCurrentAdmin = string.Equals(user.Role?.Name, "Admin", StringComparison.OrdinalIgnoreCase);
        var isNewAdmin = string.Equals(newRole.Name, "Admin", StringComparison.OrdinalIgnoreCase);

        // Если пользователь не является администратором или роль повышается до Admin — защита не требуется
        if (!isCurrentAdmin || isNewAdmin)
        {
            user.RoleId = newRole.Id;
            await userRepository.UpdateAsync(user, cancellationToken);
            return await userRepository.GetByIdAsync(user.Id, cancellationToken);
        }

        // Понижение роли администратора: требуется транзакционная защита инварианта последнего администратора
        await using var transaction = await userRepository.BeginAdminProtectionTransactionAsync(cancellationToken);

        var currentUser = await userRepository.GetByIdAsync(id, cancellationToken);
        if (currentUser is null)
        {
            return null;
        }

        if (!string.Equals(currentUser.Role?.Name, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            currentUser.RoleId = newRole.Id;
            await userRepository.UpdateAsync(currentUser, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await userRepository.GetByIdAsync(currentUser.Id, cancellationToken);
        }

        var adminCount = await userRepository.CountAdminsAsync(cancellationToken);
        if (adminCount <= 1)
        {
            throw new LastAdminProtectionException("Невозможно удалить или понизить роль последнего администратора системы.");
        }

        currentUser.RoleId = newRole.Id;
        await userRepository.UpdateAsync(currentUser, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await userRepository.GetByIdAsync(currentUser.Id, cancellationToken);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.GetByIdAsync(id, cancellationToken);

        if (user is null)
        {
            return false;
        }

        var isAdmin = string.Equals(user.Role?.Name, "Admin", StringComparison.OrdinalIgnoreCase);

        if (!isAdmin)
        {
            if (await userRepository.HasProjectsAsync(id, cancellationToken))
            {
                throw new InvalidOperationException("Нельзя удалить пользователя, у которого есть проекты.");
            }

            await userRepository.DeleteAsync(user, cancellationToken);
            return true;
        }

        // Удаление администратора: требуется транзакционная защита инварианта последнего администратора
        await using var transaction = await userRepository.BeginAdminProtectionTransactionAsync(cancellationToken);

        var currentUser = await userRepository.GetByIdAsync(id, cancellationToken);
        if (currentUser is null)
        {
            return false;
        }

        var isStillAdmin = string.Equals(currentUser.Role?.Name, "Admin", StringComparison.OrdinalIgnoreCase);
        if (isStillAdmin)
        {
            var adminCount = await userRepository.CountAdminsAsync(cancellationToken);
            if (adminCount <= 1)
            {
                throw new LastAdminProtectionException("Невозможно удалить или понизить роль последнего администратора системы.");
            }
        }

        if (await userRepository.HasProjectsAsync(id, cancellationToken))
        {
            throw new InvalidOperationException("Нельзя удалить пользователя, у которого есть проекты.");
        }

        await userRepository.DeleteAsync(currentUser, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return true;
    }
}
