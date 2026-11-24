namespace Application.DTOs;

/// <summary>
/// Новые параметры контрольной точки синхронизации проекта.
/// </summary>
public record ProjectCheckpointDto(
    string LastSyncedCommitHash,
    string LastSyncedBranch,
    string LastSyncedRepositoryId,
    string LastSyncedRepositoryHost,
    DateTime LastSyncedAt);

/// <summary>
/// Снимок контрольной точки на момент начала синхронизации для оптимистической проверки.
/// </summary>
public record ProjectCheckpointSnapshotDto(
    string? LastSyncedCommitHash,
    string? LastSyncedBranch,
    string? LastSyncedRepositoryId,
    string? LastSyncedRepositoryHost);
