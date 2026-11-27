# TD-016 Stage 3 – Аудит PostgreSQL и производительности

**Дата проведения:** 2026-11-27  
**Статус:** Завершён (скорректирован по результатам независимого ревью)  
**Объект аудита:** Слой доступа к данным (`Domain`, `Infrastructure`, миграции EF Core) и БД `courseproject-db`  
**Базовый документ требований:** `docs/plans/TD-016-Комплексный-аудит-Backend-REST-API.md`  
**Предшествующие отчёты:**  
- `docs/audits/TD-016-Stage-0-Preparation.md`  
- `docs/audits/TD-016-Stage-1-Backend-REST-API.md`  
- `docs/audits/TD-016-Stage-2-Endpoint-Completeness.md`  

---

## 1. Общая информация и границы Stage 3

Настоящий отчёт представляет результаты независимого технического аудита схемы PostgreSQL, конфигураций EF Core, миграций, индексов, запросов, транзакционной модели и конкурентных блокировок системы CourseProject / NetScope.

### Границы этапа
- **Включено:** сопоставление модели EF Core, миграций и фактической схемы `courseproject-db`; проверка ключей, ограничений целостности и каскадных удалений; аудит индексов (включая `API-023`); анализ запросов EF Core, пагинации, транзакций и блокировок; безопасный `EXPLAIN` на `courseproject-db`.
- **Исключено:** изменение исходного кода, конфигураций, миграций, индексов, тестов и данных БД (DDL/DML не выполнялись).

---

## 2. Проверенная Git-ревизия и состояние рабочей директории

- **Ветка:** `main`
- **Коммит:** `a061bb8a388ef1848b64548d0a03178bb83162d7` (`a061bb8` — `fix: rename CSharp to CSharpAnalyzer`, 2026-11-26 22:50:31 +0700)
- **Состояние рабочей директории:** отслеживаемые файлы не изменялись; присутствуют неотслеживаемые отчёты `docs/audits/TD-016-Stage-0-Preparation.md`, `docs/audits/TD-016-Stage-1-Backend-REST-API.md`, `docs/audits/TD-016-Stage-2-Endpoint-Completeness.md` и настоящий отчёт `docs/audits/TD-016-Stage-3-PostgreSQL-Performance.md`.

---

## 3. Методика и исследованные источники

- **Конфигурации и миграции EF Core:**
  - `src/Backend/Infrastructure/Infrastructure.csproj`
  - `src/Backend/Infrastructure/Data/Configurations/*.cs` (`UserConfiguration`, `RoleConfiguration`, `ProjectConfiguration`, `CommitConfiguration`, `AnalysisConfiguration`, `AnalysisResultConfiguration`)
  - `src/Backend/Infrastructure/Data/Migrations/*.cs` (7 миграций + `AppDbContextModelSnapshot.cs`)
- **Репозитории и сервисы:**
  - `src/Backend/Infrastructure/Repository/*.cs` (`UserRepository`, `RoleRepository`, `ProjectRepository`, `CommitRepository`, `AnalysisRepository`)
  - `src/Backend/Application/Services/*.cs` (`AnalysisService`, `GitHubSyncService`, `UserService`, `ProjectService`)
  - `src/Backend/Api/Controllers/*.cs` (`AnalysesController`, `ProjectsController`, `CommitsController`, `AdminUsersController`)
- **База данных `courseproject-db` (через MCP `courseproject-db`, исключительно read-only):** `database_overview`, `list_tables`, `list_indexes`, `execute_sql` (`SELECT` и `EXPLAIN`).
- **Документация:** `docs/plans/API-Stabilization-Plan.md` (поиск `API-023`).

---

## 4. Окружение PostgreSQL и ограничения доступа

