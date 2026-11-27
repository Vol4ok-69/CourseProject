# TD-016 Stage 2 – Полный аудит функциональной полноты REST API

**Дата проведения:** 2026-11-27  
**Статус:** Завершён  
**Объект аудита:** REST API CourseProject / NetScope (`src/Backend/Api`) и спецификация `swagger.json`  
**Базовый документ требований:** `docs/plans/TD-016-Комплексный-аудит-Backend-REST-API.md`  
**Предшествующие отчёты:**  
- `docs/audits/TD-016-Stage-0-Preparation.md`  
- `docs/audits/TD-016-Stage-1-Backend-REST-API.md`  

---

## 1. Общая информация и границы Stage 2

Настоящий отчёт представляет результаты аудита функциональной полноты и согласованности REST API системы CourseProject / NetScope в рамках этапа Stage 2 плана TD-016.

### Границы этапа
- Полная инвентаризация всех фактически реализованных HTTP-операций (MVC-контроллеры и Minimal API / HealthChecks).
- Сверка фактических маршрутов с файлом `swagger.json` в корне репозитория.
- Функциональная декомпозиция API по предметным областям.
- Проверка реализуемости пользовательских сценариев для целевых клиентов:
  - **CLI** (.NET 10 / Spectre.Console);
  - **Desktop** (Tauri / React / TypeScript);
  - **Telegram-бот** (.NET 10 / Telegram.Bot).
- Выявление подтверждённых функциональных пробелов, потенциальных ограничений и открытых продуктовых вопросов.
- **Исключено из этапа:** изменение исходного кода, DTO, маршрутов, `swagger.json`, конфигурации, миграций и тестов.

---

## 2. Проверенная Git-ревизия и состояние рабочей директории

- **Ветка:** `main`
- **Коммит:** `a061bb8a388ef1848b64548d0a03178bb83162d7` (`a061bb8` — `fix: rename CSharp to CSharpAnalyzer`)
- **Состояние рабочей директории:** отслеживаемые файлы не изменялись; присутствуют только неотслеживаемые отчёты `docs/audits/TD-016-Stage-0-Preparation.md`, `docs/audits/TD-016-Stage-1-Backend-REST-API.md` и настоящий отчёт `docs/audits/TD-016-Stage-2-Endpoint-Completeness.md`.

---

## 3. Методика и исследованные источники

- **Исходный код контроллеров и точки входа:**
  - `src/Backend/Api/Program.cs`
  - `src/Backend/Api/Controllers/AuthController.cs`
  - `src/Backend/Api/Controllers/ProjectsController.cs`
  - `src/Backend/Api/Controllers/CommitsController.cs`
  - `src/Backend/Api/Controllers/AnalysesController.cs`
  - `src/Backend/Api/Controllers/AdminUsersController.cs`
- **OpenAPI-спецификация:** `swagger.json` (в корне репозитория).
- **Документация проекта:**
  - `docs/Технический регламент.md`
  - `docs/Исполнительное резюме.md`
  - `docs/plans/TD-016-Комплексный-аудит-Backend-REST-API.md`

---

## 4. Полный каталог фактических REST-эндпоинтов

Всего в приложении реализовано **27 HTTP-операций**: 26 действий в 5 MVC-контроллерах и 1 endpoint проверки здоровья (`MapHealthChecks("/health")` в `src/Backend/Api/Program.cs:313`).

### 4.1. `AuthController` (`api/auth`, без `[Authorize]` на классе, `src/Backend/Api/Controllers/AuthController.cs`)

