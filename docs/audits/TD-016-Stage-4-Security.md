# TD-016 Stage 4 – Независимый аудит безопасности (Cloudflare Security Audit Skill)

**Дата проведения:** 2026-11-27  
**Статус:** Завершён (`run_status: complete`, политика `source-only-no-os-sandbox`)  
**Профиль аудита:** `deep`  
**Базовый документ требований:** `docs/plans/TD-016-Комплексный-аудит-Backend-REST-API.md`  
**Предшествующие отчёты:** `TD-016-Stage-0-Preparation.md`, `TD-016-Stage-1-Backend-REST-API.md`, `TD-016-Stage-2-Endpoint-Completeness.md`, `TD-016-Stage-3-PostgreSQL-Performance.md`  

---

## 1. Общая информация и границы Stage 4

Аудит безопасности серверной части, анализаторов и Docker-инфраструктуры CourseProject / NetScope выполнен строго по методологии **Cloudflare `security-audit` skill** (профиль `deep`, все 6 фаз).

### Область аудита (Scope)
- **Включено:** `src/Backend/`, `src/Analyzers/` (включая `Database.Runner`), `tests/Backend.Tests/`, `tests/DatabaseAnalyzer.Runner.Tests/`, `tests/SecurityAnalyzer.Tests/`, `infrastructure/docker/Dockerfile*`, `docker-compose.yml`.
- **Исключено (`out_of_scope`):** нереализованные клиенты (`src/Cli`, `src/Desktop`, `src/Bot`), отсутствующие в репозитории CI-файлы (`.github/workflows/*`).

---

## 2. Проверенная Git-ревизия и состояние рабочей директории

- **Ветка:** `main`
- **Коммит:** `a061bb8a388ef1848b64548d0a03178bb83162d7` (`a061bb8` — `fix: rename CSharp to CSharpAnalyzer`)
- **Состояние рабочей директории:** отслеживаемые файлы не изменялись; артефакты skill перенесены в `docs/audits/TD-016-Stage-4-Security/` (`run-metadata.json`, `architecture.md`, `coverage-ledger.json`, `findings.json`, `REPORT.md`, `FINDINGS-DETAIL.md`, `NEEDS-VALIDATION.md`, `HARDENING.md`, `VERIFICATION-RECORDS.json`).

---

## 3. Методика аудита (Cloudflare Security Audit Skill)

Выполнены все 6 фаз статического аудита skill с разделением ролей через независимых `research`-субагентов:
1. **Фаза 1 (Reconnaissance):** 2 параллельных агента разведки (`1a-1b`, `1c-1d`), сформированы `architecture.md` и `coverage-ledger.json` (11 начальных единиц, +1 единица Wave 2 = **12 единиц**).
2. **Фаза 2 (Coverage-led Hunting):** 4 параллельных hunter-агента Wave 1 + 1 **Coverage Critic** + 1 hunter-агент Wave 2 (`hunter-analyzers-wave2`). Всего получено **24 уникальных кандидата**.
3. **Фаза 3 (Candidate Validation):** 4 свежих независимых верификатора проверили все 24 кандидата.
4. **Фаза 4 (Structured Output & Validation):** сформирован `findings.json`; по результатам финального пересмотра 24 записи распределены как `0 confirmed`, `13 needs_validation`, `11 rejected` (`SUPPLY-01` и `SUPPLY-02` переведены в hardening-рекомендации в `HARDENING.md`). Оба JSON-валидатора (`validate-coverage-ledger.cjs`, `validate-findings.cjs`) пройдены с 0 ошибок.
5. **Фаза 5 (Independent Record Verification):** свежий независимый верификатор проверил исходные 15 записей `needs_validation` в `findings.json` — все получили вердикт `verified` (отражено в `VERIFICATION-RECORDS.json`).
6. **Фаза 6 (Target-neutral Reporting):** сформированы отчёты без раскрытия секретов и без запуска target-controlled кода.

### Статус выполнения и ограничение (`source-only-no-os-sandbox`)
- Статический аудит Stage 4 и независимые проверки кандидатов **проведены полностью**.
- **Динамическая проверка не проводилась** из-за отсутствия на ОС Windows OS-enforced sandbox (пустое окружение с allowlist, запрет внешней сети, read-only монтирование кода/toolchain, жёсткие лимиты CPU/RAM/disk). Отсутствие динамической проверки не отменяет проведённого статического аудита, но означает, что ни одна запись не переведена в `confirmed` с runtime-воспроизведением.

---

## 4. Модель угроз и границы доверия