- **Целевая платформа (.NET):** `net10.0`, `Microsoft.EntityFrameworkCore` `10.0.12`, `Npgsql.EntityFrameworkCore.PostgreSQL` `10.0.3` (`src/Backend/Infrastructure/Infrastructure.csproj:10-17`).
- **Сервер PostgreSQL (`courseproject-db`):** `PostgreSQL 17.11 (Debian 17.11-1.pgdg13+2)`, не реплика, `max_connections = 100`, `current_connections = 7`, `active_connections = 1`.
- **Назначение БД `courseproject-db`:** по доступным безопасным сведениям без раскрытия строк подключения невозможно достоверно установить, является ли экземпляр `courseproject-db` целевой БД работающего приложения, локальной разработческой или устаревшей/демонстрационной средой. Факт доступности через MCP не считается доказательством использования в production.
- **Объём данных в `courseproject-db` (`pg_stat_user_tables`):**
  - `Roles`: 2 строки (`seq_scan = 8`, `idx_scan = 13`)
  - `Users`: 1 строка (`seq_scan = 8`, `idx_scan = 6`)
  - `Projects`, `Commits`, `Analyses`, `AnalysisResults`: **0 строк** (на таблице `Analyses` зафиксировано `idx_scan = 7054` от предыдущего опроса очереди).
- **Ограничение:** из-за отсутствия данных в `Projects`, `Commits`, `Analyses` и `AnalysisResults` нагрузочные измерения (`EXPLAIN ANALYZE`) не проводились, так как на пустых таблицах они не репрезентативны.

---

## 5. Сопоставление EF Core, миграций и фактической схемы

### 5.1. Сверка миграций (`src/Backend/Infrastructure/Data/Migrations` vs `__EFMigrationsHistory`)

| Migration ID | Есть в коде | Есть в `__EFMigrationsHistory` | Результат |
|---|---|---|---|
| `20260911074307_InitialCreate` | Да | Да | Применена |
| `20261028152950_AddUniqueIndexes` | Да | Да | Применена |
| `20261030064123_AddDurableAnalysisQueue` | Да | Да | Применена |
| `20261031081432_AddAnalysisErrorMessage` | Да | Да | Применена |
| `20261103101757_AddUserGitHubIntegration` | Да | Да | Применена |
| `20261122150614_AddAnalysisRetryAndPoisonPillQueue` | Да | **Нет** | **Не применена** (`[TD016-S3-001]`) |
| `20261124090814_AddCommitProvenanceAndProjectSyncCheckpoint` | Да | **Нет** | **Не применена** (`[TD016-S3-001]`) |

### 5.2. Сопоставление таблиц, столбцов, ограничений и индексов (`courseproject-db`)

| Таблица | Проверенные свойства в `courseproject-db` (`list_tables`) | Расхождения с кодом/миграциями |
|---|---|---|
| `Roles` | `Id` (`integer`, PK), `Name` (`text`, NOT NULL), `IX_Roles_Name` (UNIQUE) | Расхождений не выявлено |
| `Users` | `Id` (`integer`, PK), `Username` (`text`, NOT NULL, UNIQUE `IX_Users_Username`), `PasswordHash` (`text`, NOT NULL), `RoleId` (`integer`, FK `ON DELETE RESTRICT`, `IX_Users_RoleId`), `EncryptedGitHubToken` (`text`, NULL), `GitHubTokenCreatedAt` (`timestamptz`, NULL), `GitHubUsername` (`varchar(255)`, NULL) | Расхождений не выявлено |
| `Projects` | `Id` (`integer`, PK), `Name` (`text`, NOT NULL), `RepoUrl` (`text`, NOT NULL), `OwnerId` (`integer`, FK `ON DELETE RESTRICT`, `IX_Projects_OwnerId`, UNIQUE `IX_Projects_OwnerId_Name`) | Отсутствуют 5 столбцов из миграции `20261124090814`: `LastSyncedAt`, `LastSyncedBranch`, `LastSyncedCommitHash`, `LastSyncedRepositoryHost`, `LastSyncedRepositoryId` |
| `Commits` | `Id` (`integer`, PK), `ProjectId` (`integer`, FK `ON DELETE CASCADE`, `IX_Commits_ProjectId`, UNIQUE `IX_Commits_ProjectId_CommitHash`), `CommitHash` (`text`, NOT NULL), `CommitDate` (`timestamptz`, NOT NULL) | Отсутствуют `IsVerified` (`boolean`) и `ParentHashes` (`text[]`) из миграции `20261124090814` |
| `Analyses` | `Id` (`integer`, PK), `CommitId` (`integer`, FK `ON DELETE CASCADE`), `Status` (`integer`, NOT NULL), `StartedAt`/`CompletedAt`/`LeaseExpiration` (`timestamptz`, NULL), `CreatedAt` (`timestamptz`, default `CURRENT_TIMESTAMP`), `ExecutionToken` (`uuid`, NULL), `ErrorMessage` (`text`, NULL), `IX_Analyses_CommitId_CreatedAt`, `IX_Analyses_Status_CreatedAt` | Отсутствуют `AttemptCount`, `MaxAttempts`, `NextAttemptAt`, check constraints `CK_Analyses_AttemptCount`, `CK_Analyses_MaxAttempts` и индекс `IX_Analyses_Status_NextAttemptAt_CreatedAt` из миграции `20261122150614` |
| `AnalysisResults` | `Id` (`integer`, PK), `AnalysisId` (`integer`, FK `ON DELETE CASCADE`, `IX_AnalysisResults_AnalysisId`), `FilePath`/`Rule`/`Message` (`text`, NOT NULL), `LineNumber`/`Severity`/`AnalyzerType` (`integer`, NOT NULL), `Recommendation` (`text`, NULL) | Расхождений не выявлено |

