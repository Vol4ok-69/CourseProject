# TD-016 Stage 6 – Аудит тестового покрытия и качества тестов

**Дата проведения:** 2026-11-27  
**Статус:** Завершён  
**Базовый документ требований:** `docs/plans/TD-016-Комплексный-аудит-Backend-REST-API.md`  
**Детализированные артефакты:** `docs/audits/TD-016-Stage-6-Test-Coverage/` (`coverage-matrix.md`, `test-quality-review.md`, `findings.json`, `run-metadata.json`)  

---

## 1. Общая информация и статус Stage 6

Проведён независимый аудит тестового покрытия и качества тестов решения `CourseProject.slnx`. Проинвентаризированы все 6 тестовых проектов, выполнен актуальный прогон `dotnet test`, проверены уровни тестирования, качество assertions и регрессионное покрытие.

---

## 2. Проверенная Git-ревизия и состояние рабочей директории

- **Ветка:** `main`, **коммит:** `a061bb8a388ef1848b64548d0a03178bb83162d7` (`fix: rename CSharp to CSharpAnalyzer`).
- **Рабочая директория:** отслеживаемые файлы, исходный код, конфигурации, миграции и тесты не изменялись; добавлены только неотслеживаемые артефакты аудита Stage 0–6.

---

## 3. Методика и источники

- Изучены `docs/audits/test-suite-evaluation.md`, отчёты Stage 0–5, `.csproj` всех 6 тестовых проектов, `PostgreSqlFixture.cs`, `ApiWebApplicationFactory.cs` и тестовые классы `tests/`.
- Аудит выполнен с участием 3 специализированных `research`-агентов и фактическим запуском `dotnet test CourseProject.slnx --configuration Release`.

---

## 4. Инвентаризация тестовых проектов

Все проекты используют `.NET 10` (`net10.0`), `xunit 2.9.3`, `xunit.runner.visualstudio 3.1.4`, `Microsoft.NET.Test.Sdk 17.14.1` и `coverlet.collector 6.0.4`:

| Проект | Классов | `[Fact]` / `[DockerFact]` | `[Theory]` | Всего тест-кейсов | Уровни |
|---|---:|---:|---:|---:|---|
| `tests/Backend.Tests/Backend.Tests.csproj` | 45 | 726 / 0 | 79 (372 cases) | **1098** | Unit, Service, HTTP Integration (`WebApplicationFactory`), Database Integration (`Testcontainers postgres:17`) |
| `tests/DatabaseAnalyzer.Tests/DatabaseAnalyzer.Tests.csproj` | 13 | 178 / 0 | 4 (12 cases) | **190** | Unit, Component |
| `tests/DatabaseAnalyzer.Runner.Tests/DatabaseAnalyzer.Runner.Tests.csproj` | 8 | 78 / 4 | 6 (23 cases) | **105** | Unit, Component, Docker Integration |
| `tests/SecurityAnalyzer.Tests/SecurityAnalyzer.Tests.csproj` | 6 | 72 / 0 | 0 | **72** | Unit |
| `tests/SqlAnalyzer.Tests/SqlAnalyzer.Tests.csproj` | 3 | 44 / 0 | 0 | **44** | Unit |
| `tests/CSharpAnalyzer.Tests/CSharpAnalyzer.Tests.csproj` | 5 | 21 / 0 | 1 (3 cases) | **24** | Unit |
| **Итого** | **80** | **1119 / 4** | **90 (410 cases)** | **1533** | — |

**Сверка с `docs/audits/test-suite-evaluation.md`:** в детальных таблицах документа сохранились устаревшие цифры (`Backend.Tests` — `999` вместо `1098`, `CSharpAnalyzer.Tests` — `17` вместо `24`, `SecurityAnalyzer.Tests` — `56` вместо `72`, `ApiPipelineIntegrationTests.cs` — `33` вместо `45`).

---

## 5. Актуальные результаты тестовых запусков

Команда: `dotnet test CourseProject.slnx --configuration Release` (SDK `10.0.401`, код возврата: `0`):
- **Всего:** 1533 | **Пройдено:** 1530 | **Ошибок:** 0 | **Пропущено:** 3.
- Пропущены 3 `[DockerFact]` теста в `DatabaseAnalyzer.Runner.Tests.DockerContainerIntegrationTests` (`DockerContainer_RunsWithPinnedDigest_Successfully`, `DockerContainer_ExecutesSafely_WithIsolationFlags`, `DockerContainer_FullRoslynExtractionAndDatabaseAnalysis_SucceedsWithDB01Finding`) из-за недоступности целевого Docker-образа/демона (`TD016-S6-012`).
- Сбор line/branch coverage (`coverlet.collector`) в данном прогоне не выполнялся.