Основные границы доверия:
1. Неаутентифицированный HTTP-клиент -> `AuthController` (`register`, `login`), `/health`.
2. Аутентифицированный `User` -> `ProjectsController`, `CommitsController`, `AnalysesController`, `AuthController.me/*`.
3. `Admin` -> `AdminUsersController` (`[Authorize(Roles = "Admin")]`).
4. Backend -> PostgreSQL 17 (очередь `FOR UPDATE SKIP LOCKED`, advisory locks).
5. Backend -> GitHub REST API (`https://api.github.com/`) с расшифрованным AES-256-GCM PAT.
6. Недоверенные файлы репозитория (ZIP -> workspace) -> `CSharpAnalyzer`, `SqlAnalyzer`, `SecurityAnalyzer`, `DatabaseAnalyzer`.
7. Backend -> Docker CLI -> `Database.Runner` (`uid 10001`, `--network none`, `--read-only`).

---

## 5. JWT, аутентификация и авторизация

- `Program.cs:198-221`: включена проверка `Issuer`, `Audience`, `Lifetime`, `IssuerSigningKey` (HS256), `PasswordHasher` использует BCrypt (`PasswordHasher.cs:5-16`).
- `AuthController.cs`: `register` (`:43`) и `login` (`:83`) анонимны с `AuthPolicy`; остальные действия защищены `[Authorize]`.
- **Выявленный кандидат (`auth-jwt-no-revocation-stale-claims`, `needs_validation`):** JWT выпускаются на 1 час (+5 мин `ClockSkew` по умолчанию) без `jti` или `security_stamp`; `ApiJwtEvents.TokenValidated` (`:39`) не проверяет состояние пользователя/роли в БД, поэтому при смене пароля, понижении роли или удалении пользователя старый JWT остаётся действительным до истечения срока.

---

## 6. IDOR и Resource Ownership

- Все 13 операций в `ProjectsController` (7), `CommitsController` (3) и `AnalysesController` (3) защищены `[Authorize]`, проверяют `project.OwnerId == userId || User.IsInRole("Admin")` и иерархию `commit.ProjectId == projectId`, `analysis.CommitId == commitId`. Прямого IDOR не выявлено (единица `covered`).

---

## 7. Административные операции

- `AdminUsersController` (`:18`) защищён `[Authorize(Roles = "Admin")]`, блокирует самоудаление и самопонижение (`Forbid()`), а `UserService` + `UserRepository.BeginAdminProtectionTransactionAsync` (`:86-119`) сериализуют операции через `pg_advisory_xact_lock(hashtext('admin_role_protection'))` (единица `covered`).

---

## 8. Секреты и GitHub PAT

- `AesGcmTokenEncryptionService` (`:11-122`) использует AES-256-GCM с 12-байтным CSPRNG nonce и проверкой 32-байтного ключа на старте (`Program.cs:80-102`).
- Секреты не хранятся в `appsettings*.json` (`.env` в `.gitignore`).
- Отклонённые кандидаты (`CAND-SE-003`, `CAND-SE-004`, `CAND-SE-005`) описаны в разделе 20.

---

## 9. CORS и Rate Limiting

- `ApiCorsPolicies` (`:55-266`) реализует строгий fail-closed разбор `Cors:AllowedOrigins`.
- `AuthPolicy` (`TokenBucketRateLimiter`, `Program.cs:145-196`) защищает `register`, `login`, `me/password`. `UseForwardedHeaders` (`:276-279`) по умолчанию доверяет только loopback (`CAND-SE-001` отклонён).
- **Кандидаты (`needs_validation`):** `CAND-SE-002` (отсутствие rate limiting и `GlobalLimiter` на `me/username`, `me/github`, `github/sync`, `analyses`), `HUNTER-W2-003` (незащищённый `GET /health` вызывает `DatabaseHealthCheck.CanConnectAsync`).

---

## 10. Валидация и злоупотребление API

