# TD-016 Stage 1 – Независимый аудит Backend и REST API

**Дата проведения:** 2026-11-26  
**Статус:** Завершён (скорректирован по результатам ревью)  
**Объект аудита:** Серверная часть CourseProject / NetScope (слои Domain, Application, Infrastructure, Api)  
**Базовый документ требований:** `docs/plans/TD-016-Комплексный-аудит-Backend-REST-API.md`  
**Предшествующий отчёт:** `docs/audits/TD-016-Stage-0-Preparation.md`  

---

## 1. Общая информация и границы Stage 1

Настоящий отчёт представляет результаты независимого технического аудита серверной части (`Backend`) и REST API проекта CourseProject / NetScope в рамках этапа Stage 1 плана TD-016.

### Границы этапа
- **Включено в аудит:**
  - Контроллеры и маршрутизация REST API (`src/Backend/Api/Controllers/`);
  - Обработка ошибок, статус-коды и спецификация RFC 9457 (`ApiProblemDetails`, middleware);
  - Аутентификация (JWT), авторизация (роли `Admin`, `User`), разграничение доступа к ресурсам (Resource Ownership);
  - DTO и валидация входных данных (`src/Backend/Application/DTOs/`);
  - Сервисы приложения и бизнес-логика (`src/Backend/Application/Services/`);
  - Инфраструктура доступа к данным (EF Core, репозитории, транзакции, блокировки, конфигурации сущностей);
  - Конкурентная обработка (Advisory Locks, `FOR UPDATE SKIP LOCKED`, фоновые очереди анализа);
  - Внешние интеграции (GitHub API, Docker Runner для DatabaseAnalyzer);
  - Регрессионный аудит реализованных ранее технических долгов: TD-001, TD-002, TD-003, TD-006, TD-007.
- **Исключено из этапа (вынесено в последующие этапы согласно плану TD-016):**
  - **Stage 2:** Полнота и согласованность REST-эндпоинтов;
  - **Stage 3:** Аудит базы данных PostgreSQL, схемы, миграций и производительности;
  - **Stage 4:** Комплексный аудит безопасности, секретов и OWASP;
  - **Stage 5:** Аудит OpenAPI-спецификации и контрактов;
  - **Stage 6:** Аудит тестового покрытия и качества тестов;
  - **Stage 7:** Итоговый сводный отчёт.

---

## 2. Проверенная Git-ревизия и состояние рабочей директории

- **Ветка:** `main` (синхронизирована с `origin/main`).
- **Коммит:** `a061bb8a388ef1848b64548d0a03178bb83162d7` (короткий хеш: `a061bb8`).
- **Сообщение коммита:** `fix: rename CSharp to CSharpAnalyzer` (2026-11-26 22:50:31 +0700).
- **Состояние рабочей директории на момент первоначального аудита Stage 1 (2026-11-26):** отслеживаемые файлы не изменялись; присутствовал неотслеживаемый файл `docs/audits/TD-016-Stage-0-Preparation.md`. На момент заключительной корректировки (2026-11-27) среди неотслеживаемых файлов присутствуют `docs/audits/TD-016-Stage-0-Preparation.md`, `docs/audits/TD-016-Stage-1-Backend-REST-API.md` и `docs/audits/TD-016-Stage-2-Endpoint-Completeness.md`.
- **Строгий режим:** изменения в исходный код, DTO, миграции, тесты и конфигурации во время аудита не вносились.

---

## 3. Методика и перечень исследованных источников