**Ограничение проверки:** проверка через MCP `list_tables` охватывает таблицы, столбцы, PK, FK, `ON DELETE` и индексы; подробное сопоставление всех триггеров или пользовательских типов вне 6 основных таблиц не требовалось. Команда `dotnet ef migrations has-pending-model-changes` (Stage 0) сравнивает только код и `AppDbContextModelSnapshot.cs`, не проверяя реальную БД.

---

## 6. Каталог таблиц, ключей и ограничений целостности

Целостность связей обеспечивается на уровне PostgreSQL внешними ключами (`Users.RoleId -> Roles.Id` `RESTRICT`, `Projects.OwnerId -> Users.Id` `RESTRICT`, `Commits.ProjectId -> Projects.Id` `CASCADE`, `Analyses.CommitId -> Commits.Id` `CASCADE`, `AnalysisResults.AnalysisId -> Analyses.Id` `CASCADE`) и уникальными индексами (`IX_Roles_Name`, `IX_Users_Username`, `IX_Projects_OwnerId_Name`, `IX_Commits_ProjectId_CommitHash`).

---

## 7. Аудит индексов

| Таблица | Индекс | Столбцы | Уникальный | Назначение / Используемые запросы |
|---|---|---|---|---|
| `Roles` | `PK_Roles` | `Id` | Да | Первичный ключ |
| `Roles` | `IX_Roles_Name` | `Name` | Да | Поиск роли по имени (`UserRepository.CountAdminsAsync`) |
| `Users` | `PK_Users` | `Id` | Да | Первичный ключ |
| `Users` | `IX_Users_Username` | `Username` | Да | Логин, проверка уникальности при регистрации/смене имени |
| `Users` | `IX_Users_RoleId` | `RoleId` | Нет | FK к `Roles` |
| `Projects` | `PK_Projects` | `Id` | Да | Первичный ключ |
| `Projects` | `IX_Projects_OwnerId` | `OwnerId` | Нет | FK к `Users`, префикс дублируется `IX_Projects_OwnerId_Name` (`API-023`) |
| `Projects` | `IX_Projects_OwnerId_Name` | `OwnerId, Name` | Да | Уникальность имени проекта у владельца + выборки по `OwnerId` |
| `Commits` | `PK_Commits` | `Id` | Да | Первичный ключ |
| `Commits` | `IX_Commits_ProjectId` | `ProjectId` | Нет | FK к `Projects`, префикс дублируется `IX_Commits_ProjectId_CommitHash` (`API-023`) |
| `Commits` | `IX_Commits_ProjectId_CommitHash` | `ProjectId, CommitHash` | Да | Дедупликация коммитов при синке и ручном создании |
| `Analyses` | `PK_Analyses` | `Id` | Да | Первичный ключ |
| `Analyses` | `IX_Analyses_CommitId_CreatedAt` | `CommitId, CreatedAt` | Нет | История анализов коммита (`AnalysesController.GetByCommitId`) |
| `Analyses` | `IX_Analyses_Status_CreatedAt` | `Status, CreatedAt` | Нет | Базовая выборка очереди (`idx_scan = 7054` в `courseproject-db`) |
| `Analyses` | `IX_Analyses_Status_NextAttemptAt_CreatedAt` *(в миграции 6, отсутствует в БД)* | `Status, NextAttemptAt, CreatedAt` | Нет | Очередь с учётом отложенных повторных попыток (`TD-007`) |
| `AnalysisResults` | `PK_AnalysisResults` | `Id` | Да | Первичный ключ |
| `AnalysisResults` | `IX_AnalysisResults_AnalysisId` | `AnalysisId` | Нет | FK к `Analyses`, загрузка результатов анализа |

