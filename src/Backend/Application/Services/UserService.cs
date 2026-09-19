using Application.DTOs;
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

        var role = await roleRepository.GetByIdAsync(dto.RoleId, cancellationToken) ?? throw new InvalidOperationException("Роль не найдена.");

        user.RoleId = role.Id;

        await userRepository.UpdateAsync(user, cancellationToken);

        return await userRepository.GetByIdAsync(user.Id, cancellationToken);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.GetByIdAsync(id, cancellationToken);

        if (user is null)
        {
            return false;
        }

        if (await userRepository.HasProjectsAsync(id, cancellationToken))
        {
            throw new InvalidOperationException("Нельзя удалить пользователя, у которого есть проекты.");
        }

        await userRepository.DeleteAsync(user, cancellationToken);

        return true;
    }
}