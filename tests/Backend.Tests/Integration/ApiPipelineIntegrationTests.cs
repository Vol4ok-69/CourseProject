using Api.Common;
using Application.DTOs;
using Application.Exceptions;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Backend.Tests.Integration;

/// <summary>
/// Сквозные интеграционные тесты HTTP-конвейера ASP.NET Core на базе WebApplicationFactory и реального PostgreSQL.
/// Проверяют взаимодействие маршрутизации, валидации DTO, JWT-аутентификации, авторизации ролей,
/// ProblemDetails (RFC 9457), сериализации JSON/enum, CORS и Rate Limiting (TD-015).
/// </summary>
public sealed class ApiPipelineIntegrationTests(ApiWebApplicationFactory factory)
    : IClassFixture<ApiWebApplicationFactory>, IAsyncLifetime
{
    private readonly ApiWebApplicationFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task InitializeAsync()
    {
        _factory.ResetMocks();
        await _factory.ResetDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    #region 6.1. Анонимные запросы и JWT

    [Fact]
    public async Task AnonymousRequest_ToProtectedEndpoint_Returns401Unauthorized_WithProblemDetails()
    {
        // Act: отправка анонимного запроса к защищенному маршруту
        var response = await _client.GetAsync("/api/projects");

        // Assert: проверка статуса и типа контента RFC 9457
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(body);
        var root = jsonDoc.RootElement;

        Assert.Equal(401, root.GetProperty("status").GetInt32());
        Assert.Equal("Требуется аутентификация", root.GetProperty("title").GetString());
        Assert.Equal(ApiErrorCodes.Unauthorized, root.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task InvalidBearerToken_Returns401Unauthorized_WithProblemDetails()
    {
        // Arrange
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/projects");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "malformed.invalid.token");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(body);
        var root = jsonDoc.RootElement;

        Assert.Equal(401, root.GetProperty("status").GetInt32());
        Assert.Equal(ApiErrorCodes.Unauthorized, root.GetProperty("code").GetString());
    }

    [Fact]
    public async Task TokenWithInvalidSignature_Returns401Unauthorized_WithProblemDetails()
    {
        // Arrange: подготовка пользователя и токена с чужой цифровой подписью
        var user = await CreateTestUserAsync("user_bad_sig", "User");
        var invalidToken = _factory.GenerateInvalidSignatureToken(user);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/projects");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", invalidToken);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(body);
        Assert.Equal(ApiErrorCodes.Unauthorized, jsonDoc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExpiredToken_Returns401Unauthorized_WithProblemDetails()
    {
        // Arrange: подготовка пользователя и просроченного токена
        var user = await CreateTestUserAsync("user_expired", "User");
        var expiredToken = _factory.GenerateExpiredToken(user);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/projects");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", expiredToken);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(body);
        Assert.Equal(ApiErrorCodes.Unauthorized, jsonDoc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ValidToken_AllowsAccess_Returns200Ok()
    {
        // Arrange: подготовка пользователя и валидного подписанного токена
        var user = await CreateTestUserAsync("user_valid_jwt", "User");
        var validToken = _factory.GenerateValidToken(user);

        using var client = _factory.CreateAuthenticatedClient(validToken);

        // Act
        var response = await client.GetAsync("/api/projects");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var projects = await response.Content.ReadFromJsonAsync<IReadOnlyList<ProjectDto>>();
        Assert.NotNull(projects);
    }

    #endregion

    #region 6.2. Авторизация и владение ресурсами (Resource Ownership)

    [Fact]
    public async Task User_CanAccess_OwnProject_Returns200Ok()
    {
        // Arrange: пользователь и принадлежащий ему проект
        var user = await CreateTestUserAsync("owner_user", "User");
        var project = await CreateTestProjectAsync("owner_project", user.Id);

        var token = _factory.GenerateValidToken(user);
        using var client = _factory.CreateAuthenticatedClient(token);

        // Act
        var response = await client.GetAsync($"/api/projects/{project.Id}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(dto);
        Assert.Equal(project.Id, dto.Id);
        Assert.Equal(project.Name, dto.Name);
    }

    [Fact]
    public async Task User_CannotAccess_OtherUsersProject_Returns403Forbidden()
    {
        // Arrange: два разных пользователя и проект второго пользователя
        var user1 = await CreateTestUserAsync("user_requester", "User");
        var user2 = await CreateTestUserAsync("user_victim", "User");
        var victimProject = await CreateTestProjectAsync("victim_project", user2.Id);

        var token1 = _factory.GenerateValidToken(user1);
        using var client = _factory.CreateAuthenticatedClient(token1);

        // Act: попытка доступа user1 к проекту user2
        var response = await client.GetAsync($"/api/projects/{victimProject.Id}");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(body);
        Assert.Equal(ApiErrorCodes.Forbidden, jsonDoc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Admin_CanAccess_OtherUsersProject_Returns200Ok()
    {
        // Arrange: администратор и проект обычного пользователя
        var admin = await CreateTestUserAsync("admin_super", "Admin");
        var regularUser = await CreateTestUserAsync("regular_owner", "User");
        var regularProject = await CreateTestProjectAsync("regular_project", regularUser.Id);

        var adminToken = _factory.GenerateValidToken(admin);
        using var client = _factory.CreateAuthenticatedClient(adminToken);

        // Act: администратор запрашивает проект обычного пользователя
        var response = await client.GetAsync($"/api/projects/{regularProject.Id}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(dto);
        Assert.Equal(regularProject.Id, dto.Id);
    }

    [Fact]
    public async Task RegularUser_CannotAccess_AdminUsersEndpoint_Returns403Forbidden()
    {
        // Arrange: обычный пользователь с ролью User
        var user = await CreateTestUserAsync("regular_user_no_admin", "User");
        var token = _factory.GenerateValidToken(user);
        using var client = _factory.CreateAuthenticatedClient(token);

        // Act: попытка обращения к административному списку пользователей
        var response = await client.GetAsync("/api/admin/users");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(body);
        Assert.Equal(ApiErrorCodes.Forbidden, jsonDoc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AdminUser_CanAccess_AdminUsersEndpoint_Returns200Ok()
    {
        // Arrange: пользователь с ролью Admin
        var admin = await CreateTestUserAsync("admin_user_allowed", "Admin");
        var token = _factory.GenerateValidToken(admin);
        using var client = _factory.CreateAuthenticatedClient(token);

        // Act
        var response = await client.GetAsync("/api/admin/users");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PagedResultDto<UserDto>>();
        Assert.NotNull(result);
        Assert.True(result.TotalCount >= 1);
    }

    #endregion

    #region 6.3. Валидация DTO и ProblemDetails (RFC 9457)

    [Fact]
    public async Task MalformedJson_Returns400BadRequest_WithValidationProblemDetails()
    {
        // Arrange: отправка синтаксически некорректного JSON
        var malformedContent = new StringContent(
            "{ malformed_json: true, unclosed ",
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _client.PostAsync("/api/auth/register", malformedContent);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Content.Headers.ContentType?.MediaType is "application/problem+json" or "application/json");

        var body = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(body);
        var root = jsonDoc.RootElement;

        Assert.Equal(400, root.GetProperty("status").GetInt32());
        Assert.Equal(ApiErrorCodes.ValidationFailed, root.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));

        // Проверка отсутствия утечек серверных данных
        Assert.DoesNotContain("at Microsoft.AspNetCore", body);
        Assert.DoesNotContain("at System.", body);
        Assert.DoesNotContain("Exception:", body);
    }

    [Fact]
    public async Task MissingRequiredDtoFields_Returns400BadRequest_WithErrorsDictionary()
    {
        // Arrange: пустые обязательные поля Username и Password
        var invalidDto = new RegisterDto { Username = string.Empty, Password = string.Empty };

        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/register", invalidDto);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Content.Headers.ContentType?.MediaType is "application/problem+json" or "application/json");

        var body = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(body);
        var root = jsonDoc.RootElement;

        Assert.Equal(400, root.GetProperty("status").GetInt32());
        Assert.Equal("Ошибка валидации входных данных", root.GetProperty("title").GetString());
        Assert.Equal("Одно или несколько полей содержат некорректные данные.", root.GetProperty("detail").GetString());
        Assert.Equal(ApiErrorCodes.ValidationFailed, root.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));

        // Обязательное наличие словаря errors для валидационных ошибок 400
        Assert.True(root.TryGetProperty("errors", out var errorsElement));
        Assert.True(errorsElement.ValueKind == JsonValueKind.Object);

        // Проверка отсутствия секретов и стека
        Assert.DoesNotContain("at System.", body);
        Assert.DoesNotContain("PasswordHash", body);
    }

    #endregion

    #region 6.4. Сериализация JSON и enum

    [Fact]
    public async Task EnumProperties_SerializedAsString_NotNumeric()
    {
        // Arrange: создаем проект, коммит и ставим задачу анализа через HTTP API
        var user = await CreateTestUserAsync("enum_tester", "User");
        var project = await CreateTestProjectAsync("enum_proj", user.Id);
        var commit = await CreateTestCommitAsync(project.Id, "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678");

        var token = _factory.GenerateValidToken(user);
        using var client = _factory.CreateAuthenticatedClient(token);

        // Act: постановка задачи анализа коммита в очередь
        var response = await client.PostAsync($"/api/projects/{project.Id}/commits/{commit.Id}/analyses", null);

        // Assert
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var rawJson = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(rawJson);
        var root = jsonDoc.RootElement;

        // Поле status должно сериализоваться как строка "Pending", а не как число 0
        Assert.True(root.TryGetProperty("status", out var statusElement) ||
                    root.TryGetProperty("Status", out statusElement));

        Assert.Equal(JsonValueKind.String, statusElement.ValueKind);
        Assert.Equal(nameof(AnalysisStatus.Pending), statusElement.GetString());
    }

    #endregion

    #region 6.5. CORS Preflight (OPTIONS)

    [Fact]
    public async Task CorsPreflight_AllowedOrigin5173_ReturnsAccessControlHeaders()
    {
        // Arrange: preflight-запрос с доверенным источником http://localhost:5173
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/projects");
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.True(response.IsSuccessStatusCode);
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins));
        Assert.Contains("http://localhost:5173", origins);

        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Methods", out var methods));
        Assert.Contains(methods, m => m.Contains("GET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CorsPreflight_AllowedOrigin3000_ReturnsAccessControlHeaders()
    {
        // Arrange: preflight-запрос с доверенным источником http://localhost:3000
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/projects");
        request.Headers.Add("Origin", "http://localhost:3000");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.True(response.IsSuccessStatusCode);
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins));
        Assert.Contains("http://localhost:3000", origins);
    }

    [Fact]
    public async Task CorsPreflight_UntrustedOrigin_DoesNotReturnAllowOriginHeader()
    {
        // Arrange: preflight-запрос с недоверенным источником
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/projects");
        request.Headers.Add("Origin", "http://malicious-site.com");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        // Act
        var response = await _client.SendAsync(request);

        // Assert: заголовок Access-Control-Allow-Origin для недоверенного источника не возвращается
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    #endregion

    #region 6.6. Rate Limiting

    [Fact]
    public async Task RateLimiting_BurstRequests_Returns429TooManyRequests_WithRetryAfterAndProblemDetails()
    {
        // Arrange: используем изолированный TestServer для чистоты проверки лимитов
        using var isolatedFactory = _factory.WithWebHostBuilder(_ => { });
        using var burstClient = isolatedFactory.CreateClient();
        var loginDto = new LoginDto { Username = "nonexistent_user", Password = "password123" };

        // Лимит политики AuthPolicy: 10 запросов в корзине.
        // Выполняем 10 запросов подряд в пределах лимита:
        for (var i = 1; i <= 10; i++)
        {
            var allowedResponse = await burstClient.PostAsJsonAsync("/api/auth/login", loginDto);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowedResponse.StatusCode);
        }

        // Act: 11-й запрос без задержки должен быть отклонен rate limiter middleware
        var rejectedResponse = await burstClient.PostAsJsonAsync("/api/auth/login", loginDto);

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, rejectedResponse.StatusCode);
        Assert.Equal("application/problem+json", rejectedResponse.Content.Headers.ContentType?.MediaType);

        // Проверка заголовка Retry-After
        Assert.NotNull(rejectedResponse.Headers.RetryAfter);

        var body = await rejectedResponse.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(body);
        var root = jsonDoc.RootElement;

        Assert.Equal(429, root.GetProperty("status").GetInt32());
        Assert.Equal("Слишком много запросов", root.GetProperty("title").GetString());
        Assert.Equal(ApiErrorCodes.RateLimitExceeded, root.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
    }

    #endregion

    #region 6.7. Сценарий A: Жизненный цикл проектов и коммитов (Projects & Commits Lifecycle)

    [Fact]
    public async Task ProjectLifecycle_Create_Returns201Created_WithLocation_AndPersistsInPostgreSql()
    {
        // Arrange
        var user = await CreateTestUserAsync("proj_creator", "User");
        var token = _factory.GenerateValidToken(user);
        using var client = _factory.CreateAuthenticatedClient(token);

        var createDto = new CreateProjectDto
        {
            Name = "backend-service",
            RepoUrl = "https://github.com/my-org/backend-service"
        };

        // Act: отправка POST /api/projects через HTTP
        var response = await client.PostAsJsonAsync("/api/projects", createDto);

        // Assert HTTP
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var projectDto = await response.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(projectDto);
        Assert.True(projectDto.Id > 0);
        Assert.Equal("backend-service", projectDto.Name);
        Assert.Equal("https://github.com/my-org/backend-service", projectDto.RepoUrl);
        Assert.Equal(user.Id, projectDto.OwnerId);
        Assert.EndsWith($"/api/projects/{projectDto.Id}", response.Headers.Location.ToString());

        // Assert PostgreSQL state
        var dbProject = await _factory.ExecuteDbContextAsync(async db =>
            await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectDto.Id));

        Assert.NotNull(dbProject);
        Assert.Equal("backend-service", dbProject.Name);
        Assert.Equal("https://github.com/my-org/backend-service", dbProject.RepoUrl);
        Assert.Equal(user.Id, dbProject.OwnerId);
    }

    [Fact]
    public async Task ProjectLifecycle_GetAll_FiltersByOwner_AndAllowsAdminToSeeAll()
    {
        // Arrange
        var user1 = await CreateTestUserAsync("user1_proj_owner", "User");
        var user2 = await CreateTestUserAsync("user2_proj_owner", "User");
        var admin = await CreateTestUserAsync("admin_proj_viewer", "Admin");

        var p1 = await CreateTestProjectAsync("project_user1", user1.Id);
        var p2 = await CreateTestProjectAsync("project_user2", user2.Id);

        var tokenUser1 = _factory.GenerateValidToken(user1);
        var tokenUser2 = _factory.GenerateValidToken(user2);
        var tokenAdmin = _factory.GenerateValidToken(admin);

        using var clientUser1 = _factory.CreateAuthenticatedClient(tokenUser1);
        using var clientUser2 = _factory.CreateAuthenticatedClient(tokenUser2);
        using var clientAdmin = _factory.CreateAuthenticatedClient(tokenAdmin);

        // Act & Assert: user1 получает только свои проекты
        var respUser1 = await clientUser1.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.OK, respUser1.StatusCode);
        var listUser1 = await respUser1.Content.ReadFromJsonAsync<List<ProjectDto>>();
        Assert.NotNull(listUser1);
        Assert.Single(listUser1);
        Assert.Equal(p1.Id, listUser1[0].Id);

        // Act & Assert: user2 получает только свои проекты
        var respUser2 = await clientUser2.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.OK, respUser2.StatusCode);
        var listUser2 = await respUser2.Content.ReadFromJsonAsync<List<ProjectDto>>();
        Assert.NotNull(listUser2);
        Assert.Single(listUser2);
        Assert.Equal(p2.Id, listUser2[0].Id);

        // Act & Assert: user1 с all=true все равно видит только свои проекты (не админ)
        var respUser1All = await clientUser1.GetAsync("/api/projects?all=true");
        var listUser1All = await respUser1All.Content.ReadFromJsonAsync<List<ProjectDto>>();
        Assert.NotNull(listUser1All);
        Assert.Single(listUser1All);

        // Act & Assert: Admin без all=true видит только проекты где он владелец (0)
        var respAdminDefault = await clientAdmin.GetAsync("/api/projects");
        var listAdminDefault = await respAdminDefault.Content.ReadFromJsonAsync<List<ProjectDto>>();
        Assert.NotNull(listAdminDefault);
        Assert.Empty(listAdminDefault);

        // Act & Assert: Admin с all=true видит проекты всех пользователей
        var respAdminAll = await clientAdmin.GetAsync("/api/projects?all=true");
        Assert.Equal(HttpStatusCode.OK, respAdminAll.StatusCode);
        var listAdminAll = await respAdminAll.Content.ReadFromJsonAsync<List<ProjectDto>>();
        Assert.NotNull(listAdminAll);
        Assert.Contains(listAdminAll, p => p.Id == p1.Id);
        Assert.Contains(listAdminAll, p => p.Id == p2.Id);
    }

    [Fact]
    public async Task ProjectLifecycle_GetById_OwnerAndAdminAllowed_OtherUserForbidden_NonExistentNotFound()
    {
        // Arrange
        var owner = await CreateTestUserAsync("proj_get_owner", "User");
        var stranger = await CreateTestUserAsync("proj_get_stranger", "User");
        var admin = await CreateTestUserAsync("proj_get_admin", "Admin");
        var project = await CreateTestProjectAsync("project_for_get", owner.Id);

        using var clientOwner = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(owner));
        using var clientStranger = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(stranger));
        using var clientAdmin = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(admin));

        // Act & Assert: владелец получает 200 OK
        var respOwner = await clientOwner.GetAsync($"/api/projects/{project.Id}");
        Assert.Equal(HttpStatusCode.OK, respOwner.StatusCode);
        var dto = await respOwner.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(dto);
        Assert.Equal(project.Id, dto.Id);

        // Act & Assert: администратор получает 200 OK
        var respAdmin = await clientAdmin.GetAsync($"/api/projects/{project.Id}");
        Assert.Equal(HttpStatusCode.OK, respAdmin.StatusCode);

        // Act & Assert: посторонний пользователь получает 403 Forbidden с ProblemDetails
        var respStranger = await clientStranger.GetAsync($"/api/projects/{project.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, respStranger.StatusCode);
        Assert.Equal("application/problem+json", respStranger.Content.Headers.ContentType?.MediaType);

        // Act & Assert: несуществующий проект возвращает 404 NotFound
        var respNotFound = await clientOwner.GetAsync("/api/projects/999999");
        Assert.Equal(HttpStatusCode.NotFound, respNotFound.StatusCode);
    }

    [Fact]
    public async Task ProjectLifecycle_Update_OwnerCanUpdate_PersistsInPostgreSql_DuplicateNameConflict()
    {
        // Arrange
        var user = await CreateTestUserAsync("proj_updater", "User");
        var stranger = await CreateTestUserAsync("proj_stranger_updater", "User");

        var p1 = await CreateTestProjectAsync("proj_original_name", user.Id);
        var p2 = await CreateTestProjectAsync("proj_second_name", user.Id);

        using var clientUser = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(user));
        using var clientStranger = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(stranger));

        var updateDto = new UpdateProjectDto
        {
            Name = "proj_updated_name",
            RepoUrl = "https://github.com/my-org/proj-updated-url"
        };

        // Act 1: успешное обновление владельцем
        var updateResp = await clientUser.PutAsJsonAsync($"/api/projects/{p1.Id}", updateDto);
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);
        var updatedDto = await updateResp.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(updatedDto);
        Assert.Equal("proj_updated_name", updatedDto.Name);
        Assert.Equal("https://github.com/my-org/proj-updated-url", updatedDto.RepoUrl);

        // Assert в БД PostgreSQL
        var dbProject = await _factory.ExecuteDbContextAsync(async db =>
            await db.Projects.AsNoTracking().FirstAsync(p => p.Id == p1.Id));
        Assert.Equal("proj_updated_name", dbProject.Name);
        Assert.Equal("https://github.com/my-org/proj-updated-url", dbProject.RepoUrl);

        // Act 2: попытка обновления до дублирующегося имени у того же владельца -> 409 Conflict
        var conflictDto = new UpdateProjectDto
        {
            Name = "proj_second_name",
            RepoUrl = "https://github.com/my-org/proj-updated-url"
        };
        var conflictResp = await clientUser.PutAsJsonAsync($"/api/projects/{p1.Id}", conflictDto);
        Assert.Equal(HttpStatusCode.Conflict, conflictResp.StatusCode);
        Assert.True(conflictResp.Content.Headers.ContentType?.MediaType is "application/problem+json" or "application/json");
        var conflictBody = await conflictResp.Content.ReadAsStringAsync();
        using var conflictDoc = JsonDocument.Parse(conflictBody);
        Assert.Equal(ApiErrorCodes.ProjectNameDuplicate, conflictDoc.RootElement.GetProperty("code").GetString());

        // Act 3: попытка обновления чужого проекта -> 403 Forbidden
        var strangerResp = await clientStranger.PutAsJsonAsync($"/api/projects/{p1.Id}", updateDto);
        Assert.Equal(HttpStatusCode.Forbidden, strangerResp.StatusCode);

        // Act 4: несуществующий проект -> 404 NotFound
        var notFoundResp = await clientUser.PutAsJsonAsync("/api/projects/999999", updateDto);
        Assert.Equal(HttpStatusCode.NotFound, notFoundResp.StatusCode);
    }

    [Fact]
    public async Task ProjectLifecycle_Delete_OwnerCanDelete_RemovesFromPostgreSql_OtherUserForbidden()
    {
        // Arrange
        var user = await CreateTestUserAsync("proj_deleter", "User");
        var stranger = await CreateTestUserAsync("proj_stranger_deleter", "User");
        var project = await CreateTestProjectAsync("project_to_delete", user.Id);

        using var clientUser = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(user));
        using var clientStranger = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(stranger));

        // Act 1: попытка удаления чужого проекта -> 403 Forbidden
        var strangerResp = await clientStranger.DeleteAsync($"/api/projects/{project.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, strangerResp.StatusCode);

        // Act 2: успешное удаление владельцем -> 204 NoContent
        var deleteResp = await clientUser.DeleteAsync($"/api/projects/{project.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);

        // Assert в БД PostgreSQL: проект удален
        var existsInDb = await _factory.ExecuteDbContextAsync(async db =>
            await db.Projects.AnyAsync(p => p.Id == project.Id));
        Assert.False(existsInDb);

        // Act 3: повторный запрос GET возвращает 404 NotFound
        var getResp = await clientUser.GetAsync($"/api/projects/{project.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);

        // Act 4: удаление несуществующего проекта возвращает 404 NotFound
        var deleteNotFound = await clientUser.DeleteAsync("/api/projects/999999");
        Assert.Equal(HttpStatusCode.NotFound, deleteNotFound.StatusCode);
    }

    [Fact]
    public async Task CommitLifecycle_Create_Returns201Created_WithLocation_DuplicateHashConflict()
    {
        // Arrange
        var user = await CreateTestUserAsync("commit_creator", "User");
        var stranger = await CreateTestUserAsync("commit_stranger", "User");
        var project = await CreateTestProjectAsync("project_with_commits", user.Id);

        using var clientUser = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(user));
        using var clientStranger = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(stranger));

        var commitHash = "1234567890abcdef1234567890abcdef12345678";
        var commitDate = DateTime.UtcNow.AddMinutes(-30);
        var createDto = new CreateCommitDto
        {
            CommitHash = commitHash,
            CommitDate = commitDate
        };

        // Act 1: создание коммита владельцем проекта
        var resp = await clientUser.PostAsJsonAsync($"/api/projects/{project.Id}/commits", createDto);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.NotNull(resp.Headers.Location);
        var commitDto = await resp.Content.ReadFromJsonAsync<CommitDto>();
        Assert.NotNull(commitDto);
        Assert.True(commitDto.Id > 0);
        Assert.Equal(project.Id, commitDto.ProjectId);
        Assert.Equal(commitHash, commitDto.CommitHash);
        Assert.EndsWith($"/api/projects/{project.Id}/commits/{commitDto.Id}", resp.Headers.Location.ToString());

        // Assert в БД PostgreSQL
        var dbCommit = await _factory.ExecuteDbContextAsync(async db =>
            await db.Commits.AsNoTracking().FirstOrDefaultAsync(c => c.Id == commitDto.Id));
        Assert.NotNull(dbCommit);
        Assert.Equal(project.Id, dbCommit.ProjectId);
        Assert.Equal(commitHash, dbCommit.CommitHash);

        // Act 2: попытка создать коммит с тем же хэшем в том же проекте -> 409 Conflict
        var conflictResp = await clientUser.PostAsJsonAsync($"/api/projects/{project.Id}/commits", createDto);
        Assert.Equal(HttpStatusCode.Conflict, conflictResp.StatusCode);
        Assert.True(conflictResp.Content.Headers.ContentType?.MediaType is "application/problem+json" or "application/json");
        var conflictBody = await conflictResp.Content.ReadAsStringAsync();
        using var conflictDoc = JsonDocument.Parse(conflictBody);
        Assert.Equal(ApiErrorCodes.CommitAlreadyExists, conflictDoc.RootElement.GetProperty("code").GetString());

        // Act 3: попытка создания коммита посторонним пользователем -> 403 Forbidden
        var strangerResp = await clientStranger.PostAsJsonAsync($"/api/projects/{project.Id}/commits", new CreateCommitDto
        {
            CommitHash = "ffffffffffffffffffffffffffffffffffffffff",
            CommitDate = DateTime.UtcNow
        });
        Assert.Equal(HttpStatusCode.Forbidden, strangerResp.StatusCode);

        // Act 4: несуществующий проект -> 404 NotFound
        var notFoundResp = await clientUser.PostAsJsonAsync("/api/projects/999999/commits", createDto);
        Assert.Equal(HttpStatusCode.NotFound, notFoundResp.StatusCode);
    }

    [Fact]
    public async Task CreateCommit_WithSha256Hash_SucceedsAndPersistsInDatabase()
    {
        // Arrange: проект и 64-символьный хэш Git SHA-256 (TD-008)
        var user = await CreateTestUserAsync("sha256_commit_user", "User");
        var project = await CreateTestProjectAsync("sha256_project", user.Id);
        using var client = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(user));

        var sha256Hash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        var createDto = new CreateCommitDto
        {
            CommitHash = sha256Hash,
            CommitDate = DateTime.UtcNow
        };

        // Act: отправка запроса на создание коммита с SHA-256 хэшем
        var response = await client.PostAsJsonAsync($"/api/projects/{project.Id}/commits", createDto);

        // Assert: статус 201 Created и корректное сохранение в PostgreSQL
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var createdDto = await response.Content.ReadFromJsonAsync<CommitDto>();
        Assert.NotNull(createdDto);
        Assert.Equal(sha256Hash, createdDto.CommitHash);
        Assert.Equal(project.Id, createdDto.ProjectId);

        var dbCommit = await _factory.ExecuteDbContextAsync(async db =>
            await db.Commits.AsNoTracking().FirstOrDefaultAsync(c => c.Id == createdDto.Id));
        Assert.NotNull(dbCommit);
        Assert.Equal(sha256Hash, dbCommit.CommitHash);
    }

    [Theory]
    [InlineData("123456")] // Длина 6 (меньше минимальных 7)
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345")] // Длина 65 (больше максимальных 64)
    [InlineData("not_a_valid_hex_commit_hash")] // Не-hex символы
    [InlineData("e9ef5e43-b99da445-bb548b049d52e0e7cbf2a5")] // Спецсимволы (дефис)
    [InlineData("e9ef5e43b99da445bb548b049d52e0e7cbf2a5ec ")] // Пробел
    [InlineData("e9ef5e43b99da445bb548b049d52e0e7cbf2a5ec\n")] // Завершающий перевод строки \n
    [InlineData("e9ef5e43b99da445bb548b049d52e0e7cbf2a5ec\r\n")] // Завершающий перевод строки \r\n
    public async Task CreateCommit_WithInvalidHash_Returns400BadRequest_WithValidationProblemDetails(string invalidHash)
    {
        // Arrange
        var user = await CreateTestUserAsync("invalid_hash_user_" + Guid.NewGuid().ToString("N")[..6], "User");
        var project = await CreateTestProjectAsync("invalid_hash_proj_" + Guid.NewGuid().ToString("N")[..6], user.Id);
        using var client = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(user));

        var createDto = new CreateCommitDto
        {
            CommitHash = invalidHash,
            CommitDate = DateTime.UtcNow
        };

        // Act: отправка невалидного DTO через сквозной API-конвейер
        var response = await client.PostAsJsonAsync($"/api/projects/{project.Id}/commits", createDto);

        // Assert: проверка формата RFC 9457 ValidationProblemDetails
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Content.Headers.ContentType?.MediaType is "application/problem+json" or "application/json");

        var body = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(body);
        var root = jsonDoc.RootElement;

        Assert.Equal(400, root.GetProperty("status").GetInt32());
        Assert.Equal("Ошибка валидации входных данных", root.GetProperty("title").GetString());
        Assert.Equal(ApiErrorCodes.ValidationFailed, root.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));

        // Проверка наличия ошибки по полю CommitHash
        Assert.True(root.TryGetProperty("errors", out var errorsElement));
        Assert.True(errorsElement.TryGetProperty("CommitHash", out var commitHashErrors));
        Assert.True(commitHashErrors.GetArrayLength() > 0);
    }

    [Fact]
    public async Task CommitLifecycle_GetByProjectId_ReturnsPagedResult_AndValidatesPagination()
    {
        // Arrange: проект с 3 коммитами
        var user = await CreateTestUserAsync("commit_pager", "User");
        var project = await CreateTestProjectAsync("project_for_paging", user.Id);
        await CreateTestCommitAsync(project.Id, "1111111111111111111111111111111111111111");
        await CreateTestCommitAsync(project.Id, "2222222222222222222222222222222222222222");
        await CreateTestCommitAsync(project.Id, "3333333333333333333333333333333333333333");

        using var client = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(user));

        // Act: страница 1, размер 2
        var respPage1 = await client.GetAsync($"/api/projects/{project.Id}/commits?page=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, respPage1.StatusCode);
        var paged1 = await respPage1.Content.ReadFromJsonAsync<PagedResultDto<CommitDto>>();
        Assert.NotNull(paged1);
        Assert.Equal(3, paged1.TotalCount);
        Assert.Equal(1, paged1.Page);
        Assert.Equal(2, paged1.PageSize);
        Assert.Equal(2, paged1.Items.Count);

        // Act: страница 2, размер 2
        var respPage2 = await client.GetAsync($"/api/projects/{project.Id}/commits?page=2&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, respPage2.StatusCode);
        var paged2 = await respPage2.Content.ReadFromJsonAsync<PagedResultDto<CommitDto>>();
        Assert.NotNull(paged2);
        Assert.Equal(3, paged2.TotalCount);
        Assert.Equal(2, paged2.Page);
        Assert.Single(paged2.Items);

        // Act: невалидная пагинация page < 1 -> 400 BadRequest
        var respInvalidPage = await client.GetAsync($"/api/projects/{project.Id}/commits?page=0&pageSize=10");
        Assert.Equal(HttpStatusCode.BadRequest, respInvalidPage.StatusCode);
        var body = await respInvalidPage.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(ApiErrorCodes.ValidationFailed, doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CommitLifecycle_GetById_VerifiesProjectMembership_AndRejectsCrossProjectAccess()
    {
        // Arrange: два проекта одного владельца с разными коммитами
        var user = await CreateTestUserAsync("commit_membership_user", "User");
        var stranger = await CreateTestUserAsync("commit_membership_stranger", "User");

        var p1 = await CreateTestProjectAsync("proj_cross_1", user.Id);
        var p2 = await CreateTestProjectAsync("proj_cross_2", user.Id);

        var c1 = await CreateTestCommitAsync(p1.Id, "1111111111111111111111111111111111111111");
        var c2 = await CreateTestCommitAsync(p2.Id, "2222222222222222222222222222222222222222");

        using var clientUser = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(user));
        using var clientStranger = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(stranger));

        // Act & Assert 1: корректный коммит своего проекта -> 200 OK
        var respValid = await clientUser.GetAsync($"/api/projects/{p1.Id}/commits/{c1.Id}");
        Assert.Equal(HttpStatusCode.OK, respValid.StatusCode);
        var dto = await respValid.Content.ReadFromJsonAsync<CommitDto>();
        Assert.NotNull(dto);
        Assert.Equal(c1.Id, dto.Id);

        // Act & Assert 2: коммит p2 запрашивается через URL p1 -> 404 NotFound
        var respWrongProject = await clientUser.GetAsync($"/api/projects/{p1.Id}/commits/{c2.Id}");
        Assert.Equal(HttpStatusCode.NotFound, respWrongProject.StatusCode);

        // Act & Assert 3: посторонний пользователь -> 403 Forbidden
        var respForbidden = await clientStranger.GetAsync($"/api/projects/{p1.Id}/commits/{c1.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, respForbidden.StatusCode);

        // Act & Assert 4: несуществующий коммит -> 404 NotFound
        var respNotFound = await clientUser.GetAsync($"/api/projects/{p1.Id}/commits/999999");
        Assert.Equal(HttpStatusCode.NotFound, respNotFound.StatusCode);
    }

    #endregion

    #region 6.8. Сценарий B: Интеграция с GitHub PAT (Personal Access Token Lifecycle & Security)

    [Fact]
    public async Task GitHubPat_SetToken_ValidToken_Returns200OK_MasksSecret_AndStoresEncryptedInPostgreSql()
    {
        // Arrange
        var user = await CreateTestUserAsync("pat_user_valid", "User");
        using var client = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(user));

        const string rawSecretToken = "ghp_secureSecretTokenValue1234567890abcdef";
        var requestDto = new GitHubTokenRequestDto { Token = rawSecretToken };

        _factory.MockGitHubService
            .Setup(g => g.VerifyTokenAndGetUsernameAsync(rawSecretToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync("octocat-secure");

        // Act: отправка PUT /api/auth/me/github
        var response = await client.PutAsJsonAsync("/api/auth/me/github", requestDto);

        // Assert HTTP
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawResponseJson = await response.Content.ReadAsStringAsync();

        // Безопасность: секрет ни в каком виде не должен утекать в теле ответа
        Assert.DoesNotContain(rawSecretToken, rawResponseJson, StringComparison.OrdinalIgnoreCase);

        var statusDto = await response.Content.ReadFromJsonAsync<GitHubIntegrationStatusDto>();
        Assert.NotNull(statusDto);
        Assert.True(statusDto.IsConnected);
        Assert.Equal("octocat-secure", statusDto.GitHubUsername);
        Assert.NotNull(statusDto.ConnectedAt);

        // Assert в БД PostgreSQL: токен зашифрован (не совпадает с открытым текстом)
        var dbUser = await _factory.ExecuteDbContextAsync(async db =>
            await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id));

        Assert.NotNull(dbUser.EncryptedGitHubToken);
        Assert.NotEqual(rawSecretToken, dbUser.EncryptedGitHubToken);
        Assert.Equal("octocat-secure", dbUser.GitHubUsername);
        Assert.NotNull(dbUser.GitHubTokenCreatedAt);
    }

    [Fact]
    public async Task GitHubPat_GetStatus_ReturnsConnectedState_WithoutExposingSecret()
    {
        // Arrange
        var user = await CreateTestUserAsync("pat_status_checker", "User");
        using var client = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(user));

        // Act 1: до сохранения токена статус IsConnected = false
        var respBefore = await client.GetAsync("/api/auth/me/github");
        Assert.Equal(HttpStatusCode.OK, respBefore.StatusCode);
        var statusBefore = await respBefore.Content.ReadFromJsonAsync<GitHubIntegrationStatusDto>();
        Assert.NotNull(statusBefore);
        Assert.False(statusBefore.IsConnected);
        Assert.Null(statusBefore.GitHubUsername);

        // Сохраняем токен
        const string secretToken = "ghp_statusSecretCheckToken999";
        _factory.MockGitHubService
            .Setup(g => g.VerifyTokenAndGetUsernameAsync(secretToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync("octocat-status");
        await client.PutAsJsonAsync("/api/auth/me/github", new GitHubTokenRequestDto { Token = secretToken });

        // Act 2: после сохранения токена статус IsConnected = true
        var respAfter = await client.GetAsync("/api/auth/me/github");
        Assert.Equal(HttpStatusCode.OK, respAfter.StatusCode);
        var rawJson = await respAfter.Content.ReadAsStringAsync();

        // Безопасность: секрет не присутствует в ответе
        Assert.DoesNotContain(secretToken, rawJson, StringComparison.OrdinalIgnoreCase);

        var statusAfter = await respAfter.Content.ReadFromJsonAsync<GitHubIntegrationStatusDto>();
        Assert.NotNull(statusAfter);
        Assert.True(statusAfter.IsConnected);
        Assert.Equal("octocat-status", statusAfter.GitHubUsername);
        Assert.NotNull(statusAfter.ConnectedAt);
    }

    [Fact]
    public async Task GitHubPat_RemoveToken_Returns200OK_AndClearsCredentialsInPostgreSql()
    {
        // Arrange: сохраняем токен пользователю
        var user = await CreateTestUserAsync("pat_user_to_remove", "User");
        using var client = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(user));

        await client.PutAsJsonAsync("/api/auth/me/github", new GitHubTokenRequestDto { Token = "ghp_someToken12345" });

        // Act: удаление токена через DELETE /api/auth/me/github
        var deleteResp = await client.DeleteAsync("/api/auth/me/github");
        Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);
        var status = await deleteResp.Content.ReadFromJsonAsync<GitHubIntegrationStatusDto>();
        Assert.NotNull(status);
        Assert.False(status.IsConnected);
        Assert.Null(status.GitHubUsername);
        Assert.Null(status.ConnectedAt);

        // Assert в БД PostgreSQL: учетные данные полностью очищены
        var dbUser = await _factory.ExecuteDbContextAsync(async db =>
            await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id));

        Assert.Null(dbUser.EncryptedGitHubToken);
        Assert.Null(dbUser.GitHubUsername);
        Assert.Null(dbUser.GitHubTokenCreatedAt);
    }

    [Fact]
    public async Task GitHubPat_ValidationAndErrorHandling_RejectsEmpty_Expired_AndExternalErrors_WithoutSecretLeak()
    {
        // Arrange
        var user = await CreateTestUserAsync("pat_error_tester", "User");
        using var client = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(user));

        // 1. Пустой токен -> 400 BadRequest с кодом VALIDATION_FAILED
        var emptyResp = await client.PutAsJsonAsync("/api/auth/me/github", new GitHubTokenRequestDto { Token = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, emptyResp.StatusCode);
        Assert.True(emptyResp.Content.Headers.ContentType?.MediaType is "application/problem+json" or "application/json");
        var emptyBody = await emptyResp.Content.ReadAsStringAsync();
        using var emptyDoc = JsonDocument.Parse(emptyBody);
        Assert.Equal(ApiErrorCodes.ValidationFailed, emptyDoc.RootElement.GetProperty("code").GetString());

        // 2. Истекший токен GitHub -> 400 BadRequest с кодом GITHUB_TOKEN_EXPIRED
        const string expiredToken = "ghp_expired_token_test_string";
        _factory.MockGitHubService
            .Setup(g => g.VerifyTokenAndGetUsernameAsync(expiredToken, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.TokenExpired, "Токен доступа GitHub истек."));

        var expiredResp = await client.PutAsJsonAsync("/api/auth/me/github", new GitHubTokenRequestDto { Token = expiredToken });
        Assert.Equal(HttpStatusCode.BadRequest, expiredResp.StatusCode);
        var expiredBody = await expiredResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(expiredToken, expiredBody, StringComparison.OrdinalIgnoreCase);
        using var expiredDoc = JsonDocument.Parse(expiredBody);
        Assert.Equal(ApiErrorCodes.GitHubTokenExpired, expiredDoc.RootElement.GetProperty("code").GetString());

        // 3. Ограничение скорости (Rate Limited) -> 502 BadGateway с кодом GITHUB_RATE_LIMITED
        const string rateLimitToken = "ghp_ratelimit_token_test_string";
        _factory.MockGitHubService
            .Setup(g => g.VerifyTokenAndGetUsernameAsync(rateLimitToken, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.RateLimited, "Превышен лимит запросов GitHub API."));

        var rateLimitResp = await client.PutAsJsonAsync("/api/auth/me/github", new GitHubTokenRequestDto { Token = rateLimitToken });
        Assert.Equal(HttpStatusCode.BadGateway, rateLimitResp.StatusCode);
        var rateLimitBody = await rateLimitResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(rateLimitToken, rateLimitBody, StringComparison.OrdinalIgnoreCase);
        using var rateLimitDoc = JsonDocument.Parse(rateLimitBody);
        Assert.Equal(ApiErrorCodes.GitHubRateLimited, rateLimitDoc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GitHubPat_SyncProject_WithValidToken_SyncsCommits_RejectsWithoutToken_HandlesRateLimit()
    {
        // Arrange
        var user = await CreateTestUserAsync("pat_sync_user", "User");
        var project = await CreateTestProjectAsync("project_for_sync", user.Id);
        using var client = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(user));

        // Act 1: синхронизация без сохраненного PAT -> 400 BadRequest с кодом GITHUB_TOKEN_REQUIRED
        var respNoToken = await client.PostAsync($"/api/projects/{project.Id}/github/sync", null);
        Assert.Equal(HttpStatusCode.BadRequest, respNoToken.StatusCode);
        var noTokenBody = await respNoToken.Content.ReadAsStringAsync();
        using var noTokenDoc = JsonDocument.Parse(noTokenBody);
        Assert.Equal(ApiErrorCodes.GitHubTokenRequired, noTokenDoc.RootElement.GetProperty("code").GetString());

        // Сохраняем PAT
        await client.PutAsJsonAsync("/api/auth/me/github", new GitHubTokenRequestDto { Token = "ghp_syncValidToken123" });

        // Настраиваем коммиты от мок-сервиса GitHub
        var commitHash1 = "1111111111111111111111111111111111111111";
        var commitHash2 = "2222222222222222222222222222222222222222";
        _factory.MockGitHubService
            .Setup(g => g.GetCommitsAsync(project.RepoUrl, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GitHubCommitInfo>
            {
                new(commitHash1, DateTime.UtcNow.AddHours(-3)),
                new(commitHash2, DateTime.UtcNow.AddHours(-1))
            });

        // Act 2: успешная синхронизация с добавлением коммитов
        var syncResp1 = await client.PostAsync($"/api/projects/{project.Id}/github/sync", null);
        Assert.Equal(HttpStatusCode.OK, syncResp1.StatusCode);
        var syncResult1 = await syncResp1.Content.ReadFromJsonAsync<GitHubSyncResultDto>();
        Assert.NotNull(syncResult1);
        Assert.Equal(2, syncResult1.Added);
        Assert.Equal(0, syncResult1.Skipped);

        // Assert в PostgreSQL: оба коммита сохранены
        var countInDb = await _factory.ExecuteDbContextAsync(async db =>
            await db.Commits.CountAsync(c => c.ProjectId == project.Id));
        Assert.Equal(2, countInDb);

        // Act 3: повторная синхронизация тех же коммитов -> Added = 0, Skipped = 2
        var syncResp2 = await client.PostAsync($"/api/projects/{project.Id}/github/sync", null);
        Assert.Equal(HttpStatusCode.OK, syncResp2.StatusCode);
        var syncResult2 = await syncResp2.Content.ReadFromJsonAsync<GitHubSyncResultDto>();
        Assert.NotNull(syncResult2);
        Assert.Equal(0, syncResult2.Added);
        Assert.Equal(2, syncResult2.Skipped);

        // Act 4: ошибка внешнего сервиса RateLimited при синхронизации -> 502 BadGateway
        _factory.MockGitHubService
            .Setup(g => g.GetCommitsAsync(project.RepoUrl, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubIntegrationException(GitHubErrorKind.RateLimited, "Превышен лимит запросов GitHub API."));

        var rateLimitResp = await client.PostAsync($"/api/projects/{project.Id}/github/sync", null);
        Assert.Equal(HttpStatusCode.BadGateway, rateLimitResp.StatusCode);
        var rateLimitBody = await rateLimitResp.Content.ReadAsStringAsync();
        using var rateLimitDoc = JsonDocument.Parse(rateLimitBody);
        Assert.Equal(ApiErrorCodes.GitHubRateLimited, rateLimitDoc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GitHubPat_DataIsolationBetweenUsers_User2CannotAccessOrUseUser1Pat()
    {
        // Arrange: User 1 подключает PAT, User 2 токен не подключает
        var user1 = await CreateTestUserAsync("pat_isolated_user1", "User");
        var user2 = await CreateTestUserAsync("pat_isolated_user2", "User");
        var projectUser1 = await CreateTestProjectAsync("project_u1_pat", user1.Id);
        var projectUser2 = await CreateTestProjectAsync("project_u2_no_pat", user2.Id);

        using var client1 = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(user1));
        using var client2 = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(user2));

        await client1.PutAsJsonAsync("/api/auth/me/github", new GitHubTokenRequestDto { Token = "ghp_u1_secret_token" });

        // Act & Assert 1: User 2 проверяет свой статус -> IsConnected = false (токен User 1 не виден)
        var statusResp2 = await client2.GetAsync("/api/auth/me/github");
        Assert.Equal(HttpStatusCode.OK, statusResp2.StatusCode);
        var status2 = await statusResp2.Content.ReadFromJsonAsync<GitHubIntegrationStatusDto>();
        Assert.NotNull(status2);
        Assert.False(status2.IsConnected);

        // Act & Assert 2: User 2 синхронизирует свой проект -> 400 BadRequest (токен User 1 не используется)
        var syncUser2Resp = await client2.PostAsync($"/api/projects/{projectUser2.Id}/github/sync", null);
        Assert.Equal(HttpStatusCode.BadRequest, syncUser2Resp.StatusCode);

        // Act & Assert 3: User 2 пытается синхронизировать проект User 1 -> 403 Forbidden
        var syncStrangerResp = await client2.PostAsync($"/api/projects/{projectUser1.Id}/github/sync", null);
        Assert.Equal(HttpStatusCode.Forbidden, syncStrangerResp.StatusCode);
    }

    #endregion

    #region 6.9. Сценарий C: Очередь и жизненный цикл задач анализа (Analysis Queue & Lifecycle)

    [Fact]
    public async Task AnalysisQueue_Enqueue_Returns202Accepted_WithLocation_AndStatusPending()
    {
        // Arrange
        var user = await CreateTestUserAsync("queue_enqueuer", "User");
        var project = await CreateTestProjectAsync("queue_project", user.Id);
        var commit = await CreateTestCommitAsync(project.Id, "aaaa1111bbbb2222cccc3333dddd4444eeee5555");
        using var client = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(user));

        // Act: отправка POST для постановки в очередь
        var response = await client.PostAsync($"/api/projects/{project.Id}/commits/{commit.Id}/analyses", null);

        // Assert HTTP
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var dto = await response.Content.ReadFromJsonAsync<AnalysisDto>(JsonOptions);
        Assert.NotNull(dto);
        Assert.True(dto.Id > 0);
        Assert.Equal(commit.Id, dto.CommitId);
        Assert.Equal(AnalysisStatus.Pending, dto.Status);
        Assert.Empty(dto.Results);
        Assert.Null(dto.ErrorMessage);
        Assert.EndsWith($"/api/projects/{project.Id}/commits/{commit.Id}/analyses/{dto.Id}", response.Headers.Location.ToString());

        // Assert в БД PostgreSQL
        var dbAnalysis = await _factory.ExecuteDbContextAsync(async db =>
            await db.Analyses.AsNoTracking().FirstOrDefaultAsync(a => a.Id == dto.Id));

        Assert.NotNull(dbAnalysis);
        Assert.Equal(commit.Id, dbAnalysis.CommitId);
        Assert.Equal(AnalysisStatus.Pending, dbAnalysis.Status);
        Assert.Null(dbAnalysis.StartedAt);
        Assert.Null(dbAnalysis.CompletedAt);
    }

    [Fact]
    public async Task AnalysisQueue_DuplicateEnqueue_WhileActive_Returns409Conflict_WithProblemDetails()
    {
        // Arrange: ставим первую задачу в очередь со статусом Pending
        var user = await CreateTestUserAsync("queue_dup_tester", "User");
        var project = await CreateTestProjectAsync("queue_dup_project", user.Id);
        var commit = await CreateTestCommitAsync(project.Id, "bbbb2222cccc3333dddd4444eeee5555ffff6666");
        using var client = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(user));

        var firstResp = await client.PostAsync($"/api/projects/{project.Id}/commits/{commit.Id}/analyses", null);
        Assert.Equal(HttpStatusCode.Accepted, firstResp.StatusCode);

        // Act: попытка повторной постановки задачи для того же коммита, пока первая еще активна
        var secondResp = await client.PostAsync($"/api/projects/{project.Id}/commits/{commit.Id}/analyses", null);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, secondResp.StatusCode);
        Assert.True(secondResp.Content.Headers.ContentType?.MediaType is "application/problem+json" or "application/json");

        var body = await secondResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(ApiErrorCodes.AnalysisAlreadyQueued, doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnalysisQueue_GetById_OwnerCanRetrieve_VerifiesCommitMembership_OtherUserForbidden()
    {
        // Arrange
        var user = await CreateTestUserAsync("queue_owner_get", "User");
        var stranger = await CreateTestUserAsync("queue_stranger_get", "User");
        var project = await CreateTestProjectAsync("queue_get_proj", user.Id);
        var commit1 = await CreateTestCommitAsync(project.Id, "1111111111111111111111111111111111111111");
        var commit2 = await CreateTestCommitAsync(project.Id, "2222222222222222222222222222222222222222");

        using var clientUser = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(user));
        using var clientStranger = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(stranger));

        // Создаем анализ для commit1
        var enqueueResp = await clientUser.PostAsync($"/api/projects/{project.Id}/commits/{commit1.Id}/analyses", null);
        var created = await enqueueResp.Content.ReadFromJsonAsync<AnalysisDto>(JsonOptions);
        Assert.NotNull(created);

        // Act & Assert 1: владелец получает анализ по правильному маршруту -> 200 OK
        var respValid = await clientUser.GetAsync($"/api/projects/{project.Id}/commits/{commit1.Id}/analyses/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, respValid.StatusCode);
        var fetchedDto = await respValid.Content.ReadFromJsonAsync<AnalysisDto>(JsonOptions);
        Assert.NotNull(fetchedDto);
        Assert.Equal(created.Id, fetchedDto.Id);

        // Act & Assert 2: проверка принадлежности: попытка запросить анализ через commit2 -> 404 NotFound
        var respWrongCommit = await clientUser.GetAsync($"/api/projects/{project.Id}/commits/{commit2.Id}/analyses/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, respWrongCommit.StatusCode);

        // Act & Assert 3: посторонний пользователь -> 403 Forbidden
        var respForbidden = await clientStranger.GetAsync($"/api/projects/{project.Id}/commits/{commit1.Id}/analyses/{created.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, respForbidden.StatusCode);

        // Act & Assert 4: несуществующий анализ -> 404 NotFound
        var respNotFound = await clientUser.GetAsync($"/api/projects/{project.Id}/commits/{commit1.Id}/analyses/999999");
        Assert.Equal(HttpStatusCode.NotFound, respNotFound.StatusCode);
    }

    [Fact]
    public async Task AnalysisQueue_GetByCommitId_ReturnsPagedAnalyses_AndValidatesPagination()
    {
        // Arrange
        var user = await CreateTestUserAsync("queue_list_user", "User");
        var project = await CreateTestProjectAsync("queue_list_proj", user.Id);
        var commit = await CreateTestCommitAsync(project.Id, "3333333333333333333333333333333333333333");
        using var client = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(user));

        var enqueueResp = await client.PostAsync($"/api/projects/{project.Id}/commits/{commit.Id}/analyses", null);
        Assert.Equal(HttpStatusCode.Accepted, enqueueResp.StatusCode);

        // Act 1: запрос списка анализов коммита
        var listResp = await client.GetAsync($"/api/projects/{project.Id}/commits/{commit.Id}/analyses?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var paged = await listResp.Content.ReadFromJsonAsync<PagedResultDto<AnalysisDto>>(JsonOptions);
        Assert.NotNull(paged);
        Assert.Equal(1, paged.TotalCount);
        Assert.Single(paged.Items);

        // Act 2: невалидный номер страницы -> 400 BadRequest
        var invalidResp = await client.GetAsync($"/api/projects/{project.Id}/commits/{commit.Id}/analyses?page=0&pageSize=10");
        Assert.Equal(HttpStatusCode.BadRequest, invalidResp.StatusCode);
        var body = await invalidResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(ApiErrorCodes.ValidationFailed, doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnalysisLifecycle_StatusTransitions_PendingToRunningToCompleted_ReflectedViaHttp()
    {
        // Arrange: создаем проект, коммит и ставим в очередь задачу анализа
        var user = await CreateTestUserAsync("lifecycle_trans_user", "User");
        var project = await CreateTestProjectAsync("lifecycle_proj", user.Id);
        var commit = await CreateTestCommitAsync(project.Id, "4444444444444444444444444444444444444444");
        using var client = _factory.CreateAuthenticatedClient(_factory.GenerateValidToken(user));

        var enqueueResp = await client.PostAsync($"/api/projects/{project.Id}/commits/{commit.Id}/analyses", null);
        Assert.Equal(HttpStatusCode.Accepted, enqueueResp.StatusCode);
        var created = await enqueueResp.Content.ReadFromJsonAsync<AnalysisDto>(JsonOptions);
        Assert.NotNull(created);
        var analysisId = created.Id;

        // Фаза 1: HTTP-запрос возвращает начальный статус Pending
        var pendingResp = await client.GetAsync($"/api/projects/{project.Id}/commits/{commit.Id}/analyses/{analysisId}");
        var pendingDto = await pendingResp.Content.ReadFromJsonAsync<AnalysisDto>(JsonOptions);
        Assert.NotNull(pendingDto);
        Assert.Equal(AnalysisStatus.Pending, pendingDto.Status);
        Assert.Null(pendingDto.StartedAt);

        // Фаза 2: симуляция захвата задачи фоновым воркером в БД PostgreSQL (переход в Running)
        var startedTime = DateTime.UtcNow;
        await _factory.ExecuteDbContextAsync(async db =>
        {
            var analysis = await db.Analyses.FirstAsync(a => a.Id == analysisId);
            analysis.Status = AnalysisStatus.Running;
            analysis.StartedAt = startedTime;
            analysis.ExecutionToken = Guid.NewGuid();
            await db.SaveChangesAsync();
        });

        // HTTP-запрос видит переход в статус Running
        var runningResp = await client.GetAsync($"/api/projects/{project.Id}/commits/{commit.Id}/analyses/{analysisId}");
        var runningDto = await runningResp.Content.ReadFromJsonAsync<AnalysisDto>(JsonOptions);
        Assert.NotNull(runningDto);
        Assert.Equal(AnalysisStatus.Running, runningDto.Status);
        Assert.NotNull(runningDto.StartedAt);

        // Пока задача находится в статусе Running, дублирование по-прежнему отклоняется
        var duplicateWhileRunning = await client.PostAsync($"/api/projects/{project.Id}/commits/{commit.Id}/analyses", null);
        Assert.Equal(HttpStatusCode.Conflict, duplicateWhileRunning.StatusCode);

        // Фаза 3: симуляция успешного завершения анализа фоновым воркером (переход в Completed с результатами)
        var completedTime = DateTime.UtcNow;
        await _factory.ExecuteDbContextAsync(async db =>
        {
            var analysis = await db.Analyses.FirstAsync(a => a.Id == analysisId);
            analysis.Status = AnalysisStatus.Completed;
            analysis.CompletedAt = completedTime;
            analysis.Results.Add(new AnalysisResult
            {
                AnalysisId = analysis.Id,
                FilePath = "src/Backend/Controllers/TestController.cs",
                LineNumber = 15,
                Rule = "SEC001",
                Message = "Обнаружено использование жестко закодированного секрета.",
                Severity = Severity.Error,
                Recommendation = "Перенесите секрет в защищенное хранилище конфигурации.",
                AnalyzerType = AnalyzerType.Security
            });
            await db.SaveChangesAsync();
        });

        // HTTP-запрос подтверждает статус Completed и возвращает сформированные результаты анализа
        var completedResp = await client.GetAsync($"/api/projects/{project.Id}/commits/{commit.Id}/analyses/{analysisId}");
        var completedDto = await completedResp.Content.ReadFromJsonAsync<AnalysisDto>(JsonOptions);
        Assert.NotNull(completedDto);
        Assert.Equal(AnalysisStatus.Completed, completedDto.Status);
        Assert.NotNull(completedDto.CompletedAt);
        Assert.Single(completedDto.Results);
        var result = completedDto.Results[0];
        Assert.Equal("SEC001", result.Rule);
        Assert.Equal(Severity.Error, result.Severity);
        Assert.Equal(AnalyzerType.Security, result.AnalyzerType);
        Assert.Equal(15, result.LineNumber);

        // Фаза 4: после завершения предыдущего анализа постановка нового анализа для того же коммита вновь разрешена
        var newEnqueueResp = await client.PostAsync($"/api/projects/{project.Id}/commits/{commit.Id}/analyses", null);
        Assert.Equal(HttpStatusCode.Accepted, newEnqueueResp.StatusCode);
        var newAnalysisDto = await newEnqueueResp.Content.ReadFromJsonAsync<AnalysisDto>(JsonOptions);
        Assert.NotNull(newAnalysisDto);
        Assert.NotEqual(analysisId, newAnalysisDto.Id);
        Assert.Equal(AnalysisStatus.Pending, newAnalysisDto.Status);
    }

    #endregion

    #region Вспомогательные методы создания тестовых сущностей

    private async Task<User> CreateTestUserAsync(string username, string roleName)
    {
        return await _factory.ExecuteDbContextAsync(async context =>
        {
            var role = await context.Roles.FirstAsync(r => r.Name == roleName);
            var user = new User
            {
                Username = username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("TestSecretPassword123!"),
                RoleId = role.Id,
                Role = role
            };

            context.Users.Add(user);
            await context.SaveChangesAsync();
            return user;
        });
    }

    private async Task<Project> CreateTestProjectAsync(string projectName, int ownerId)
    {
        return await _factory.ExecuteDbContextAsync(async context =>
        {
            var project = new Project
            {
                Name = projectName,
                RepoUrl = $"https://github.com/test-org/{projectName}",
                OwnerId = ownerId
            };

            context.Projects.Add(project);
            await context.SaveChangesAsync();
            return project;
        });
    }

    private async Task<Commit> CreateTestCommitAsync(int projectId, string hash)
    {
        return await _factory.ExecuteDbContextAsync(async context =>
        {
            var commit = new Commit
            {
                ProjectId = projectId,
                CommitHash = hash,
                CommitDate = DateTime.UtcNow.AddHours(-2)
            };

            context.Commits.Add(commit);
            await context.SaveChangesAsync();
            return commit;
        });
    }

    #endregion
}