---

## 8. Анализ запросов EF Core и N+1

1. **`AsNoTracking()`:** применяется во всех методах чтения (`AnalysisRepository.cs:56,79,96`, `CommitRepository.cs:22,36,81,104`, `ProjectRepository.cs:20,28`, `RoleRepository.cs:13`, `UserRepository.cs:37`).
2. **`Include()` / N+1:** связанные данные подгружаются явно через `.Include()` и `.ThenInclude()`, циклов с отдельными SQL-запросами на каждую сущность в контроллерах/репозиториях не обнаружено.
3. **Загрузка `Results` в списке анализов (`[TD016-S3-RISK-001]`):** в `AnalysisRepository.GetByCommitIdAsync` (`src/Backend/Infrastructure/Repository/AnalysisRepository.cs:107-113`) выполняется `.Include(analysis => analysis.Results)` без `AsSplitQuery()` (единый JOIN). Это продиктовано действующим публичным контрактом `AnalysisDto`, который включает `IReadOnlyList<AnalysisResultDto> Results` (`AnalysesController.GetByCommitId`, строка 174, вызывает `MapToDto`). При большом количестве замечаний на один анализ загрузка до 20 анализов с результатами может существенно увеличивать объём ответа, однако на пустой БД измеримых доказательств деградации нет.

---

## 9. Анализ пагинации

- **`ProjectsController` (`GetAll`, `GetByOwnerId`):** пагинация отсутствует. `ProjectRepository.GetAllAsync` (`:17-23`) и `ProjectRepository.GetByOwnerIdAsync` (`:25-31`) возвращают полную коллекцию без `Skip`/`Take` (`[TD016-S3-RISK-003]`).
- **`CommitsController`, `AnalysesController`, `AdminUsersController`:** используется `PagedResultDto<T>` с отдельным `CountAsync` и выборкой `.Skip(offset).Take(pageSize)` (`page >= 1`, `pageSize` 1..100).
- **Детерминированность сортировки (пересмотр `[TD016-S3-002]`):** повторная проверка кода подтвердила, что `CommitRepository.GetByProjectIdAsync` (`CommitRepository.cs:48-49`: `.OrderByDescending(c => c.CommitDate).ThenByDescending(c => c.Id)`), `AnalysisRepository.GetByCommitIdAsync` (`AnalysisRepository.cs:109-110`: `.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)`), `ProjectRepository.cs:21` и `UserRepository.cs:49` **имеют детерминированную сортировку**. Ранее зафиксированное замечание `[TD016-S3-002]` **снято как опровергнутое кодом**.

---

## 10. Транзакции и обработка конфликтов

- **Очередь анализов (`TD-007`):** фактический raw SQL в `AnalysisRepository.TryAcquireNextPendingOrStaleAsync` (`src/Backend/Infrastructure/Repository/AnalysisRepository.cs:207-230`):
  ```sql
  SELECT "Id", "Status", "AttemptCount", "MaxAttempts", "LeaseExpiration"
  FROM "Analyses"
  WHERE ("Status" = 0
     OR ("Status" = 1 AND ("LeaseExpiration" IS NULL OR "LeaseExpiration" < clock_timestamp())))
    AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= clock_timestamp())
    AND (@skippedCount = 0 OR "Id" != ALL(@skippedCandidateIds))
    AND NOT EXISTS (
        SELECT 1 FROM pg_locks
        WHERE locktype = 'advisory'
          AND classid = hashtext('netscope_analysis_workspace')
          AND objid = "Analyses"."Id"
          AND objsubid = 2
          AND granted = true
    )
  ORDER BY "CreatedAt" ASC
  LIMIT 1
  FOR UPDATE SKIP LOCKED
  ```
  Метод выполняется в транзакции `IsolationLevel.ReadCommitted` с циклом пропуска заблокированных кандидатов (`maxCandidateSkips = 50`) и проверкой Poison Pill (`AttemptCount >= MaxAttempts`).