| # | HTTP | Маршрут | Действие (строки) | Назначение | Аутентификация / Авторизация | Входные данные | Ответ | Статусы |
|---|---|---|---|---|---|---|---|---|
| 1 | `POST` | `/api/auth/register` | `Register` (`:43–77`) | Регистрация пользователя | Публичный (без `[Authorize]`) | Body: `RegisterDto` | `AuthResponseDto` | `201`, `400`, `409` |
| 2 | `POST` | `/api/auth/login` | `Login` (`:83–104`) | Вход и выдача JWT | Публичный (без `[Authorize]`) | Body: `LoginDto` | `AuthResponseDto` | `200`, `400`, `401` |
| 3 | `GET` | `/api/auth/me` | `GetCurrentUser` (`:111–142`) | Профиль текущего пользователя | `[Authorize]` | — | `UserDto` | `200`, `401`, `404` |
| 4 | `PUT` | `/api/auth/me/username` | `ChangeUsername` (`:149–196`) | Смена имени пользователя | `[Authorize]` | Body: `ChangeUsernameDto` | `AuthResponseDto` | `200`, `400`, `401`, `404`, `409` |
| 5 | `PUT` | `/api/auth/me/password` | `ChangePassword` (`:203–239`) | Смена пароля | `[Authorize]` | Body: `ChangePasswordDto` | `AuthResponseDto` | `200`, `400`, `401`, `404` |
| 6 | `GET` | `/api/auth/me/github` | `GetGitHubStatus` (`:246–274`) | Статус привязки GitHub-токена | `[Authorize]` | — | `GitHubIntegrationStatusDto` | `200`, `401`, `404` |
| 7 | `PUT` | `/api/auth/me/github` | `SetGitHubToken` (`:281–377`) | Сохранение/обновление GitHub-токена | `[Authorize]` | Body: `GitHubTokenRequestDto` | `GitHubIntegrationStatusDto` | `200`, `400`, `401`, `404`, `502` |
| 8 | `DELETE` | `/api/auth/me/github` | `RemoveGitHubToken` (`:384–413`) | Удаление GitHub-токена | `[Authorize]` | — | `GitHubIntegrationStatusDto` | `200`, `401`, `404` |

### 4.2. `ProjectsController` (`api/projects`, `[Authorize]` на классе, `src/Backend/Api/Controllers/ProjectsController.cs`)

| # | HTTP | Маршрут | Действие (строки) | Назначение | Авторизация | Входные данные | Ответ | Статусы |
|---|---|---|---|---|---|---|---|---|
| 9 | `GET` | `/api/projects` | `GetAll` (`:27–58`) | Список доступных проектов (без пагинации) | `[Authorize]` (User: свои; Admin: все при `all=true`) | Query: `bool all = false` | `IReadOnlyList<ProjectDto>` | `200`, `401` |
| 10 | `GET` | `/api/projects/{id:int}` | `GetById` (`:65–103`) | Получение проекта по ID | `[Authorize]` + владелец / Admin | Route: `id` | `ProjectDto` | `200`, `401`, `403`, `404` |
| 11 | `GET` | `/api/projects/owner/{ownerId:int}` | `GetByOwnerId` (`:110–145`) | Проекты указанного владельца (без пагинации) | `[Authorize]` + владелец / Admin | Route: `ownerId` | `IReadOnlyList<ProjectDto>` | `200`, `401`, `403` |
| 12 | `POST` | `/api/projects` | `Create` (`:152–200`) | Создание проекта | `[Authorize]` | Body: `CreateProjectDto` | `ProjectDto` | `201`, `400`, `401`, `409` |
| 13 | `PUT` | `/api/projects/{id:int}` | `Update` (`:207–274`) | Обновление проекта | `[Authorize]` + владелец / Admin | Route: `id`, Body: `UpdateProjectDto` | `ProjectDto` | `200`, `400`, `401`, `403`, `404`, `409` |
| 14 | `DELETE` | `/api/projects/{id:int}` | `Delete` (`:281–318`) | Удаление проекта | `[Authorize]` + владелец / Admin | Route: `id` | `204 No Content` | `204`, `401`, `403`, `404` |
| 15 | `POST` | `/api/projects/{projectId:int}/github/sync` | `SyncGitHub` (`:325–430`) | Синхронизация коммитов с GitHub | `[Authorize]` + владелец / Admin | Route: `projectId` | `GitHubSyncResultDto` | `200`, `400`, `401`, `403`, `404`, `409`, `502` |

### 4.3. `CommitsController` (`api/projects/{projectId:int}/commits`, `[Authorize]` на классе, `src/Backend/Api/Controllers/CommitsController.cs`)