- Все входные DTO используют `DataAnnotations`, `CreateCommitDto` дополнительно реализует `IValidatableObject`.
- **Кандидаты (`needs_validation`):** `ZW-001` (`GitHubRepositoryReference.Parse` самостоятельно не проверяет схему и допустимые символы; известные MVC-пути используют `CreateProjectDto`/`UpdateProjectDto` с проверкой `https://`, однако regex допускает percent-encoded символы `%2F`/`%2E%2E`, а обход DTO-валидации через другой путь не установлен), `HUNTER-W2-001` (потенциальный риск патологического времени выполнения регулярного выражения `ConnectionStringPasswordRegex` в `Sec001HardcodedSecretsRule.cs:20-22` и `Sec002PlaintextConfigSecretsRule.cs:17-19`: соседние lazy-wildcards `=.*?;.*?`, отсутствуют `matchTimeout` и `RegexOptions.NonBacktracking`; catastrophic backtracking динамически не доказан), `HUNTER-W2-002` (отсутствие собственных лимитов размера файла и файловых проверок `ReparsePoint` в `CSharpAnalyzer`, `SqlAnalyzer`, `SecurityAnalyzer` при наличии upstream-ограничений распаковщика: 50 MiB на файл, 250 MiB суммарно, пропуск Unix symlinks; реальное ресурсное воздействие на 50 MiB не проверено), `HUNTER-W2-004` (в `GitHubSyncService.SyncAsync` отсутствует прикладной лимит страниц/коммитов, накопление DAG происходит в памяти, а `allExpandedParents` повторно обходится на каждой странице; фактическое ресурсное воздействие не воспроизведено), `QUEUE-03` (отсутствие квоты очереди на пользователя/систему).

---

## 11. Безопасность ZIP-архивов

- В `RepositorySourceService.cs` реализованы защита от Zip Slip (`ValidateRawArchivePath`, `GetSafeDestinationPath`), пропуск Unix-симлинков (`0xA000`), потоковые лимиты (50 MiB архив/файл, 250 MiB распаковка), запрет зарезервированных имён Windows и проверка коллизий NFC/регистра.
- **Кандидат (`needs_validation`):** `ZW-003` — подтверждённое поведение: записи каталогов (`item.IsDirectory`) в `RepositorySourceService.ExtractArchiveInternalCoreAsync` (`:431-502`) обходят счётчик `MaxFileCount` (10 000). Число записей не является математически неограниченным (архив ограничен 50 МБ), но возможное ресурсное воздействие по памяти и inode требует динамической проверки.

---

## 12. Изоляция рабочих каталогов и TD-003

- Рабочие каталоги изолируются по `{Storage:AnalysisPath}/{AnalysisId}/{ExecutionToken}`, защищены PostgreSQL advisory locks и `WorkspaceFileLockProvider`, очищаются `AnalysisCleanupService`.
- **Кандидат (`needs_validation`):** `ZW-005` — ранний выход в `RepositorySourceService.CleanupInternalAsync` (`:604-605`) при отсутствии `fullTargetDir` пропускает немедленную очистку `containerExchangeDir` (очищается позже через `AnalysisCleanupService`).

---

## 13. Docker Runner и ADR-001

- `DockerCliProcessAdapter` (`:156-222`) запускает `Database.Runner` с `--network none`, `--read-only`, `--cap-drop ALL`, `--security-opt no-new-privileges`, `--user 10001:10001`, `--cpus 1.0`, `--memory 512m`, `--pids-limit 64`, `--tmpfs /tmp:rw,noexec,nosuid,size=64m` и таймаутом 60 с. `DatabaseContainerRunner` проверяет `output.json` (не симлинк, <= 10 МБ, совпадение `AnalysisId` и `ExecutionToken`).
- **Кандидаты (`needs_validation`):** `DOCKER-01` (`docker-compose.yml:68,71-72` монтирует `/var/run/docker.sock` с `group_add: ["${DOCKER_GID:-0}"]`), `DOCKER-02` (`DatabaseContainerRunner.cs:133-137` задаёт права `0777` на `out/` с неатомарной проверкой `output.json`).

---

## 14. HTTP-ответы, исключения и логирование

- `ApiProblemDetails` (`:109-233`) маскирует ошибки 5xx в RFC 9457, `ApiJwtEvents` не логирует токены, `AnalysisService.SanitizeErrorMessage` (`:1471+`) очищает стектрейсы, пути и PAT.

---

## 15. Отказоустойчивость внешних интеграций

- **Кандидат (`needs_validation`):** `QUEUE-01` — `TryAcquireNextPendingOrStaleAsync` (`AnalysisRepository.cs:190`) ограничивает пропуск `maxCandidateSkips = 50`, а `AnalysisBackgroundService` обрабатывает очередь последовательно в одном цикле (возможен HoL-блокинг).

---

## 16. Supply chain и CI/CD

- **Hardening-рекомендации (перенесены в `HARDENING.md`, `rejected` как самостоятельные уязвимости в `findings.json`):** `SUPPLY-01` (отсутствуют `packages.lock.json` и `--locked-mode`), `SUPPLY-02` (использование изменяемых Docker-тегов `postgres:17`, `netscope-database-analyzer:latest`, `mcr.microsoft.com/dotnet/*:10.0` без `@sha256:`). Без скомпрометированного NuGet-источника или реестра контейнеров сами по себе не образуют подтверждённую уязвимость.