---

## 6. Результаты анализа покрытия критичных сценариев

Из 14 критичных групп сценариев (подробно в `docs/audits/TD-016-Stage-6-Test-Coverage/coverage-matrix.md`):
- **Покрыты достаточно:** 6 групп (миграции/CHECK, конфликты `23505`/savepoint, потеря COMMIT ACK, HTTP-конвейер, жизненный цикл очереди/lease/Poison Pill, безопасность архивов/TD-003).
- **Имеют слабую гарантию / качество:** 5 групп (`FOR UPDATE SKIP LOCKED` на 2+ задачах, негативные assertions при 403/409, таймаут `HttpClient`/whitespace DTO, `ProblemDetails` тавтологии/Content-Type, однопоточный `WorkspaceFileLockProviderTests`).
- **Не покрыты на HTTP-уровне:** 1 сценарий (`AdminUsersController` HTTP-мутации `TD016-S6-001`; unit/service тесты существуют).
- **Не требуется / открытый вопрос:** 1 сценарий (`AnalysisStatus.Cancelled` `TD016-S6-002` — 0 переходов в `src/Backend/`, зарезервированный enum).
- **Ограничение инфраструктуры:** 1 группа (3 Docker-теста `TD016-S6-012`).

---

## 7. PostgreSQL и HTTP Integration

- **PostgreSQL:** используется реальный `postgres:17` через `Testcontainers.PostgreSql` и `Respawn` (`PostgreSqlFixture.cs`), а не SQLite/InMemory. Покрыты миграции, `CK_Analyses_*`, `23505` savepoints, `pg_advisory_lock`, `FOR UPDATE` на `Projects` и wire-протокольный сброс COMMIT ACK (`PostgreSqlLoopbackFaultProxy`). Пробел: не проверен пропуск заблокированной строки при 2+ задачах в `FOR UPDATE SKIP LOCKED` (`TD016-S6-004`).
- **HTTP (`WebApplicationFactory<Program>`):** `ApiPipelineIntegrationTests` (45 тестов), `CommitValidationAndNormalizationIntegrationTests` (12), `GitHubSyncExceptionMappingIntegrationTests` (7), `DatabaseInitializationAndHealthTests` (2) реально проходят middleware (JWT, CORS, RateLimiting, ExceptionHandler). Пробел: мутации `AdminUsersController` (`PUT`/`DELETE`) тестируются только unit/SQLite-тестами (`TD016-S6-001`).

---

## 8. Авторизация, конкурентность и очередь

- JWT (401/200), IDOR (403/404), PAT isolation и last-admin (`TaskCompletionSource` барьер в `AdminProtectionTests`) проверены хорошо, но часть контроллерных и интеграционных тестов не проверяет `Times.Never` / неизменность БД после 403/409 (`TD016-S6-006`).
- В `AnalysisQueueTests:111-129` `Task.WhenAll` вызывается на одном `AppDbContext`, а `ScenarioD` в `PostgreSqlWorkspaceCleanupIntegrationTests:286-319` фактически последователен (`TD016-S6-011`).
- Статус `AnalysisStatus.Cancelled` не используется в переходах `src/Backend/` (0 совпадений в коде, зарезервированное значение enum — `TD016-S6-002`, `NO_GAP_FOUND`).

---

## 9. GitHub и внешние зависимости

- `GitHubServiceTests`, `GitHubRateLimitParserTests` и `GitHubSyncDagTraversalTests` подробно проверяют пагинацию, `Retry-After`, SSRF `Link`, DAG и `23505`.
- Пробелы: не проверены malformed JSON и 404/пустой `default_branch` (`TD016-S6-003`), а тест таймаута бросает `HttpRequestException` вместо `TaskCanceledException` (`TD016-S6-007`).

---

## 10. DTO, ProblemDetails и безопасность

- `DtoValidationAndAnalysisErrorTests`, `CommitValidationAndNormalizationIntegrationTests` и `PaginationTests` покрывают обязательные поля, длины, SHA-1/SHA-256, `DateTimeKind.Unspecified`, `pageSize` clamp `100` и санитизацию ошибок.
- Пробелы: не проверены extra JSON fields (`TD016-S6-005`) и пробельные строки для ряда DTO (`TD016-S6-007`); `GitHubSyncExceptionMappingIntegrationTests` проверяет `application/json` вместо `application/problem+json` (`TD016-S6-009`).
- Zip Slip, Zip Bomb и `ExecutionToken` в `RepositoryArchiveSecurityTests` и `PostgreSqlWorkspaceCleanupIntegrationTests` покрыты глубоко.

