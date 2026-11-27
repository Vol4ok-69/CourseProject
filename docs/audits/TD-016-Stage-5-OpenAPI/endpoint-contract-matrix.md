# Endpoint Contract Matrix (27 Operations)

| # | Method | Path | Controller / Handler | Swagger Present | OperationId | Auth (Code vs Swagger) | Request / Query / Response Parity | Statuses Parity |
|---|---|---|---|---|---|---|---|---|
| 1 | `POST` | `/api/auth/register` | `AuthController.Register` | Yes | Missing | Anonymous vs **Global Bearer** (`TD016-S5-005`) | `RegisterDto` -> `AuthResponseDto` (Match) | `201,400,409,429` (`429` lacks `Retry-After`) |
| 2 | `POST` | `/api/auth/login` | `AuthController.Login` | Yes | Missing | Anonymous vs **Global Bearer** (`TD016-S5-005`) | `LoginDto` -> `AuthResponseDto` (Match) | `200,400,401,429` (`429` lacks `Retry-After`) |
| 3 | `GET` | `/api/auth/me` | `AuthController.GetCurrentUser` | Yes | Missing | `[Authorize]` / Bearer (Match) | `UserDto` (Match) | `200,401,404` |
| 4 | `PUT` | `/api/auth/me/username` | `AuthController.ChangeUsername` | Yes | Missing | `[Authorize]` / Bearer (Match) | `ChangeUsernameDto` -> `AuthResponseDto` (Match) | `200,400,401,404,409` |
| 5 | `PUT` | `/api/auth/me/password` | `AuthController.ChangePassword` | Yes | Missing | `[Authorize]` / Bearer (Match) | `ChangePasswordDto` -> `AuthResponseDto` (Match) | `200,400,401,404,429` (`429` lacks `Retry-After`) |
| 6 | `GET` | `/api/auth/me/github` | `AuthController.GetGitHubStatus` | Yes | Missing | `[Authorize]` / Bearer (Match) | `GitHubIntegrationStatusDto` (Match) | `200,401,404` |
| 7 | `PUT` | `/api/auth/me/github` | `AuthController.SetGitHubToken` | Yes | Missing | `[Authorize]` / Bearer (Match) | `GitHubTokenRequestDto` -> `GitHubIntegrationStatusDto` | `200,400,401,404,500,502` |
| 8 | `DELETE` | `/api/auth/me/github` | `AuthController.RemoveGitHubToken` | Yes | Missing | `[Authorize]` / Bearer (Match) | `GitHubIntegrationStatusDto` (Match) | `200,401,404` |
| 9 | `GET` | `/api/projects` | `ProjectsController.GetAll` | Yes | Missing | `[Authorize]` / Bearer (Match) | `all=false` -> `ProjectDto[]` (Match) | `200,401` |
| 10 | `GET` | `/api/projects/{id}` | `ProjectsController.GetById` | Yes | Missing | `[Authorize]` / Bearer (Match) | `ProjectDto` (Match) | `200,401,403,404` |
| 11 | `GET` | `/api/projects/owner/{ownerId}` | `ProjectsController.GetByOwnerId` | Yes | Missing | `[Authorize]` / Bearer (Match) | `ProjectDto[]` (Match) | `200,401,403` |
| 12 | `POST` | `/api/projects` | `ProjectsController.Create` | Yes | Missing | `[Authorize]` / Bearer (Match) | `CreateProjectDto` -> `ProjectDto` (Match) | `201,400,401,409` |
| 13 | `PUT` | `/api/projects/{id}` | `ProjectsController.Update` | Yes | Missing | `[Authorize]` / Bearer (Match) | `UpdateProjectDto` -> `ProjectDto` (Match) | `200,400,401,403,404,409` |
| 14 | `DELETE` | `/api/projects/{id}` | `ProjectsController.Delete` | Yes | Missing | `[Authorize]` / Bearer (Match) | `204` (Match) | `204,401,403,404` |
| 15 | `POST` | `/api/projects/{projectId}/github/sync` | `ProjectsController.SyncGitHub` | Yes | Missing | `[Authorize]` / Bearer (Match) | **`GitHubSyncResultDto` stale (`TD016-S5-001`)** | **Missing `409` (`TD016-S5-003`)** |
| 16 | `GET` | `/api/projects/{projectId}/commits` | `CommitsController.GetByProjectId` | Yes | Missing | `[Authorize]` / Bearer (Match) | `page=1,pageSize=20` (no min/max) -> `CommitDtoPagedResultDto` | `200,400,401,403,404` |
| 17 | `GET` | `/api/projects/{projectId}/commits/{id}` | `CommitsController.GetById` | Yes | Missing | `[Authorize]` / Bearer (Match) | `CommitDto` (Match) | `200,401,403,404` |
| 18 | `POST` | `/api/projects/{projectId}/commits` | `CommitsController.Create` | Yes | Missing | `[Authorize]` / Bearer (Match) | **`CreateCommitDto` stale (`TD016-S5-002`, `S5-008`)** | `201,400,401,403,404,409` |
| 19 | `POST` | `/api/projects/{projectId}/commits/{commitId}/analyses` | `AnalysesController.Create` | Yes | Missing | `[Authorize]` / Bearer (Match) | `AnalysisDto` (Match) | `202,401,403,404,409` |
| 20 | `GET` | `/api/projects/{projectId}/commits/{commitId}/analyses` | `AnalysesController.GetByCommitId` | Yes | Missing | `[Authorize]` / Bearer (Match) | `page=1,pageSize=20` (no min/max) -> `AnalysisDtoPagedResultDto` | `200,400,401,403,404` |
| 21 | `GET` | `/api/projects/{projectId}/commits/{commitId}/analyses/{analysisId}` | `AnalysesController.GetById` | Yes | Missing | `[Authorize]` / Bearer (Match) | `AnalysisDto` (Match) | `200,401,403,404` |
| 22 | `GET` | `/api/admin/users` | `AdminUsersController.GetAll` | Yes | Missing | `Admin` / Bearer (Match) | `page=1,pageSize=20` (no min/max) -> `UserDtoPagedResultDto` | `200,400,401,403` |
| 23 | `GET` | `/api/admin/users/{id}` | `AdminUsersController.GetById` | Yes | Missing | `Admin` / Bearer (Match) | `UserDto` (Match) | `200,401,403,404` |
| 24 | `PUT` | `/api/admin/users/{id}` | `AdminUsersController.Update` | Yes | Missing | `Admin` / Bearer (Match) | `UpdateUserDto` -> `UserDto` (Match) | `200,400,401,403,404,409` |
| 25 | `PUT` | `/api/admin/users/{id}/role` | `AdminUsersController.UpdateRole` | Yes | Missing | `Admin` / Bearer (Match) | `UpdateUserRoleDto` (RoleId not `required`) -> `UserDto` | `200,400,401,403,404,409` |
| 26 | `DELETE` | `/api/admin/users/{id}` | `AdminUsersController.Delete` | Yes | Missing | `Admin` / Bearer (Match) | `204` (Match) | `204,401,403,404,409` |
| 27 | `GET` | `/health` | `Program.MapHealthChecks` | **No** (`TD016-S5-012`) | N/A | Anonymous | HealthCheck response | `200,503` |