| # | HTTP | Маршрут | Действие (строки) | Назначение | Авторизация | Входные данные | Ответ | Статусы |
|---|---|---|---|---|---|---|---|---|
| 16 | `GET` | `/api/projects/{projectId:int}/commits` | `GetByProjectId` (`:28–96`) | Список коммитов проекта (с пагинацией) | `[Authorize]` + владелец / Admin | Route: `projectId`, Query: `page=1`, `pageSize=20` | `PagedResultDto<CommitDto>` | `200`, `400`, `401`, `403`, `404` |
| 17 | `GET` | `/api/projects/{projectId:int}/commits/{id:int}` | `GetById` (`:105–154`) | Получение коммита по ID | `[Authorize]` + владелец / Admin | Route: `projectId`, `id` | `CommitDto` | `200`, `401`, `403`, `404` |
| 18 | `POST` | `/api/projects/{projectId:int}/commits` | `Create` (`:161–227`) | Ручное создание коммита | `[Authorize]` + владелец / Admin | Route: `projectId`, Body: `CreateCommitDto` | `CommitDto` | `201`, `400`, `401`, `403`, `404`, `409` |

### 4.4. `AnalysesController` (`api/projects/{projectId:int}/commits/{commitId:int}/analyses`, `[Authorize]` на классе, `src/Backend/Api/Controllers/AnalysesController.cs`)

| # | HTTP | Маршрут | Действие (строки) | Назначение | Авторизация | Входные данные | Ответ | Статусы |
|---|---|---|---|---|---|---|---|---|
| 19 | `POST` | `/api/projects/{projectId:int}/commits/{commitId:int}/analyses` | `Create` (`:30–98`) | Постановка анализа в очередь (`AcceptedAtAction`) | `[Authorize]` + владелец / Admin | Route: `projectId`, `commitId` | `AnalysisDto` | `202`, `401`, `403`, `404`, `409` |
| 20 | `GET` | `/api/projects/{projectId:int}/commits/{commitId:int}/analyses` | `GetByCommitId` (`:109–177`) | История анализов коммита (с пагинацией) | `[Authorize]` + владелец / Admin | Route: `projectId`, `commitId`, Query: `page=1`, `pageSize=20` | `PagedResultDto<AnalysisDto>` | `200`, `400`, `401`, `403`, `404` |
| 21 | `GET` | `/api/projects/{projectId:int}/commits/{commitId:int}/analyses/{analysisId:int}` | `GetById` (`:187–233`) | Детали анализа, результаты и замечания | `[Authorize]` + владелец / Admin | Route: `projectId`, `commitId`, `analysisId` | `AnalysisDto` | `200`, `401`, `403`, `404` |

### 4.5. `AdminUsersController` (`api/admin/users`, `[Authorize(Roles = "Admin")]` на классе, `src/Backend/Api/Controllers/AdminUsersController.cs`)

| # | HTTP | Маршрут | Действие (строки) | Назначение | Авторизация | Входные данные | Ответ | Статусы |
|---|---|---|---|---|---|---|---|---|
| 22 | `GET` | `/api/admin/users` | `GetAll` (`:29–66`) | Список пользователей (с пагинацией) | `[Authorize(Roles = "Admin")]` | Query: `page=1`, `pageSize=20` | `PagedResultDto<UserDto>` | `200`, `400`, `401`, `403` |
| 23 | `GET` | `/api/admin/users/{id:int}` | `GetById` (`:74–92`) | Получение пользователя по ID | `[Authorize(Roles = "Admin")]` | Route: `id` | `UserDto` | `200`, `401`, `403`, `404` |
| 24 | `PUT` | `/api/admin/users/{id:int}` | `Update` (`:98–141`) | Обновление данных пользователя | `[Authorize(Roles = "Admin")]` | Route: `id`, Body: `UpdateUserDto` | `UserDto` | `200`, `400`, `401`, `403`, `404`, `409` |
| 25 | `PUT` | `/api/admin/users/{id:int}/role` | `UpdateRole` (`:148–209`) | Изменение роли пользователя | `[Authorize(Roles = "Admin")]` | Route: `id`, Body: `UpdateUserRoleDto` | `UserDto` | `200`, `400`, `401`, `403`, `404`, `409` |
| 26 | `DELETE` | `/api/admin/users/{id:int}` | `Delete` (`:216–270`) | Удаление пользователя | `[Authorize(Roles = "Admin")]` | Route: `id` | `204 No Content` | `204`, `401`, `403`, `404`, `409` |