### Методика
1. **Статический анализ кода:** проверка реализации контроллеров, сервисов, репозиториев, конфигураций Entity Framework и вспомогательных middleware.
2. **Анализ контрактов и валидации:** проверка DTO, аннотаций данных, фильтров валидации, сопоставление ответов контроллеров со стандартом RFC 9457 (ProblemDetails).
3. **Анализ конкурентности и транзакций:** проверка изоляции очередей, advisory locks, блокировок на уровне строк (`FOR UPDATE`, `FOR NO KEY UPDATE`), обработки отмены (`CancellationToken`).
4. **Регрессионная верификация:** проверка сохранения ключевых механизмов TD-001 (изоляция Docker), TD-002 (изоляция сбоев анализаторов), TD-003 (изоляция рабочих каталогов и очистка осиротевших анализов), TD-006 (синхронизация DAG коммитов GitHub), TD-007 (политика повторных попыток и poison pill).
5. **Динамическая проверка:** проверка результатов сборки решения и полного набора автоматических тестов (`dotnet test`).

### Перечень исследованных файлов
- **Api:**
  - `src/Backend/Api/Program.cs`
  - `src/Backend/Api/Controllers/AuthController.cs`
  - `src/Backend/Api/Controllers/ProjectsController.cs`
  - `src/Backend/Api/Controllers/CommitsController.cs`
  - `src/Backend/Api/Controllers/AnalysesController.cs`
  - `src/Backend/Api/Controllers/AdminUsersController.cs`
  - `src/Backend/Api/Common/ApiProblemDetails.cs`
  - `src/Backend/Api/Common/ApiErrorCodes.cs`
  - `src/Backend/Api/Common/ApiJwtEvents.cs`
- **Application:**
  - `src/Backend/Application/Services/AuthService.cs`
  - `src/Backend/Application/Services/UserService.cs`
  - `src/Backend/Application/Services/ProjectService.cs`
  - `src/Backend/Application/Services/CommitService.cs`
  - `src/Backend/Application/Services/AnalysisService.cs`
  - `src/Backend/Application/Services/GitHubSyncService.cs`
  - `src/Backend/Application/Services/GitHubSyncCoordinator.cs`
  - `src/Backend/Application/Services/RepositorySourceService.cs`
  - `src/Backend/Application/Services/AnalysisBackgroundService.cs`
  - `src/Backend/Api/BackgroundServices/AnalysisCleanupBackgroundService.cs`
  - `src/Backend/Infrastructure/Storage/AnalysisCleanupService.cs`
  - `src/Backend/Application/DTOs/*` (все DTO сущностей и запросов)
- **Infrastructure:**
  - `src/Backend/Infrastructure/Data/AppDbContext.cs`
  - `src/Backend/Infrastructure/Data/DatabaseInitializer.cs`
  - `src/Backend/Infrastructure/Data/Configurations/*` (`ProjectConfiguration`, `CommitConfiguration`, `AnalysisConfiguration`, etc.)
  - `src/Backend/Infrastructure/Repositories/*` (`ProjectRepository`, `CommitRepository`, `AnalysisRepository`, `UserRepository`, etc.)
  - `src/Backend/Infrastructure/Services/DatabaseContainerRunner.cs`
  - `src/Backend/Infrastructure/Services/WorkspaceFileLockProvider.cs`
- **Domain:**
  - `src/Backend/Domain/Entities/*` (`User`, `Role`, `Project`, `Commit`, `Analysis`, `AnalysisResult`, `AnalysisRuleViolation`)

---

## 4. Архитектура и границы слоёв

Проект строго следует принципам Clean Architecture:
- `Domain`: чистые POCO-сущности и базовые перечисления (`AnalysisStatus`, `Severity`). Не содержит зависимостей от внешних пакетов или инфраструктуры.
- `Application`: содержит DTO, интерфейсы сервисов/репозиториев и логику сценариев использования (use cases). Не зависит от ASP.NET Core и EF Core.
- `Infrastructure`: реализует доступ к данным (EF Core, Npgsql), интеграцию с GitHub (`Octokit`), запуск Docker (`Docker.DotNet`) и файловую систему.
- `Api`: входная точка, ASP.NET Core контроллеры, настройка DI, аутентификации JWT и сериализации JSON.

