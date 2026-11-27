# TD-016 Stage 5 – Комплексный аудит OpenAPI и API-контрактов

**Дата проведения:** 2026-11-27  
**Статус:** Завершён  
**Объект аудита:** Публичный контракт REST API CourseProject / NetScope и спецификация `swagger.json`  
**Базовый документ требований:** `docs/plans/TD-016-Комплексный-аудит-Backend-REST-API.md`  
**Детализированные артефакты:** `docs/audits/TD-016-Stage-5-OpenAPI/` (`REPORT.md`, `endpoint-contract-matrix.md`, `dto-schema-matrix.md`, `problem-details-contract.md`, `client-generation-assessment.md`, `findings.json`, `run-metadata.json`, `openapi.json`, `verification-records.json`)  

---

## 1. Резюме и итоговый статус

Проведён независимый аудит публичного контракта REST API (27 HTTP-операций, 19 публичных DTO, 3 перечисления, RFC 9457 `ProblemDetails`, авторизация, пагинация, генерация клиентов и версионирование). Выявлено **13 замечаний**, из них **5 подтверждённых расхождений (`confirmed_defect`, `medium`)** между актуальным исходным кодом и `swagger.json` (устаревшие схемы `GitHubSyncResultDto` и `CreateCommitDto.CommitHash`, отсутствие `409 Conflict` в `SyncGitHub`, неполная схема `ProblemDetails` без `code`/`traceId`/`errors` и с неверным `application/json`, наследование глобального `Bearer` на публичных `register`/`login`). Поскольку на момент аудита реализованных клиентов нет, severity оценён как `medium`.

---

## 2. Цель и границы Stage 5