---

## 11. Регрессионное покрытие TD-001–TD-015

- **TD-001**: `DatabaseAnalyzer.Tests` + `DatabaseAnalyzer.Runner.Tests` (3 Docker-теста пропущены в текущем прогоне — `TD016-S6-012`).
- **TD-002**: `AnalysisServiceQueueTests`, `AnalysisLeaseRenewalLifecycleTests`, `AnalysisResultsConsistencyTests`.
- **TD-003**: `PostgreSqlWorkspaceCleanupIntegrationTests`, `PostgreSqlAdvisoryLockIntegrationTests`, `RepositoryArchiveSecurityTests`, `ContainerRunnerSecurityTests`.
- **TD-006**: `GitHubSyncDagTraversalTests`, `GitHubHistoryClassifierTests`, `PostgreSqlGitHubSyncIntegrationTests`.
- **TD-007**: `AnalysisRetryPolicyTests`, `AnalysisServiceRetryQueueTests`, `PostgreSqlQueueOperationsTests`, `PostgreSqlCommitAckLossIntegrationTests`.
- **TD-015**: `ApiPipelineIntegrationTests`, `ProblemDetailsAndEnumTests`, `CorsPolicyTests`, `RateLimitingTests`.

---

## 12. Качество тестов и слабые гарантии

Подробный разбор приведён в `docs/audits/TD-016-Stage-6-Test-Coverage/test-quality-review.md` (тавтологии в `ProblemDetailsAndEnumTests:319-440` и `RateLimitingTests:19-86`, последовательный `ScenarioD`, общий `DbContext` в `AnalysisQueueTests`, скрытые `[DockerFact]` пропуски).

---

## 13. Подтверждённые пробелы и замечания (12 записей)

Полный реестр в `docs/audits/TD-016-Stage-6-Test-Coverage/findings.json`:
- **P2 (3):** `TD016-S6-001` (`AdminUsersController` HTTP-мутации), `TD016-S6-006` (недостаточная гарантия негативных assertions), `TD016-S6-012` (ограничение среды: 3 `[DockerFact]` теста пропущены).
- **P3 (7):** `TD016-S6-003`, `TD016-S6-004`, `TD016-S6-007`, `TD016-S6-008`, `TD016-S6-009`, `TD016-S6-010` (копирование лямбд в unit-тестах при наличии интеграционных тестов), `TD016-S6-011`.
- **P4 (2):** `TD016-S6-002` (`NO_GAP_FOUND` / открытый вопрос по `AnalysisStatus.Cancelled`), `TD016-S6-005` (extra JSON fields).

---

## 14. Ограничения аудита

- 3 Docker-теста (`[DockerFact]`) не исполнялись в текущем окружении (это ограничение среды, не дефект приложения; тесты нельзя считать ни проваленными, ни проверенными).
- Числовой сбор line/branch coverage (`coverlet.collector`) не запускался; оценка основана на инвентаризации, запуске `dotnet test` и анализе assertions.

---

## 15. Рекомендации и приоритеты

1. **P2:** Добавить HTTP-интеграционные тесты мутаций `AdminUsersController`, усилить негативные assertions (`Times.Never` / проверка БД) и обеспечить обязательное выполнение `[DockerFact]` в целевой CI-среде с Docker.
2. **P3–P4:** Заменить копии лямбд в `ProblemDetailsAndEnumTests` / `RateLimitingTests`, добавить тесты на пропуск строки в `FOR UPDATE SKIP LOCKED`, malformed JSON / 404 ветки GitHub, `TaskCanceledException`, whitespace DTO и extra JSON fields.

---

## 16. Итоговое заключение

Тестовая база NetScope (1533 тест-кейса) демонстрирует **высокий уровень инженерной зрелости** в части PostgreSQL-интеграции (`Testcontainers`, `Respawn`, wire-протокол TCP ACK loss, advisory locks) и безопасности архивов/изоляции (`TD-003`, `TD-006`, `TD-007`). Зафиксированы 12 записей (`3` P2, `7` P3, `2` P4, включая 1 запись `NO_GAP_FOUND` по зарезервированному `AnalysisStatus.Cancelled`), отражающие точечные пробелы HTTP-интеграции, качества assertions и пропуск 3 `[DockerFact]` тестов в текущем окружении.
