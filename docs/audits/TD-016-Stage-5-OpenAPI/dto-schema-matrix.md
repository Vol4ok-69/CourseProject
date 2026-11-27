# DTO & Enum Schema Matrix

## Public DTOs (19 Checked)

| DTO | Role | Swagger Schema Parity | Notes / Findings |
|---|---|---|---|
| `RegisterDto` | Request | Match | `username` (`3..50`), `password` (`min 6`), both `required`. |
| `LoginDto` | Request | Match | `username`, `password`, both `required`. |
| `ChangeUsernameDto` | Request | Match | `username` (`3..50`), `required`. |
| `ChangePasswordDto` | Request | Match | `currentPassword`, `newPassword` (`min 6`), both `required`. |
| `GitHubTokenRequestDto` | Request | Match | `token`, `required`. |
| `CreateProjectDto` | Request | Match | `name` (`3..50`), `repoUrl` (regex), both `required`. |
| `UpdateProjectDto` | Request | Match | `name` (`3..50`), `repoUrl` (regex), both `required`. |
| `CreateCommitDto` | Request | **Drift** (`TD016-S5-002`, `S5-008`) | `swagger.json` has `maxLength: 40`, omits `pattern`, omits `IValidatableObject` timezone rule on `commitDate`. |
| `UpdateUserDto` | Request | Match | `username` (`3..50`), `required`. |
| `UpdateUserRoleDto` | Request | Minor (`TD016-S5-011`) | `roleId` has `minimum: 1`, omitted from `required`. |
| `AuthResponseDto` | Response | Nullability (`TD016-S5-007`) | `accessToken` emitted as `nullable: true`, not `required`. |
| `UserDto` | Response | Nullability (`TD016-S5-007`) | `username`, `roleName` emitted as `nullable: true`. |
| `ProjectDto` | Response | Nullability (`TD016-S5-007`) | `name`, `repoUrl` emitted as `nullable: true`. |
| `CommitDto` | Response | Nullability (`TD016-S5-007`) | `commitHash` emitted as `nullable: true`. |
| `AnalysisDto` | Response | Nullability (`TD016-S5-007`) | `results` emitted as `nullable: true`. |
| `AnalysisResultDto` | Response | Nullability (`TD016-S5-007`) | `filePath`, `rule`, `message` emitted as `nullable: true`. |
| `PagedResultDto<T>` | Response | Nullability (`TD016-S5-007`) | Flattened to `UserDtoPagedResultDto`, `CommitDtoPagedResultDto`, `AnalysisDtoPagedResultDto`; `items` `nullable: true`. |
| `GitHubIntegrationStatusDto` | Response | Match | `isConnected`, `gitHubUsername?`, `connectedAt?`. |
| `GitHubSyncResultDto` | Response | **Drift** (`TD016-S5-001`) | `swagger.json` omits `alreadyCurrent` and `historyClassification`. |

## Enums (3 Checked in `swagger.json`, +1 Missing `HistoryClassification`)
- `AnalysisStatus`, `Severity`, `AnalyzerType`: serialized as PascalCase strings via `JsonStringEnumConverter` (`Program.cs:232`) and accurately emitted as `type: "string", enum: [...]` in `swagger.json`.
- `HistoryClassification`: missing from `swagger.json` due to `GitHubSyncResultDto` staleness (`TD016-S5-001`).