- **Защита последнего администратора:** `UserRepository.BeginAdminProtectionTransactionAsync` (`:86-119`) использует `SELECT pg_advisory_xact_lock(hashtext('admin_role_protection'));`.
- **Уникальные конфликты:** обрабатываются уникальными индексами (`IX_Users_Username`, `IX_Projects_OwnerId_Name`, `IX_Commits_ProjectId_CommitHash`) с маппингом в `409 Conflict`.

---

## 11. Блокировки и конкурентные сценарии

- **TD-001 / TD-002:** изоляция выполняется на уровне Docker/процесса и не создаёт блокировок в PostgreSQL.
- **TD-003:** очистка каталогов использует `pg_try_advisory_lock(hashtext('netscope_analysis_workspace'), @analysisId)` (`AnalysisCleanupService.cs:159-161`).
- **TD-006:** координация синхронизации GitHub защищена `GitHubSyncCoordinator` и строковой блокировкой проекта.
- **TD-007:** очередь защищена `FOR UPDATE SKIP LOCKED`, проверкой `pg_locks` и ограничением `AttemptCount >= MaxAttempts`.

---

## 12. Результаты EXPLAIN / EXPLAIN ANALYZE

- **Упрощённый `EXPLAIN` на `courseproject-db`:**
  ```sql
  EXPLAIN SELECT "Id", "CommitId", "Status", "CreatedAt"
  FROM "Analyses" WHERE "Status" = 0 ORDER BY "CreatedAt" LIMIT 1 FOR UPDATE SKIP LOCKED;
  ```
  План: `Limit -> LockRows -> Index Scan using "IX_Analyses_Status_CreatedAt"`.
- **Ограничение для фактического SQL `TD-007`:** выполнить `EXPLAIN` для полного запроса `TryAcquireNextPendingOrStaleAsync` (`AnalysisRepository.cs:210-226`) на `courseproject-db` **невозможно**, так как в текущей схеме `courseproject-db` отсутствуют столбцы `AttemptCount`, `MaxAttempts`, `NextAttemptAt` и индекс `IX_Analyses_Status_NextAttemptAt_CreatedAt`. Таким образом, приведённый выше `EXPLAIN` характеризует только упрощённую выборку и **не подтверждает** производительность полного механизма `TD-007` (включая подзапрос к `pg_locks` и фильтр `NextAttemptAt`).

---

## 13. Проверка API-023

- **Исходная формулировка (`docs/plans/API-Stabilization-Plan.md:99,420`):** *"Избыточные префиксные индексы в схеме PostgreSQL"* (`IX_Projects_OwnerId` и `IX_Commits_ProjectId`), отложено на Этап 7 (`DEFERRED`) ради обратной совместимости схемы.
- **Технический анализ:** с точки зрения структуры B-tree `IX_Projects_OwnerId` (`"OwnerId"`) является левым префиксом `IX_Projects_OwnerId_Name` (`"OwnerId", "Name"`), а `IX_Commits_ProjectId` (`"ProjectId"`) — левым префиксом `IX_Commits_ProjectId_CommitHash` (`"ProjectId", "CommitHash"`).
- **Ограничения оценки:**
  - нулевое количество строк в `courseproject-db` не доказывает отсутствие накладных расходов на поддержку этих индексов при будущих операциях `INSERT`/`UPDATE`/`DELETE`;
  - статистика `pg_stat_user_indexes` (`index_scans = 0`) зависит от периода накопления и сброса статистики и не доказывает ненужность индекса.
- **Вывод:** исходный статус `DEFERRED` обоснован; решение об удалении префиксных индексов следует принимать только после наполнения тестовой БД репрезентативными данными и анализа реальной нагрузки.

---

## 14. Реестр подтверждённых дефектов и рисков