### 4.6. Системные эндпоинты (`Program.cs`)

| # | HTTP | Маршрут | Обработчик | Назначение | Авторизация | Ответ | Статусы |
|---|---|---|---|---|---|---|---|
| 27 | `GET` | `/health` | `app.MapHealthChecks("/health")` (`Program.cs:313`) | Проверка работоспособности API и БД | Публичный | HealthCheck JSON/text | `200`, `503` |

### 4.7. Детализация параметров пагинации и контрактов DTO

- **Пагинация (`CommitsController.GetByProjectId`, `AnalysesController.GetByCommitId`, `AdminUsersController.GetAll`):**
  - `page` (`int`, по умолчанию `1`): при `page < 1` возвращается `400 Bad Request` (`ApiErrorCodes.ValidationFailed`).
  - `pageSize` (`int`, по умолчанию `20`): при `pageSize < 1` возвращается `400 Bad Request` (`ApiErrorCodes.ValidationFailed`); значения `> 100` ограничиваются через `Math.Min(pageSize, 100)` до `100`.
  - Формат ответа: `PagedResultDto<T>` (`Items`, `TotalCount`, `Page`, `PageSize`).
- **Ключевые входные DTO (`src/Backend/Application/DTOs/`):**
  - `RegisterDto` (`RegisterDto.cs:8-23`): `Username` (`string`, `[Required]`, `[StringLength(50, MinimumLength = 3)]`), `Password` (`string`, `[Required]`, `[MinLength(6)]`).
  - `LoginDto` (`LoginDto.cs:8-21`): `Username` (`string`, `[Required]`), `Password` (`string`, `[Required]`).
  - `ChangeUsernameDto` (`ChangeUsernameDto.cs:8-16`): `Username` (`string`, `[Required]`, `[StringLength(50, MinimumLength = 3)]`).
  - `ChangePasswordDto` (`ChangePasswordDto.cs:8-22`): `CurrentPassword` (`string`, `[Required]`), `NewPassword` (`string`, `[Required]`, `[MinLength(6)]`).
  - `GitHubTokenRequestDto` (`GitHubTokenRequestDto.cs:8-15`): `Token` (`string`, `[Required]`, без дополнительных ограничений длины или префикса в DataAnnotations).
  - `CreateProjectDto` (`CreateProjectDto.cs:8-23`) и `UpdateProjectDto` (`UpdateProjectDto.cs:8-23`): идентичный набор обязательных полей — `Name` (`string`, `[Required]`, `[StringLength(50, MinimumLength = 3)]`), `RepoUrl` (`string`, `[Required]`, `[RegularExpression(@"^https:\/\/github\.com\/[^\/\s]+\/[^\/\s]+(?:\.git)?\/?$")]`); частичное обновление (PATCH) не поддерживается, `null` или отсутствие любого поля отклоняются с `400 Bad Request`.
  - `CreateCommitDto` (`CreateCommitDto.cs:8-33`): `CommitHash` (`string`, `[Required]`, `[StringLength(64, MinimumLength = 7)]`, `[RegularExpression(@"\A[0-9a-fA-F]{7,64}\z")]`), `CommitDate` (`DateTime?`, `[Required]`, в `Validate()` запрещён `DateTimeKind.Unspecified` — требуется явный часовой пояс).
  - `UpdateUserDto` (`UpdateUserDto.cs:8-16`): `Username` (`string`, `[Required]`, `[StringLength(50, MinimumLength = 3)]`).
  - `UpdateUserRoleDto` (`UpdateUserRoleDto.cs:8-15`): `RoleId` (`int`, `[Range(1, int.MaxValue)]`).