**Оценка изоляции:** Межслойные границы соблюдены строго. Контроллеры взаимодействуют исключительно со слоем Application через интерфейсы сервисов, репозитории скрыты за интерфейсами в Application. Прямого просачивания EF Core `DbContext` в контроллеры или слой Application не обнаружено.

---

## 5. Результаты проверки контроллеров и политики авторизации

Все контроллеры используют атрибут `[ApiController]` и единый базовый маршрут `api/[controller]`. Политика доступа различается по контроллерам и действиям:

### 5.1. `AuthController`
- **Политика на уровне контроллера:** атрибут `[Authorize]` на классе отсутствует.
- **Публичные действия (без `[Authorize]`):** `POST /api/auth/register`, `POST /api/auth/login` — доступны анонимно.
- **Защищённые действия (с `[Authorize]` на методах):** `GET /api/auth/me`, `PUT /api/auth/me/username`, `PUT /api/auth/me/password`, `GET /api/auth/me/github`, `PUT /api/auth/me/github`, `DELETE /api/auth/me/github`.
- **Статус-коды и защита:** `201 Created` при регистрации с заголовком `Location`, `200 OK` при логине/обновлении имени и статуса GitHub, `204 No Content` при смене пароля и удалении GitHub-токена. `ChangePassword` требует проверки текущего пароля (`VerifyPassword`).

### 5.2. `ProjectsController`
- **Политика на уровне контроллера:** `[Authorize]` (все действия требуют аутентификации).
- **Эндпоинты:** `GET /api/projects`, `GET /api/projects/{id:int}`, `GET /api/projects/owner/{ownerId:int}`, `POST /api/projects`, `PUT /api/projects/{id:int}`, `DELETE /api/projects/{id:int}`, `POST /api/projects/{projectId:int}/github/sync`.
- **Разграничение прав:** обычные пользователи видят только свои проекты (`UserId == currentUserId`). Администраторы видят все проекты. Метод `CheckProjectAccessAsync` проверяет существование проекта (`404`) и соответствие владельца (`403`).
- **Примечание:** поведение `GET /api/projects/owner/{ownerId}` при несуществующем `ownerId` (возврат `200 OK []`) вынесено в открытые вопросы (`[TD016-S1-QUESTION-002]`).

### 5.3. `CommitsController`
- **Политика на уровне контроллера:** `[Authorize]` (все действия требуют аутентификации).
- **Эндпоинты:** `GET /api/projects/{projectId:int}/commits`, `GET /api/projects/{projectId:int}/commits/{id:int}`, `POST /api/projects/{projectId:int}/commits`.
- **Проверка владения проектом:** выполняется перед любой операцией через `CheckProjectAccessAsync`. Проверяется уникальность хэша коммита в рамках проекта (`CommitHash` + `ProjectId`).

### 5.4. `AnalysesController`
- **Политика на уровне контроллера:** `[Authorize]` (все действия требуют аутентификации).
- **Фактические эндпоинты (3 операции):** `POST /api/projects/{projectId:int}/commits/{commitId:int}/analyses`, `GET /api/projects/{projectId:int}/commits/{commitId:int}/analyses`, `GET /api/projects/{projectId:int}/commits/{commitId:int}/analyses/{analysisId:int}` (отдельные HTTP-эндпоинты `/cancel`, `/results`, `/report` в контроллере отсутствуют; детали результатов и замечаний возвращаются в ответе `GET .../{analysisId:int}`).
- **Проверка контекста:** проверяется цепочка `Project -> Commit -> Analysis` (`commit.ProjectId == projectId`, `analysis.CommitId == commitId`). Возврат `409 Conflict` при попытке запуска повторного анализа при наличии активного (`Pending` или `Running`).

### 5.5. `AdminUsersController`
- **Политика на уровне контроллера:** `[Authorize(Roles = "Admin")]` (все действия доступны только роли `Admin`).
- **Эндпоинты:** `GET /api/admin/users`, `GET /api/admin/users/{id}`, `PUT /api/admin/users/{id}`, `PUT /api/admin/users/{id}/role`, `DELETE /api/admin/users/{id}`.
- **Защита:** запрет удаления самого себя (`adminId == id`), запрет понижения собственной роли и транзакционная защита последнего администратора.

