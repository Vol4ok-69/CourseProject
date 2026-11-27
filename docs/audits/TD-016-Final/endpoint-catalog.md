# Полный каталог фактических HTTP-операций (27 операций)

Источник: инвентаризация Stage 2 (`docs/audits/TD-016-Stage-2-Endpoint-Completeness.md`) и сверка OpenAPI Stage 5 (`docs/audits/TD-016-Stage-5-OpenAPI/endpoint-contract-matrix.md`).

| # | HTTP | Маршрут | Контроллер / Обработчик | Назначение | Авторизация | Вход / DTO | Ответ / Статусы | OpenAPI | Замечания |
|---|---|---|---|---|---|---|---|---|---|
| 1 | `POST` | `/api/auth/register` | `AuthController.Register` | Регистрация | Публичный | `RegisterDto` | `AuthResponseDto` (`201,400,409,429`) | Да | Наследует `Bearer` (`TD016-F-003`), нет `Retry-After` |
| 2 | `POST` | `/api/auth/login` | `AuthController.Login` | Вход и выдача JWT | Публичный | `LoginDto` | `AuthResponseDto` (`200,400,401,429`) | Да | Наследует `Bearer` (`TD016-F-003`), нет `Retry-After` |
| 3 | `GET` | `/api/auth/me` | `AuthController.GetCurrentUser` | Профиль пользователя | `[Authorize]` | — | `UserDto` (`200,401,404`) | Да | Нет `operationId`, `nullable: true` |
| 4 | `PUT` | `/api/auth/me/username` | `AuthController.ChangeUsername` | Смена имени | `[Authorize]` | `ChangeUsernameDto` | `AuthResponseDto` (`200,400,401,404,409`) | Да | Нет `operationId` |
| 5 | `PUT` | `/api/auth/me/password` | `AuthController.ChangePassword` | Смена пароля | `[Authorize]` | `ChangePasswordDto` | `AuthResponseDto` (`200,400,401,404,429`) | Да | Нет `Retry-After` |
| 6 | `GET` | `/api/auth/me/github` | `AuthController.GetGitHubStatus` | Статус GitHub PAT | `[Authorize]` | — | `GitHubIntegrationStatusDto` (`200,401,404`) | Да | Нет `operationId` |
| 7 | `PUT` | `/api/auth/me/github` | `AuthController.SetGitHubToken` | Сохранение PAT | `[Authorize]` | `GitHubTokenRequestDto` | `GitHubIntegrationStatusDto` (`200,400,401,404,500,502`) | Да | Нет `operationId` |
| 8 | `DELETE` | `/api/auth/me/github` | `AuthController.RemoveGitHubToken` | Удаление PAT | `[Authorize]` | — | `GitHubIntegrationStatusDto` (`200,401,404`) | Да | Нет `operationId` |
| 9 | `GET` | `/api/projects` | `ProjectsController.GetAll` | Список проектов | `[Authorize]` | Query: `all=false` | `ProjectDto[]` (`200,401`) | Да | Без пагинации (`TD016-F-007`) |
| 10 | `GET` | `/api/projects/{id:int}` | `ProjectsController.GetById` | Проект по ID | `[Authorize]` + Owner/Admin | Route: `id` | `ProjectDto` (`200,401,403,404`) | Да | `nullable: true` в ответе |
| 11 | `GET` | `/api/projects/owner/{ownerId:int}` | `ProjectsController.GetByOwnerId` | Проекты владельца | `[Authorize]` + Owner/Admin | Route: `ownerId` | `ProjectDto[]` (`200,401,403`) | Да | Без пагинации; `200 []` при отс. ID |
| 12 | `POST` | `/api/projects` | `ProjectsController.Create` | Создание проекта | `[Authorize]` | `CreateProjectDto` | `ProjectDto` (`201,400,401,409`) | Да | Нет `operationId` |
| 13 | `PUT` | `/api/projects/{id:int}` | `ProjectsController.Update` | Обновление проекта | `[Authorize]` + Owner/Admin | `id`, `UpdateProjectDto` | `ProjectDto` (`200,400,401,403,404,409`) | Да | Вопрос по смене `RepoUrl` |
| 14 | `DELETE` | `/api/projects/{id:int}` | `ProjectsController.Delete` | Удаление проекта | `[Authorize]` + Owner/Admin | Route: `id` | `204` (`204,401,403,404`) | Да | Каскад при активном анализе (`TD016-F-006`) |
| 15 | `POST` | `/api/projects/{projectId:int}/github/sync` | `ProjectsController.SyncGitHub` | Синхронизация GitHub | `[Authorize]` + Owner/Admin | Route: `projectId` | `GitHubSyncResultDto` (`200,400,401,403,404,409,502`) | Да | Устаревший DTO + нет `409` (`TD016-F-001`) |
| 16 | `GET` | `/api/projects/{projectId:int}/commits` | `CommitsController.GetByProjectId` | Список коммитов | `[Authorize]` + Owner/Admin | `projectId`, `page=1`, `pageSize=20` | `PagedResultDto<CommitDto>` (`200,400,401,403,404`) | Да | Нет min/max в схеме |
| 17 | `GET` | `/api/projects/{projectId:int}/commits/{id:int}` | `CommitsController.GetById` | Коммит по ID | `[Authorize]` + Owner/Admin | `projectId`, `id` | `CommitDto` (`200,401,403,404`) | Да | Нет `operationId` |
| 18 | `POST` | `/api/projects/{projectId:int}/commits` | `CommitsController.Create` | Создание коммита | `[Authorize]` + Owner/Admin | `projectId`, `CreateCommitDto` | `CommitDto` (`201,400,401,403,404,409`) | Да | `maxLength: 40` вместо `64` (`TD016-F-001`) |
| 19 | `POST` | `/api/projects/{projectId:int}/commits/{commitId:int}/analyses` | `AnalysesController.Create` | Запуск анализа | `[Authorize]` + Owner/Admin | `projectId`, `commitId` | `AnalysisDto` (`202,401,403,404,409`) | Да | Нет `operationId` |
| 20 | `GET` | `/api/projects/{projectId:int}/commits/{commitId:int}/analyses` | `AnalysesController.GetByCommitId` | История анализов | `[Authorize]` + Owner/Admin | `page=1`, `pageSize=20` | `PagedResultDto<AnalysisDto>` (`200,400,401,403,404`) | Да | Включает `Results` (`TD016-F-009`) |
| 21 | `GET` | `/api/projects/{projectId:int}/commits/{commitId:int}/analyses/{analysisId:int}` | `AnalysesController.GetById` | Детали анализа | `[Authorize]` + Owner/Admin | `projectId`, `commitId`, `analysisId` | `AnalysisDto` (`200,401,403,404`) | Да | `nullable: true` на `Results` |
| 22 | `GET` | `/api/admin/users` | `AdminUsersController.GetAll` | Список пользователей | `Admin` | `page=1`, `pageSize=20` | `PagedResultDto<UserDto>` (`200,400,401,403`) | Да | Нет min/max в схеме |
| 23 | `GET` | `/api/admin/users/{id:int}` | `AdminUsersController.GetById` | Пользователь по ID | `Admin` | Route: `id` | `UserDto` (`200,401,403,404`) | Да | Нет `operationId` |
| 24 | `PUT` | `/api/admin/users/{id:int}` | `AdminUsersController.Update` | Обновление имени | `Admin` | `id`, `UpdateUserDto` | `UserDto` (`200,400,401,403,404,409`) | Да | Нет HTTP-интеграции (`TD016-F-011`) |
| 25 | `PUT` | `/api/admin/users/{id:int}/role` | `AdminUsersController.UpdateRole` | Смена роли | `Admin` | `id`, `UpdateUserRoleDto` | `UserDto` (`200,400,401,403,404,409`) | Да | `RoleId` не в `required`; нет HTTP-интеграции |
| 26 | `DELETE` | `/api/admin/users/{id:int}` | `AdminUsersController.Delete` | Удаление пользователя | `Admin` | Route: `id` | `204` (`204,401,403,404,409`) | Да | Нет HTTP-интеграции (`TD016-F-011`) |
| 27 | `GET` | `/health` | `Program.MapHealthChecks` | HealthCheck API и БД | Публичный | — | HealthCheck (`200,503`) | **Нет** | Ожидаемо для `MapHealthChecks` |