- **Ключевые выходные DTO (`src/Backend/Application/DTOs/`):**
  - `AuthResponseDto` (`AuthResponseDto.cs:6-12`): `AccessToken` (`string`).
  - `UserDto` (`UserDto.cs:6-27`): `Id` (`int`), `Username` (`string`), `RoleId` (`int`), `RoleName` (`string`).
  - `ProjectDto` (`ProjectDto.cs:6-27`): `Id` (`int`), `Name` (`string`), `RepoUrl` (`string`), `OwnerId` (`int`).
  - `CommitDto` (`CommitDto.cs:6-27`): `Id` (`int`), `ProjectId` (`int`), `CommitHash` (`string`), `CommitDate` (`DateTime`).
  - `AnalysisDto` (`AnalysisDto.cs:8-95`): `Id`, `CommitId`, `Status` (`AnalysisStatus`), `CreatedAt`, `StartedAt?`, `CompletedAt?`, `ErrorMessage?`, `Results` (`IReadOnlyList<AnalysisResultDto>`: `Id`, `FilePath`, `LineNumber`, `Rule`, `Message`, `Severity`, `Recommendation?`, `AnalyzerType`).
  - `PagedResultDto<T>` (`PagedResultDto.cs:7-34`): `Items` (`IReadOnlyList<T>`), `TotalCount` (`int`), `Page` (`int`), `PageSize` (`int`).

---

## 5. Сверка реализации с OpenAPI и документацией

**Дата и время проверки:** 2026-11-27 00:38 (+07:00)  
**Способ получения спецификации:** файл `swagger.json` в корне репозитория, обновлённый владельцем проекта с работающего API (`curl -o "swagger.json" "http://localhost:5000/swagger/v1/swagger.json"`).

### Фактический результат сверки (`swagger.json` vs код)
1. **MVC-контроллеры (26 эндпоинтов):** все 26 действий в 5 контроллерах (`AuthController`, `ProjectsController`, `CommitsController`, `AnalysesController`, `AdminUsersController`) **полностью присутствуют** в актуальном `swagger.json`, включая `/api/auth/me` и `/api/auth/me/github` (`GET`, `PUT`, `DELETE`).
2. **Системные эндпоинты (`Program.cs:313`):** `GET /health` (`app.MapHealthChecks("/health")`) отсутствует в `swagger.json`, что является стандартным поведением ASP.NET Core HealthChecks и Swashbuckle.
3. **Устаревшие сведения Stage 1:** в отчёте Stage 1 были ошибочно указаны несуществующие маршруты (`POST .../cancel`, `GET .../results`, `GET .../report` в `AnalysesController`, а также `/api/auth/profile`, `/api/auth/change-password`, `/api/auth/change-username`, `/api/projects/{id}/sync`). Повторная проверка исходного кода контроллеров и актуального `swagger.json` подтвердила, что фактический каталог Stage 2 (26 действий контроллеров + `/health`) является единственно верным.

---

## 6. Функциональная декомпозиция API

- **Регистрация, вход и профиль:** полностью обеспечены (`POST /api/auth/register`, `POST /api/auth/login`, `GET /api/auth/me`, `PUT /api/auth/me/username`, `PUT /api/auth/me/password`).
- **Подключение GitHub:** обеспечено (`GET/PUT/DELETE /api/auth/me/github`, `POST /api/projects/{projectId}/github/sync`).
- **Управление проектами и коммитами:** обеспечено (`ProjectsController`, `CommitsController`). Ограничение: списки проектов не имеют пагинации.
- **Анализы и замечания:** запуск (`POST .../analyses`), история (`GET .../analyses`) и детальный просмотр с замечаниями (`GET .../analyses/{analysisId}`) обеспечены. **Отсутствует** операция отмены запущенного/ожидающего анализа.
- **Администрирование:** полностью обеспечено (`AdminUsersController`).

---

## 7. Сценарии CLI и последовательности API-вызовов

1. **Регистрация и вход:** `POST /api/auth/register` -> `POST /api/auth/login`.
2. **Текущий пользователь:** `GET /api/auth/me`.
3. **Просмотр и управление проектами:** `GET /api/projects`, `POST /api/projects`, `PUT /api/projects/{id}`, `DELETE /api/projects/{id}`.
4. **Настройка GitHub и синхронизация:** `PUT /api/auth/me/github` -> `POST /api/projects/{projectId}/github/sync`.
5. **Просмотр коммитов:** `GET /api/projects/{projectId}/commits?page=1&pageSize=20`.
6. **Запуск и отслеживание анализа:** `POST /api/projects/{projectId}/commits/{commitId}/analyses` (`202 Accepted` с `AnalysisDto`) -> polling `GET /api/projects/{projectId}/commits/{commitId}/analyses/{analysisId}` до терминального статуса -> чтение результатов и замечаний из поля `Results` того же ответа `AnalysisDto`.
7. **Обработка ошибок и истечения JWT:** стандартные ответы `401 Unauthorized` / `403 Forbidden` / `4xx` в формате RFC 9457 (`ProblemDetails`).