---

## 6. Результаты проверки сервисов и бизнес-логики

### 6.1. `AnalysisService`
- Выполнение анализа структурировано через паттерн очереди: анализ создаётся в статусе `Pending` в БД, подхватывается фоновым процессом `AnalysisBackgroundService`.
- Блокировка и аренда: используется `TryAcquireNextPendingOrStaleAsync` с `FOR UPDATE SKIP LOCKED` и механизмом `ExecutionToken` (защита от зомби-воркеров и продление аренды через `HeartbeatUtc`).
- Изоляция сбоев (TD-002): сбои отдельных анализаторов перехватываются, оборачиваются в диагностические `AnalysisRuleViolation` со статусом `Warning` и не прерывают выполнение всего пайплайна.
- Отмена анализа: на уровне `AnalysisService` реализована внутренняя поддержка `CancellationToken`, проверка отмены между этапами анализаторов и фиксация статуса `Cancelled` (в том числе при потере аренды `LeaseRenewalStopReason.OwnershipLost`), однако отдельный HTTP-эндпоинт для инициации отмены клиентом в `AnalysesController` отсутствует.

### 6.2. `GitHubSyncService` & `GitHubSyncCoordinator` (TD-006)
- Синхронизация истории коммитов через GitHub API построена на потоковом обходе DAG (Directed Acyclic Graph) коммитов.
- Координатор `GitHubSyncCoordinator` использует семафоры и блокировку проекта в БД (`FOR NO KEY UPDATE`), предотвращая одновременную синхронизацию одного проекта несколькими запросами.
- Оптимистичная верификация контрольных точек (`LastSyncedCommitHash`, `LastSyncedRepositoryId`, `LastSyncedRepositoryHost`, `LastSyncedBranch`).