| ID | Категория | Приоритет | Суть | Источник |
|---|---|---|---|---|
| **[TD016-S3-001]** | Расхождение окружения / Риск | **P2** *(пересмотрено с P1)* | В `courseproject-db` не применены 2 последние миграции (`20261122150614_*` и `20261124090814_*`); отсутствуют столбцы `AttemptCount`, `MaxAttempts`, `NextAttemptAt`, `LastSynced*`, `IsVerified`, `ParentHashes` и индекс `IX_Analyses_Status_NextAttemptAt_CreatedAt`. Поскольку назначение `courseproject-db` (целевая или устаревшая среда) не подтверждено, влияние на эксплуатацию не установлено. **Рекомендация:** применять миграции только после подтверждения целевой среды, проверки миграционного плана и резервного копирования. | `__EFMigrationsHistory` vs `src/Backend/Infrastructure/Data/Migrations/` |
| **[TD016-S3-002]** | **Снято (опровергнуто кодом)** | — | Первоначальное утверждение об отсутствии вторичной сортировки опровергнуто: в `CommitRepository.cs:48-49` и `AnalysisRepository.cs:109-110` присутствует `.ThenByDescending(x => x.Id)`. | `CommitRepository.cs:48-49`, `AnalysisRepository.cs:109-110` |
| **[TD016-S3-RISK-001]** | Архитектурный риск | **P2** | `AnalysisRepository.GetByCommitIdAsync` (`:108`) загружает `.Include(a => a.Results)` (единый JOIN) для всей страницы (до 20 анализов) согласно текущему контракту `AnalysisDto`. При большом числе замечаний объём данных может кратно расти; измеримых доказательств на пустой БД нет. **Рекомендация:** оценить необходимость `Results` в списочном контракте или `AsSplitQuery()` при наличии репрезентативных данных. | `AnalysisRepository.cs:108`, `AnalysesController.cs:174` |
| **[TD016-S3-RISK-002]** | Гипотеза / Рекомендация | **P3** | На таблице `Commits` отсутствует составной индекс `("ProjectId", "CommitDate" DESC)` для сортировки коммитов проекта при пагинации без `Sort`. Требует проверки на репрезентативном объёме данных. | `CommitConfiguration.cs`, `CommitRepository.cs:48` |
| **[TD016-S3-RISK-003]** | Архитектурный риск | **P3** | Отсутствие пагинации для `GET /api/projects` и `GET /api/projects/owner/{ownerId}` (`ProjectRepository.GetAllAsync` `:17-23`, `GetByOwnerIdAsync` `:25-31`). При значительном росте числа проектов выборка не ограничена. Измерения отсутствуют. **Рекомендация:** оценить необходимость пагинации при уточнении целевого масштаба. | `ProjectRepository.cs:17-31`, `ProjectsController.cs:30,114` |

---

## 15. Рекомендации по индексам и запросам

1. Подтвердить статус и назначение `courseproject-db`; при необходимости синхронизации схемы подготовить резервную копию и проверить миграционный план для `20261122150614_*` и `20261124090814_*`.
2. После наполнения тестовой БД репрезентативными данными выполнить `EXPLAIN (ANALYZE, BUFFERS)` для фактического SQL из `TryAcquireNextPendingOrStaleAsync`, списочной выборки `GetByCommitIdAsync` (оценка `Include(Results)`) и оценки необходимости `API-023` / `("ProjectId", "CommitDate" DESC)`.
3. При уточнении ожидаемого масштаба числа проектов на пользователя оценить введение пагинации в `GET /api/projects` и `GET /api/projects/owner/{ownerId}`.

---

## 16. Ограничения достоверности результатов

- Основные таблицы (`Projects`, `Commits`, `Analyses`, `AnalysisResults`) в `courseproject-db` пусты (0 строк), поэтому нагрузочные замеры (`EXPLAIN ANALYZE`, `BUFFERS`) не выполнялись.
- Из-за отставания схемы `courseproject-db` на 2 миграции не удалось выполнить даже статический `EXPLAIN` для полного SQL-запроса очереди `TD-007` (`TryAcquireNextPendingOrStaleAsync`).

---

## 17. Итоговое заключение Stage 3

Архитектура работы с PostgreSQL в кодовой базе построена корректно (`AsNoTracking`, детерминированная пагинация коммитов и анализов через `ThenByDescending(Id)`, `FOR UPDATE SKIP LOCKED`, advisory locks). Зафиксировано отставание схемы `courseproject-db` на 2 миграции (`[TD016-S3-001]`, P2 — окружение/риск), подтверждены риски загрузки `Results` в списке анализов (`[TD016-S3-RISK-001]`, P2) и отсутствия пагинации проектов (`[TD016-S3-RISK-003]`, P3), а также дана сбалансированная оценка `API-023` (`DEFERRED`). Первоначальное замечание `[TD016-S3-002]` снято как опровергнутое исходным кодом.