---

## 8. Сценарии Desktop и последовательности API-вызовов

Все основные сценарии (авторизация, профиль, список и карточка проекта, CRUD проектов, подключение GitHub, история коммитов, синхронизация, история анализов, запуск и polling статуса, просмотр результатов и замечаний, администрирование) **реализуемы через существующий API** без прямого доступа к БД.

**Замечания для UI:**
- Отсутствие пагинации в `GET /api/projects` и `GET /api/projects/owner/{ownerId}` (при росте числа проектов).
- Отсутствие эндпоинта отмены анализа (`Cancel`), если пользователь захочет остановить длительный анализ из UI.

---

## 9. Сценарии Telegram-бота и последовательности API-вызовов

Согласно `docs/Технический регламент.md` (разделы 1, 15, 17) и `docs/Исполнительное резюме.md`, `src/Bot` (.NET / `Telegram.Bot`) является **планируемым клиентом** для оперативных оповещений и команд через REST API (каталог зарезервирован, код отсутствует).

- **Что можно реализовать без изменения Backend:** команды просмотра проектов (`GET /api/projects`), коммитов (`GET .../commits`) и статуса/результатов анализа (`GET .../analyses/{analysisId}`) при условии хранения JWT-токена на стороне бота (например, через ввод логина/пароля или токена).
- **Что не определено (требует продуктового решения):**
  - механизм привязки `TelegramUserId` / `ChatId` к учётной записи NetScope;
  - механизм доставки push-уведомлений о завершении анализа (webhook, polling со стороны бота или отдельный endpoint подписки).

---

## 10. Матрица функционального покрытия

| Возможность / сценарий | CLI | Desktop | Telegram-бот | Существующий API | Вывод |
|---|---|---|---|---|---|
| Авторизация | Покрыто | Покрыто | Частично покрыто | `POST /api/auth/register`, `POST /api/auth/login` | Частично покрыто: REST API поддерживает JWT-аутентификацию, но механизм привязки Telegram ID к учётной записи не определён |
| Получение профиля | Покрыто | Покрыто | Покрыто | `GET /api/auth/me` | Покрыто |
| Список проектов | Покрыто | Покрыто | Покрыто | `GET /api/projects`, `GET /api/projects/owner/{ownerId}` | Покрыто (без пагинации) |
| Создание проекта | Покрыто | Покрыто | Не требуется | `POST /api/projects` | Покрыто (бот предназначен для уведомлений и оперативных команд) |
| Изменение проекта | Покрыто | Покрыто | Не требуется | `PUT /api/projects/{id}` | Покрыто |
| Удаление проекта | Покрыто | Покрыто | Не требуется | `DELETE /api/projects/{id}` | Покрыто |
| Работа с GitHub | Покрыто | Покрыто | Не требуется | `GET/PUT/DELETE /api/auth/me/github` | Покрыто |
| Синхронизация коммитов | Покрыто | Покрыто | Покрыто | `POST /api/projects/{projectId}/github/sync` | Покрыто |
| Просмотр коммитов | Покрыто | Покрыто | Покрыто | `GET /api/projects/{projectId}/commits`, `GET .../commits/{id}` | Покрыто |
| Запуск анализа | Покрыто | Покрыто | Покрыто | `POST /api/projects/{projectId}/commits/{commitId}/analyses` | Покрыто |
| Статус анализа | Покрыто | Покрыто | Покрыто | `GET /api/projects/{projectId}/commits/{commitId}/analyses/{analysisId}` | Покрыто |
| Отмена анализа | Требуется решение владельца проекта | Требуется решение владельца проекта | Не требуется | — | HTTP-эндпоинт отсутствует; требование отмены не подтверждено документацией |
| История анализов | Покрыто | Покрыто | Покрыто | `GET /api/projects/{projectId}/commits/{commitId}/analyses` | Покрыто |
| Результаты анализа и замечания | Покрыто | Покрыто | Покрыто | `GET /api/projects/{projectId}/commits/{commitId}/analyses/{analysisId}` | Покрыто |
| Административные операции | Покрыто | Покрыто | Не требуется | `GET/PUT/DELETE /api/admin/users/*` | Покрыто |
| Привязка Telegram и уведомления | Не требуется | Не требуется | Требуется решение владельца проекта | — | Требуется решение владельца проекта |

