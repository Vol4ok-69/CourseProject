using Application.DTOs;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Domain.Entities;

namespace Application.Services;

public class AuthService(IUserRepository userRepository, IPasswordHasher passwordHasher, ITokenService tokenService)
{
    public async Task<AuthResponseDto> RegisterAsync(RegisterDto dto, CancellationToken cancellationToken = default)
    {
        var existingUser = await userRepository.GetByUsernameAsync(dto.Username, cancellationToken);

        if (existingUser is not null)
        {
            throw new InvalidOperationException("Пользователь с таким именем уже существует.");
        }

        var user = new User
        {
            Username = dto.Username,
            PasswordHash = passwordHasher.Hash(dto.Password),
            RoleId = 2
        };

        await userRepository.AddAsync(user, cancellationToken);

        var createdUser = await userRepository.GetByIdAsync(user.Id, cancellationToken)
            ?? throw new InvalidOperationException("Не удалось получить созданного пользователя.");

        return new AuthResponseDto
        {
            AccessToken = tokenService.GenerateToken(createdUser)
        };
    }

    public async Task<AuthResponseDto?> LoginAsync(LoginDto dto, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.GetByUsernameAsync(dto.Username, cancellationToken);

        if (user is null || !passwordHasher.Verify(dto.Password, user.PasswordHash))
        {
            return null;
        }

        return new AuthResponseDto
        {
            AccessToken = tokenService.GenerateToken(user)
        };
    }
}