---

## 17. Проверка тестового покрытия безопасности

В `tests/Backend.Tests/` присутствуют модульные и интеграционные тесты (`CorsPolicyTests`, `RateLimitingTests`, `JwtLoggingTests`, `TokenEncryptionServiceTests`, `RepositoryArchiveSecurityTests`, `AdminProtectionTests`, `PostgreSqlAdvisoryLockIntegrationTests`, `PostgreSqlWorkspaceCleanupIntegrationTests` и др.). В рамках политики `source-only-no-os-sandbox` тесты повторно не запускались (в Stage 0 зафиксировано 1533/1533).

---

## 18. Реестр подтверждённых уязвимостей и рисков

Подтверждённых уязвимостей (`confirmed`) в этом прогоне: **0** (ввиду отсутствия OS-enforced sandbox для динамической/локальной проверки).

---

## 19. Отдельный реестр `needs_validation` (13 записей, без severity)

### Кандидаты, требующие динамической или инфраструктурной проверки (12)
1. `auth-jwt-no-revocation-stale-claims` — отсутствие отзыва/валидации в БД для выданных JWT при смене пароля, роли или удалении.
2. `CAND-SE-002` — отсутствие rate limiting на `me/username`, `me/github`, `github/sync`, `analyses`.
3. `DOCKER-01` — монтирование `/var/run/docker.sock` с `group_add: ["${DOCKER_GID:-0}"]`.
4. `DOCKER-02` — права `0777` на `out/` и неатомарное чтение `output.json`.
5. `HUNTER-W2-001` — потенциальный риск патологического времени выполнения `ConnectionStringPasswordRegex` (`Sec001`, `Sec002`, соседние lazy-wildcards без `matchTimeout` и `RegexOptions.NonBacktracking`; catastrophic backtracking динамически не доказан).
6. `HUNTER-W2-002` — отсутствие собственных лимитов размера и файловой проверки `ReparsePoint` в `CSharpAnalyzer`, `SqlAnalyzer`, `SecurityAnalyzer` (upstream-ограничения распаковщика: 50 MiB на файл, 250 MiB суммарно, пропуск Unix symlinks).
7. `HUNTER-W2-003` — неаутентифицированный и нелимитированный `/health` -> `CanConnectAsync`.
8. `HUNTER-W2-004` — отсутствие прикладного лимита страниц/коммитов, накопление DAG в памяти и повторный обход `allExpandedParents` на каждой странице в `GitHubSyncService.SyncAsync` (ресурсное воздействие не воспроизведено).
9. `QUEUE-01` — `maxCandidateSkips = 50` и последовательный воркер в `AnalysisBackgroundService`.
10. `QUEUE-03` — отсутствие лимита глубины очереди на пользователя/систему.
11. `ZW-001` — `GitHubRepositoryReference.Parse` самостоятельно не проверяет схему и символы; известные MVC-пути используют DTO с проверкой `https://`, но regex допускает percent-encoded символы (`%2F`/`%2E%2E`), а обход DTO-валидации через другой путь не установлен.
12. `ZW-003` — обход `MaxFileCount` записями каталогов в `RepositorySourceService`.

### Эксплуатационные риски (1)
13. `ZW-005` — пропуск немедленной очистки `containerExchangeDir` при раннем выходе в `CleanupInternalAsync` (компенсируется периодической фоновой очисткой `AnalysisCleanupService`).

---

## 20. Отклонённые кандидаты (`rejected`, 11 записей)

- `auth-jwt-key-length-unvalidated` (ключ задаётся в deployment, `JwtBearer` проверяет длину при работе).
- `CAND-SE-001` (`UseForwardedHeaders` по умолчанию доверяет только loopback).
- `CAND-SE-003` (`DatabaseAnalyzer` пробрасывает только стандартные `OperationCanceledException`).
- `CAND-SE-004` (`EncryptedGitHubToken` не доступен клиенту; подмена требует записи в БД).
- `CAND-SE-005` (`SanitizeErrorMessage` маскирует пути, PAT и стектрейсы).
- `DOCKER-03` (`Program.cs:59-63` требует `Docker:ExchangeVolume`, fallback недостижим).
- `QUEUE-02` (`Status = 1` всегда пишется с ненулевым `LeaseExpiration`).
- `SUPPLY-01` (перенесено в hardening-рекомендации `HARDENING.md`: отсутствие lockfile само по себе не доказывает уязвимость).
- `SUPPLY-02` (перенесено в hardening-рекомендации `HARDENING.md`: изменяемый Docker-тег сам по себе не доказывает уязвимость).
- `ZW-002` (`CommitHash` валидируется regex или берётся из `api.github.com`).
- `ZW-004` (`Link` header читается только от доверенного `https://api.github.com/`).

