namespace Application.DTOs;

/// <summary>
/// Данные для обновления родительских связей и подтверждения существующего коммита.
/// </summary>
/// <param name="CommitHash">Хэш обновляемого коммита.</param>
/// <param name="ParentHashes">Полный список родительских хэшей коммита.</param>
public record CommitUpdateDto(string CommitHash, IReadOnlyList<string> ParentHashes);