---

## 11. Подтверждённые функциональные пробелы

Подтверждённых функциональных пробелов не выявлено.

Отсутствие HTTP-эндпоинта отмены анализа (ранее обозначенное как `[TD016-S2-GAP-001]`) снято с перечня подтверждённых пробелов: проверка `docs/Технический регламент.md` и `docs/Исполнительное резюме.md` показала, что обязательная пользовательская операция отмены анализа через REST API для CLI/Desktop не зафиксирована в требованиях. Внутренняя поддержка `CancellationToken` и статуса `Cancelled` в `AnalysisService` относится к внутреннему механизму остановки при потере аренды или завершении хоста.

---

## 12. Потенциальные проблемы и ограничения

1. **Отсутствие пагинации в `GET /api/projects` и `GET /api/projects/owner/{ownerId}`:** при большом количестве проектов возможна избыточная нагрузка на память и сеть.

---

## 13. Рекомендуемые новые эндпоинты

Обязательных новых эндпоинтов, необходимых для покрытия документированных требований, не выявлено.

*Возможным расширением API при утверждении соответствующего продуктового сценария является добавление `POST /api/projects/{projectId:int}/commits/{commitId:int}/analyses/{analysisId:int}/cancel`.*

---

## 14. Открытые продуктовые вопросы

1. **[TD016-S2-QUESTION-001] Привязка Telegram-аккаунта и уведомления:** требуется определить механизм связывания `TelegramUserId`/`ChatId` с пользователем NetScope и способ доставки уведомлений о завершении анализа (webhook, polling или push).
2. **[TD016-S2-QUESTION-002] Отмена анализа через REST API:** требуется решить, нужна ли пользователям CLI и Desktop возможность принудительно отменять запущенный/ожидающий анализ через отдельный HTTP-эндпоинт.

---

## 15. Ограничения проведённого аудита

- Клиентские приложения (`src/Cli`, `src/Desktop`, `src/Bot`) на момент аудита отсутствуют в решении `CourseProject.slnx` (каталоги зарезервированы), поэтому проверка сценариев проводилась по документации и контрактам Backend API.
- Динамическое тестирование живого HTTP-сервера с реальным Telegram-ботом не выполнялось.

---

## 16. Итоговое заключение Stage 2

REST API CourseProject / NetScope в целом **функционально полно покрывает** документированные сценарии CLI и Desktop (27 фактических HTTP-операций: 26 действий контроллеров + `GET /health`). Актуальный `swagger.json` полностью соответствует всем 26 эндпоинтам контроллеров. Подтверждённых функциональных пробелов не выявлено (0); сформулированы 2 открытых продуктовых вопроса (`[TD016-S2-QUESTION-001]` по интеграции Telegram-бота и `[TD016-S2-QUESTION-002]` по необходимости HTTP-эндпоинта отмены анализа).

### Исправления по результатам заключительной корректировки
- Пересмотрен статус `TD016-S2-GAP-001`: отсутствие HTTP-эндпоинта отмены анализа снято с подтверждённых пробелов (требование не зафиксировано в документации) и перенесено в открытые продуктовые вопросы (`[TD016-S2-QUESTION-002]`).
- Устранено противоречие в строке «Авторизация» матрицы для Telegram-бота и согласованы итоговые показатели матрицы.
- Детализированы параметры пагинации (`page`, `pageSize`, ограничения `1..100`, `PagedResultDto<T>`) и ограничения входных DTO.
- Полностью согласован каталог маршрутов между отчётами Stage 1 и Stage 2.