---

## 21. Рекомендации

### 21.1. Возможные исправления после подтверждения риска (`needs_validation`)
1. Добавить `matchTimeout` / `RegexOptions.NonBacktracking` в `ConnectionStringPasswordRegex` (`Sec001`, `Sec002`) после подтверждения патологического времени выполнения (`HUNTER-W2-001`).
2. Добавить собственные лимиты размера файла, файловую проверку `ReparsePoint` и проверку корневого пути в `CSharpAnalyzer`, `SqlAnalyzer`, `SecurityAnalyzer` (`HUNTER-W2-002`).
3. Добавить `GlobalLimiter` / per-user лимиты на `me/username`, `me/github`, `github/sync`, `analyses` и кэширование/rate limit на `/health` (`CAND-SE-002`, `HUNTER-W2-003`).
4. Учитывать каталоги в `MaxFileCount` (`ZW-003`), убрать ранний выход до очистки `containerExchangeDir` (`ZW-005`) и экранировать `Owner`/`Name` в `GitHubRepositoryReference` (`ZW-001`).
5. Оптимизировать `GitHubSyncService` (инкрементальный `allExpandedParents`, лимит страниц — `HUNTER-W2-004`) и рассмотреть квоту очереди (`QUEUE-01`, `QUEUE-03`).

### 21.2. Hardening-рекомендации (полезны независимо от подтверждения уязвимости)
- См. `docs/audits/TD-016-Stage-4-Security/HARDENING.md`: `SUPPLY-01` (`packages.lock.json` + `--locked-mode`), `SUPPLY-02` (`@sha256:` для Docker-образов), явный `ClockSkew` / `ValidAlgorithms`, проверка длины `Jwt:Key`, ограничение `AllowedHosts`, вывод `EfCoreModelInspector` / `LocalProcessAdapter` из production-сборки, очистка `.locks/`, запрет ведущего `-` в `ValidImageRegex`, AAD и обнуление ключа в `AesGcmTokenEncryptionService`.

### 21.3. Архитектурные и продуктовые вопросы для владельца проекта
1. Требуется ли механизм отзыва выданных JWT (`security_stamp` / `jti`) при смене пароля, понижении роли или удалении пользователя (`auth-jwt-no-revocation-stale-claims`)?
2. Допустимо ли монтирование `/var/run/docker.sock` (`DOCKER-01`) и права `0777` на `out/` (`DOCKER-02`) в целевой инфраструктуре развёртывания, или планируется изоляция через Docker socket proxy / rootless Docker?
3. Существуют ли не-MVC пути создания/обновления `Project.RepoUrl`, минующие `CreateProjectDto`/`UpdateProjectDto` (`ZW-001`)?

---

## 22. Покрытие и пропущенные области

- **Всего единиц в `coverage-ledger.json`:** 12 (`3 covered`, `8 candidate`, `1 out_of_scope` для будущих клиентов, `0 blocked`, `0 deferred`).
- **Семантика статусов ledger:** согласно `validate-coverage-ledger.cjs` (`:527, :563`), статус `candidate` означает любую единицу с непустым массивом `result_fingerprints` (включая единицы `NuGet Packages & Docker Base Images` и `GitHub PAT & Error Logging`, где все связанные записи в `findings.json` получили статус `rejected`), а статус `covered` требует `result_fingerprints: []`.

---

## 23. Ограничения достоверности

- Отсутствие OS-enforced sandbox не позволило выполнить локальную эксплуатацию и тесты внутри изоляции; ни одна запись не переведена в `confirmed`.
- Поверхности реального reverse proxy, Docker-хоста и registry provenance не видны из репозитория.

---

## 24. Итоговое заключение

Статический аудит безопасности Stage 4 завершён по всем 6 фазам Cloudflare `security-audit` skill с независимой проверкой кандидатов (динамическая проверка не выполнялась из-за отсутствия OS-enforced sandbox). Базовые механизмы защиты (IDOR/ownership, last-admin lock, AES-256-GCM, Zip Slip, Docker `--network none`/`--read-only`/`--cap-drop ALL`, advisory locks) реализованы на высоком уровне. Итог: `0 confirmed`, `13 needs_validation` (12 кандидатов + 1 эксплуатационный риск `ZW-005`), `11 rejected` (9 отклонённых + 2 hardening-рекомендации `SUPPLY-01`, `SUPPLY-02`, вынесенные в `HARDENING.md`).
