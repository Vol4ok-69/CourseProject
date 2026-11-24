namespace Application.DTOs;

/// <summary>
/// Результат атомарного сохранения синхронизированных коммитов в БД.
/// </summary>
/// <param name="InsertedHashes">Множество фактически добавленных новых коммитов.</param>
/// <param name="UpdatedHashes">Множество обновленных существующих (ранее неподтвержденных) коммитов.</param>
/// <param name="UnchangedHashes">Множество коммитов, оставленных без изменений (уже подтвержденных).</param>
public record SyncBatchResult(
    IReadOnlySet<string> InsertedHashes,
    IReadOnlySet<string> UpdatedHashes,
    IReadOnlySet<string> UnchangedHashes);