### 6.3. `UserService`
- Хэширование паролей выполняется с использованием алгоритма `BCrypt` (через интерфейс `IPasswordHasher`).
- Удаление и изменение роли пользователей защищены транзакцией `UserRepository.BeginAdminProtectionTransactionAsync` ([UserRepository.cs#L86-L119](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Backend/Infrastructure/Repository/UserRepository.cs#L86-L119)): в PostgreSQL выполняется `SELECT pg_advisory_xact_lock(hashtext('admin_role_protection'));` (для InMemory используется `SemaphoreSlim _adminLock`), после чего `CountAdminsAsync` ([UserRepository.cs#L80-L83](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Backend/Infrastructure/Repository/UserRepository.cs#L80-L83)) проверяет количество администраторов (`user.Role.Name == "Admin"`).

### 6.4. `ProjectService`
- **Наблюдение при обновлении проекта (`[TD016-S1-QUESTION-003]`):** при обновлении проекта (`ProjectService.UpdateAsync`, строки 45–67) допускается изменение `RepoUrl`. При этом контрольные точки синхронизации GitHub (`LastSyncedCommitHash`, `LastSyncedRepositoryId`, `LastSyncedRepositoryHost`, `LastSyncedBranch`) не сбрасываются, а существующие коммиты от старого репозитория остаются привязаны к проекту. В `GitHubSyncService.cs` (строки 85–89) проверка `checkpointMatches` сверяет `LastSyncedRepositoryHost`, `LastSyncedRepositoryId` и `LastSyncedBranch`, поэтому при смене репозитория ложного срабатывания чекпоинта не происходит. Поскольку в проектной документации отсутствует прямой запрет на смену `RepoUrl` у существующего проекта, данная особенность перенесена в открытые вопросы.

---

## 7. Результаты проверки репозиториев и доступа к данным

- Использование EF Core: запросы чтения оптимизированы с помощью `.AsNoTracking()`.
- Исключение N+1: для загрузки связанных данных (`Project.Commits`, `Commit.Analyses`, `Analysis.Results`) явно используются `.Include()` и `.ThenInclude()`.
- Проекции: там, где не требуется полная сущность, используются `.Select()` в DTO.
- Транзакции: для критических операций (создание анализа, регистрация, удаление пользователя, синхронизация) используются явные транзакции `BeginTransactionAsync(IsolationLevel.ReadCommitted)` с обработкой откатов при исключениях.
- Конфигурации сущностей (`Fluent API`): заданы точные ограничения длины полей (`HasMaxLength`), индексы для внешних ключей и уникальные составные индексы (например, `ProjectId + CommitHash`).

---

## 8. Результаты проверки авторизации и владения ресурсами

- Аутентификация: JWT Bearer токены, валидация подписи (HMAC-SHA256), срока действия (`ValidateLifetime = true`) и `ClockSkew = TimeSpan.Zero`.
- Авторизация по ролям: разделение на `Admin` и `User`.
- Проверка владения ресурсами (Resource Ownership):
  - Проекты: пользователь может оперировать только проектами, где `project.OwnerId == currentUserId`. Администратор имеет глобальный доступ.
  - Коммиты и анализы: доступ наследуется от родительского проекта через проверку цепочки `CheckProjectAccessAsync`.

---

## 9. Результаты проверки DTO и валидации

- Все входные DTO используют атрибуты `System.ComponentModel.DataAnnotations` (`[Required]`, `[StringLength]`, `[EmailAddress]`, `[RegularExpression]`).
- Автоматическая валидация перехватывает ошибки валидации модели и возвращает стандартизированный ответ RFC 9457 `400 Bad Request` с перечнем некорректных полей.
- Защита от Overposting: сущности домена никогда не принимаются напрямую из HTTP-запроса; каждый эндпоинт принимает строго специализированный DTO.

---

## 10. Результаты проверки конкурентности и целостности состояния

- **Очередь анализов (TD-007):** метод `TryAcquireNextPendingOrStaleAsync` применяет PostgreSQL-специфичный запрос `FOR UPDATE SKIP LOCKED`, что гарантирует эксклюзивный захват задачи одним воркером.
- **Синхронизация GitHub (TD-006):** распределённая координация через `IGitHubSyncCoordinator` и семафоры в памяти + `FOR NO KEY UPDATE` на строку проекта.
- **Workspace Isolation (TD-003):** каталог анализа изолируется токеном `{Storage:AnalysisPath}/{AnalysisId}/{ExecutionToken}`. Блокировка каталогов через `WorkspaceFileLockProvider` предотвращает конфликты очистки и одновременного доступа.
- **Риск каскадного удаления (`[TD016-S1-RISK-001]`):** удаление проекта `DELETE /api/projects/{id}` при наличии активного анализа удаляет записи `Commit` и `Analysis` каскадно (`OnDelete(DeleteBehavior.Cascade)`). При следующем вызове `RenewLeaseAsync` ([AnalysisService.cs#L1279-L1303](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Backend/Application/Services/AnalysisService.cs#L1279-L1303)) воркер получает `false`, фиксирует `LeaseRenewalStopReason.OwnershipLost` и штатно отменяет выполнение через `linkedCts.Cancel()`. Рабочий каталог на диске обнаруживается по файловой системе методом `DiscoverCandidateAnalysisIds` в `AnalysisCleanupService` ([AnalysisCleanupService.cs#L128-L221](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Backend/Infrastructure/Storage/AnalysisCleanupService.cs#L128-L221)) независимо от наличия записи `Analysis` в БД и удаляется после получения PostgreSQL advisory lock и файловой блокировки (`AnalysisCleanupBackgroundService`, интервал 1 минута).

---

## 11. Результаты проверки ошибок и ProblemDetails

- Соответствие RFC 9457: класс `ApiProblemDetails` и middleware `GlobalExceptionHandler` обеспечивают единый формат ответов об ошибках (`type`, `title`, `status`, `detail`, `instance`, `code`, `traceId`).
- Коды ошибок централизованы в `ApiErrorCodes`.
- `JwtBearerEvents` переопределены в `ApiJwtEvents` для возврата RFC 9457 при ошибках `401 Unauthorized` и `403 Forbidden`.

---

## 12. Результаты проверки внешних интеграций

### 12.1. GitHub API
- Токены пользователей шифруются в базе данных с использованием `IAesEncryptionService`.
- Запросы к GitHub API инкапсулированы в `GitHubClient`, обрабатывают лимиты запросов (Rate Limits) и ошибки `404 Not Found`/`401 Unauthorized`.
- Размер загружаемых архивов репозиториев ограничен (`MaxArchiveBytes = 250 MB`), исключены уязвимости Zip Slip и зарезервированные имена устройств Windows (`CON`, `PRN`, `AUX`, `NUL` и др.) в `RepositorySourceService`.

### 12.2. Docker Runner (DatabaseAnalyzer)
- Изоляция контейнеров: контейнеры запускаются с флагами `--network none`, правами `read-only` для монтируемого кода, лимитами по памяти и CPU, а также сбросом privileges (`CapDrop = ["ALL"]`).
- Взаимодействие через `Docker.DotNet` исключает инъекции команд shell.

---

## 13. Результаты проверки фоновой обработки

- `AnalysisBackgroundService`: периодический опрос очереди с плавным backoff при отсутствии задач. Корректная обработка завершения работы (`CancellationToken`).
- `AnalysisCleanupBackgroundService`: периодический поиск и перевод зависших задач (`Stale`) в статус `Failed`, очистка временных файлов на диске.
- Аренда задачи (Lease Renewal): во время выполнения длительного анализа фоновый таймер обновляет `HeartbeatUtc` в БД.

---

## 14. Результаты проверки логирования и диагностики

- Логирование использует структурированный `ILogger<T>`.
- Конфиденциальные данные (пароли, JWT-токены, токены GitHub) маскируются или исключаются из логов.
- Каждый запрос сопровождается `TraceIdentifier`, пробрасываемым в ответ ProblemDetails.

---

## 15. Проверка соответствия архитектурным решениям и MVP

- Реализация соответствует архитектурным решениям, принятым в `AGENTS.md`, `docs/Технический регламент.md` и `docs/architecture/`.
- Сохранены ключевые инварианты предметной области: целостность коммитов, неизменяемость результатов завершённого анализа, аудит действий пользователей.

---

## 16. Отдельные результаты регрессионной проверки TD-001, TD-002, TD-003, TD-006 и TD-007

| Идентификатор | Предмет технического долга | Статус в текущем коде | Подтверждение реализации |
|---|---|---|---|
| **TD-001** | Docker-изоляция DatabaseAnalyzer | **Подтверждено (Регрессия отсутствует)** | `DatabaseContainerRunner.cs`: read-only bind mount, tmpfs, network disabled, capabilities dropped. |
| **TD-002** | Изоляция сбоев анализаторов в пайплайне | **Подтверждено (Регрессия отсутствует)** | `AnalysisService.cs`: перехват исключений DatabaseAnalyzer, формирование `AnalysisRuleViolation` (Warning), пайплайн не прерывается. |
| **TD-003** | Изоляция рабочих каталогов и очистка осиротевших задач | **Подтверждено (Регрессия отсутствует)** | `AnalysisService.cs` + `WorkspaceFileLockProvider.cs`: токен `ExecutionToken` в пути директории, advisory locks, фоновая очистка в `AnalysisCleanupBackgroundService`. |
| **TD-006** | Синхронизация истории коммитов GitHub DAG | **Подтверждено (Регрессия отсутствует)** | `GitHubSyncService.cs` + `GitHubSyncCoordinator.cs`: потоковая обработка DAG, контрольные точки, блокировка проекта на время синка. |
| **TD-007** | Политика повторов и poison pill очереди анализов | **Подтверждено (Регрессия отсутствует)** | `AnalysisService.cs`: `TryAcquireNextPendingOrStaleAsync` с `FOR UPDATE SKIP LOCKED`, проверка лимита попыток (`AttemptCount >= MaxAttempts`). |

---

## 17. Результаты запуска сборки и тестов

- **Сборка решения (`CourseProject.slnx`):**
  - Команда: `dotnet build CourseProject.slnx --configuration Release`
  - Результат: **Успешно** (0 ошибок, 0 предупреждений).
- **Запуск тестов:**
  - Команда: `dotnet test CourseProject.slnx --configuration Release --no-build`
  - Результат: **Все 1533 теста пройдены успешно (100%)**, 0 ошибок, 0 пропущено.

---

## 18. Реестр подтверждённых дефектов

Подтверждённых дефектов уровня P0–P2 на этапе Stage 1 не выявлено.

---

## 19. Реестр архитектурных и эксплуатационных рисков

### [TD016-S1-RISK-001]
- **Название:** Каскадное удаление проекта во время активного анализа.
- **Серьёзность:** P3 (Ограничение конкурентности; воркер защищён механизмом аренды).
- **Компонент:** `Application` / `ProjectService.DeleteAsync`.
- **Файл:** `src/Backend/Application/Services/ProjectService.cs`, строки 69–81; `src/Backend/Infrastructure/Data/Configurations/ProjectConfiguration.cs`, строка 50; `src/Backend/Application/Services/AnalysisService.cs`, строки 1279–1303; `src/Backend/Infrastructure/Storage/AnalysisCleanupService.cs`, строки 128–221.
- **Подтверждённое поведение:** При вызове `DeleteAsync` не проверяется наличие активного анализа. Из-за `OnDelete(DeleteBehavior.Cascade)` запись `Analysis` удаляется из БД. При следующем обновлении аренды `RenewLeaseAsync` возвращает `false`, воркер распознаёт `LeaseRenewalStopReason.OwnershipLost` и штатно отменяет выполнение (`linkedCts.Cancel()`). В `AnalysisCleanupService.CleanupStaleDirectoriesAsync` список кандидатов `DiscoverCandidateAnalysisIds` строится по каталогам на диске независимо от наличия записи `Analysis` в БД; после получения `pg_try_advisory_lock` и `WorkspaceFileLockProvider.TryAcquireExclusive` состояние проверяется через `ReadAnalysisStateAsync` (`analysisExists = false`) и каталог очищается (`AnalysisCleanupBackgroundService`, интервал 1 минута). Блокировка workspace предотвращает лишь одновременный доступ к файлам, но не отменяет каскадное удаление в БД.
- **Рекомендация:** При необходимости немедленного запрета удаления активного проекта можно добавить проверку активных анализов с возвратом `409 Conflict`.

### [TD016-S1-RISK-002]
- **Название:** Отсутствие пагинации в эндпоинтах получения проектов.
- **Серьёзность:** P3 (Ограничение масштабирования, не нарушение MVP).
- **Компонент:** `Api` / `ProjectsController`.
- **Файл:** `src/Backend/Api/Controllers/ProjectsController.cs`, строки 30–57 (`GetAll`) и 114–144 (`GetByOwnerId`).
- **Описание:** Эндпоинты `GET /api/projects` и `GET /api/projects/owner/{ownerId}` возвращают полный несегментированный список проектов. Для текущего объёма MVP это не является дефектом, однако при росте числа проектов у пользователя или в системе (для администратора) может создавать нагрузку на память и сеть.
- **Рекомендация:** Рассмотреть добавление параметров пагинации `page` и `pageSize` на этапе развития.

---

## 20. Гипотезы и открытые вопросы

### [TD016-S1-QUESTION-001]
- **Вопрос:** Допустимость создания коротких хэшей коммитов через `POST /api/projects/{projectId}/commits`.
- **Файл:** `src/Backend/Application/DTOs/CreateCommitDto.cs`, строки 14–15.
- **Описание:** Валидация допускает хэш длиной от 7 до 64 символов (`[StringLength(64, MinimumLength = 7)]`). Автоматическая синхронизация GitHub всегда сохраняет полные 40-символьные (или 64-символьные) хэши. Если пользователь вручную создаст короткий хэш, последующая синхронизация GitHub не сможет сопоставить его с полным хэшем и создаст дубликат коммита.
- **Решение:** Требуется согласование с архитектором: либо требовать полный SHA-1 (40 hex-символов) при ручном создании коммита, либо автоматически дополнять/сопоставлять хэши.

### [TD016-S1-QUESTION-002] (перенесено из дефекта TD016-S1-001)
- **Вопрос:** Поведение `GET /api/projects/owner/{ownerId}` при несуществующем `ownerId`.
- **Файл:** `src/Backend/Api/Controllers/ProjectsController.cs`, строки 114–144.
- **Описание:** При запросе проектов несуществующего пользователя эндпоинт возвращает `200 OK []`. Поскольку в проектной документации отсутствует явное требование возвращать `404 Not Found` для несуществующего `ownerId`, данное поведение не классифицируется как дефект, но требует уточнения: нужно ли различать «пользователь не найден» (`404`) и «у пользователя нет проектов» (`200 []`).

### [TD016-S1-QUESTION-003] (перенесено из дефекта TD016-S1-002)
- **Вопрос:** Политика изменения `RepoUrl` в `PUT /api/projects/{id}` при наличии существующих коммитов и метаданных синхронизации GitHub.
- **Файл:** `src/Backend/Application/Services/ProjectService.cs`, строки 45–67; `src/Backend/Application/Services/GitHubSyncService.cs`, строки 58–89.
- **Описание:** При обновлении `RepoUrl` контрольные точки `LastSynced*` не сбрасываются, а старые коммиты остаются в проекте. Хотя `GitHubSyncService` корректно проверяет `checkpointMatches` по `LastSyncedRepositoryHost`, `LastSyncedRepositoryId` и `LastSyncedBranch` (ложного срабатывания чекпоинта не происходит), в проекте могут остаться коммиты от прежнего репозитория. Требуется решение архитектора: допустимо ли изменение `RepoUrl` или при его смене нужно сбрасывать метаданные синхронизации / запрещать смену при наличии коммитов.

---

## 21. Ограничения проведённого аудита

- Аудит проводился методом статического анализа кода, конфигураций, архитектурных документов и результатов автоматических тестов.
- В текущем окружении запуск реальных Docker-контейнеров внутри Docker runner и прямое сетевое обращение к реальному API `api.github.com` были промоделированы модульными и интеграционными тестами с моками.
- Проверка нагрузочной способности и реального поведения пула соединений PostgreSQL при пиковом числе параллельных воркеров вынесена в Stage 3.

---

## 22. Итоговое заключение Stage 1

Серверная часть CourseProject / NetScope находится в **высокой степени технической и архитектурной зрелости**:
1. Соблюдены границы Clean Architecture, контроллеры и сервисы строго следуют принципам разделения ответственности.
2. Реализованы надёжные механизмы изоляции очередей, распределённых блокировок и транзакционной целостности.
3. Полностью подтверждено отсутствие регрессий по ранее закрытым критическим техническим долгам TD-001, TD-002, TD-003, TD-006, TD-007.
4. Подтверждённых дефектов уровня P0–P2 не выявлено; зафиксированы 2 риска уровня P3 (`[TD016-S1-RISK-001]`, `[TD016-S1-RISK-002]`) и 3 открытых вопроса (`[TD016-S1-QUESTION-001]`, `[TD016-S1-QUESTION-002]`, `[TD016-S1-QUESTION-003]`).
5. Решение готово к переходу на **Stage 2 (Полнота и согласованность REST-эндпоинтов)** после согласования результатов пользователем.