- **Включено:** сверка 27 HTTP-операций с `swagger.json`, проверка DTO/enum/nullable/сериализации, RFC 9457, Security Schemes, пагинации, оценка генерации клиентов (.NET 10 C# и TypeScript) и стратегии версионирования.
- **Исключено:** изменение кода, DTO, маршрутов, настроек Swashbuckle, тестов и БД; повторный аудит безопасности Stage 4.

---

## 3. Git-ревизия и состояние рабочей директории

- **Ветка:** `main`, **коммит:** `a061bb8a388ef1848b64548d0a03178bb83162d7` (`fix: rename CSharp to CSharpAnalyzer`).
- **Рабочая директория:** отслеживаемые файлы не изменялись; присутствуют отчёты Stage 0–4 и новые артефакты Stage 5.

---

## 4. Изученные источники

- `src/Backend/Api/Program.cs`, `src/Backend/Api/Controllers/*.cs`, `src/Backend/Api/Common/*.cs`
- `src/Backend/Application/DTOs/*.cs`, `src/Backend/Domain/Enums/*.cs`
- `swagger.json` (в корне репозитория) и отчёты Stage 0–4.

---

## 5. Методика и ограничения

Использованы 4 независимых аудитора (Route Parity, DTO/Serialization, Errors/Security Contract, Client Generation/Evolution) и 1 независимый верификатор (Independent Verifier), подтвердивший все 7 ключевых расхождений.
- **Ограничение:** генераторы клиентского кода (`NSwag`, `Kiota`, `orval`) и живой HTTP-сервер не запускались — оценка генерации выполнена статически по `swagger.json` (`OpenAPI 3.0.4`).

---

## 6. Источник актуальной OpenAPI-спецификации

- Проанализирован `swagger.json` в корне репозитория (`OpenAPI 3.0.4`, `Swashbuckle.AspNetCore` `10.2.3`, обновлён владельцем с `http://localhost:5000/swagger/v1/swagger.json` 2026-11-27).
- Установлено, что `swagger.json` отстаёт от текущего исходного кода в 3 местах (`TD016-S5-001`, `TD016-S5-002`, `TD016-S5-003`).

---

## 7. Сверка 27 операций

- **26 MVC-действий** присутствуют в `swagger.json` с совпадающими HTTP-методами, путями и параметрами маршрута.
- **`GET /health`** (`Program.cs:313`) отсутствует в `swagger.json` (`TD016-S5-012`), так как `MapHealthChecks` не регистрирует `ApiDescription` для `AddEndpointsApiExplorer()` — это ожидаемое поведение, а не баг маршрутизации.
- Полная таблица приведена в `docs/audits/TD-016-Stage-5-OpenAPI/endpoint-contract-matrix.md`.

---

## 8. DTO и схемы запросов/ответов

Проверено **19 публичных DTO** (подробности в `dto-schema-matrix.md`):
- **`TD016-S5-001` (`medium`):** `GitHubSyncResultDto` в `swagger.json:2223-2239` содержит только `added` и `skipped`, отсутствуют `alreadyCurrent` и `historyClassification` (`GitHubSyncResultDto.cs:24-30`).
- **`TD016-S5-002` (`medium`):** `CreateCommitDto.CommitHash` в `swagger.json:2163-2168` имеет `maxLength: 40` и не содержит `pattern` (`CreateCommitDto.cs:14-15` задаёт `64` и `\A[0-9a-fA-F]{7,64}\z`).
- **`TD016-S5-008` (`medium`):** правило `IValidatableObject` для `CreateCommitDto.CommitDate` (запрет `DateTimeKind.Unspecified`) не отражено в `swagger.json`.
- **`TD016-S5-011` (`low`):** `UpdateUserRoleDto.RoleId` имеет `minimum: 1`, но отсутствует в `required`.

---

## 9. Nullable и сериализация

- `Program.cs:229-233` использует `camelCase` и `JsonStringEnumConverter`.
- **`TD016-S5-007` (`medium`):** `AddSwaggerGen` (`Program.cs:236`) не вызывает `SupportNonNullableReferenceTypes()`, из-за чего non-nullable ссылочные поля в выходных DTO (`ProjectDto.Name/RepoUrl`, `UserDto.Username/RoleName`, `AuthResponseDto.AccessToken`, `CommitDto.CommitHash`, `AnalysisDto.Results`, `AnalysisResultDto.*`, `PagedResultDto<T>.Items`) генерируются с `"nullable": true` и без `required`. При этом важно разделять два эффекта: `SupportNonNullableReferenceTypes()` отвечает за `nullable: false`, тогда как включение non-nullable свойств в `required` в Swashbuckle 10.2.3 может требовать отдельной настройки (например, `NonNullableAsRequired` или schema filter — точное поведение без запуска генерации зафиксировано как ограничение рекомендации).

---

## 10. Enum

- `AnalysisStatus`, `Severity`, `AnalyzerType` сериализуются строками (PascalCase) через `JsonStringEnumConverter` и корректно описаны в `swagger.json` как `type: "string", enum: [...]`.
- `HistoryClassification` отсутствует в `swagger.json` вследствие устаревания `GitHubSyncResultDto` (`TD016-S5-001`).

---

## 11. HTTP-статусы

- **`TD016-S5-003` (`medium`):** `POST /api/projects/{projectId}/github/sync` (`ProjectsController.cs:331,367-384`) объявляет и возвращает `409 Conflict`, но `409` отсутствует в `swagger.json:1842-1903`.
- **`TD016-S5-010` (`low`):** гарантированные ответы `429` на `register`, `login`, `me/password` не описывают заголовок `Retry-After` (`Program.cs:153-157`). Ответ `500 Internal Server Error` потенциально возвращается middleware (`UseExceptionHandler()`) на необработанных исключениях, но явно задекларирован только в `SetGitHubToken`; механически добавлять `500` ко всем операциям не требуется (достаточно единой документации поведения middleware).

---

## 12. ProblemDetails

- **`TD016-S5-004` (`medium`):** `ApiProblemDetails.cs` всегда добавляет в ответы поля `code` и `traceId` (и `errors` через `ValidationProblemDetails` при `400`), а также выставляет `Content-Type: application/problem+json`. В `swagger.json:2276-2302` схема `ProblemDetails` не содержит `code`, `traceId`, `errors`, а все ошибки используют `application/json` (подробнее в `problem-details-contract.md`).

---

## 13. Авторизация и Security Schemes

- **`TD016-S5-005` (`medium`):** `Program.cs:268-271` добавляет глобальный `SecurityRequirement` (`Bearer`) без operation filter. В результате анонимные операции `POST /api/auth/register` и `POST /api/auth/login` (`swagger.json:672-811`) наследуют требование `Bearer` без переопределения `"security": []`.

---

## 14. Пагинация, фильтрация и сортировка

- В `AdminUsersController`, `CommitsController`, `AnalysesController` параметры `page=1` и `pageSize=20` описаны с корректными значениями по умолчанию, но без `minimum: 1` и `maximum: 100` в схеме (`TD016-S5-009`, `low`).
- `PagedResultDto<T>` разворачивается Swashbuckle в 3 конкретные схемы (`UserDtoPagedResultDto`, `CommitDtoPagedResultDto`, `AnalysisDtoPagedResultDto`).
- `GET /api/projects` и `GET /api/projects/owner/{ownerId}` не имеют пагинации (подтверждено в Stage 2).

---

## 15. Генерация клиентов

- **`TD016-S5-006` (`medium`):** все 26 операций в `swagger.json` не имеют `operationId` (`CustomOperationIds` не настроен).
- Для `.NET 10` (`NSwag`/`Kiota`/`Refit`) и `TypeScript` (`openapi-typescript`/`orval`) критическими препятствиями являются отсутствие `operationId`, наследование `Bearer` на `login`/`register`, нетипизированные `code`/`traceId`/`errors` + `application/json` вместо `application/problem+json`, а также `nullable: true` на обязательных полях ответов.

---

## 16. Версионирование и эволюция

- **`TD016-S5-013` (`evolution_risk`):** версионирование API не настроено (`SwaggerDoc("v1")` — лишь метка). При 0 реализованных клиентах сегодня это не блокирует разработку, но перед выпуском CLI, Desktop и Telegram-бота рекомендуется зафиксировать стратегию (`/api/v1/` либо строго аддитивную политику).

---

## 17. Реестр подтверждённых замечаний

Полный машиночитаемый реестр из 13 записей находится в `docs/audits/TD-016-Stage-5-OpenAPI/findings.json`:
- **`confirmed_defect` (`medium`, 5):** `TD016-S5-001` (`GitHubSyncResultDto`), `TD016-S5-002` (`CreateCommitDto.CommitHash`), `TD016-S5-003` (`409` в `SyncGitHub`), `TD016-S5-004` (`ProblemDetails` и `application/problem+json`), `TD016-S5-005` (`Bearer` на `register`/`login`). *(Понижены с `high` до `medium`, так как на момент аудита реализованных клиентов нет.)*
- **`client_generation_risk` (`medium`, 1):** `TD016-S5-006` (`operationId`).
- **`contract_ambiguity` (`medium`, 2):** `TD016-S5-007` (`SupportNonNullableReferenceTypes`), `TD016-S5-008` (`CommitDate` timezone).
- **`documentation_gap` (4):** `TD016-S5-009` (`page`/`pageSize` bounds, `low`), `TD016-S5-010` (`Retry-After`/`500`, `low`), `TD016-S5-011` (`UpdateUserRoleDto.RoleId` required, `low`), `TD016-S5-012` (`/health`, info).
- **`evolution_risk` (1):** `TD016-S5-013` (версионирование API).

---

## 18. Неоднозначности и открытые вопросы

1. Какие генераторы клиентов будут выбраны для .NET 10 (NSwag/Kiota/Refit) и TypeScript (`openapi-typescript`/`orval`)?
2. Будет ли введено явное версионирование `/api/v1/...` до начала разработки трёх клиентов?

---

## 19. Рекомендации

1. Перегенерировать `swagger.json` из актуальной сборки API для устранения дрейфа (`TD016-S5-001`, `002`, `003`).
2. Настроить `AddSwaggerGen`: `CustomOperationIds`, `SupportNonNullableReferenceTypes()`, OperationFilter для снятия `Bearer` с публичных операций (`register`/`login`), а также схему `ApiProblemDetails` (`code`, `traceId`, `errors`) и `application/problem+json`.
3. Добавить `[Range]` на `page`/`pageSize` и `[Required]` на `UpdateUserRoleDto.RoleId`.

---

## 20. Ограничения аудита

- Пробная кодогенерация внешними инструментами (`NSwag`, `Kiota`, `orval`) и живые HTTP-запросы не выполнялись; выводы основаны на статическом анализе `swagger.json` и исходного кода.

---

## 21. Итоговое заключение и критерии завершения

Stage 5 выполнен в полном объёме: все 27 операций, 19 DTO, 3 перечисления, `ProblemDetails`, `SecuritySchemes`, пагинация, кодогенерация и версионирование проверены и независимо верифицированы. Исходный код, конфигурации, тесты, миграции и БД не изменялись.
