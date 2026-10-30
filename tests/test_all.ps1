param(
    [string]$BaseUrl = $null,
    [string]$AdminUsername = $null,
    [string]$AdminPassword = $null,
    [int]$AnalysisTimeoutSec = 0,
    [int]$AnalysisPollIntervalSec = 0
)

$ErrorActionPreference = "Stop"

# Auto-load .env from repository root if it exists
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } elseif ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { (Get-Location).Path }
$projectRoot = Split-Path -Parent $scriptDir
$envPath = Join-Path $projectRoot ".env"

if (Test-Path $envPath) {
    Get-Content $envPath | ForEach-Object {
        $line = $_.Trim()
        if (-not [string]::IsNullOrWhiteSpace($line) -and -not $line.StartsWith("#") -and $line.Contains("=")) {
            $parts = $line.Split("=", 2)
            $varName = $parts[0].Trim()
            $varVal = $parts[1].Trim()
            if (-not [string]::IsNullOrWhiteSpace($varName) -and [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($varName))) {
                [Environment]::SetEnvironmentVariable($varName, $varVal)
            }
        }
    }
}

if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    $BaseUrl = if ($env:API_BASE_URL) { $env:API_BASE_URL } elseif ($env:BASE_URL) { $env:BASE_URL } else { "https://localhost:7033" }
}

if ([string]::IsNullOrWhiteSpace($AdminUsername)) {
    $AdminUsername = if ($env:API_ADMIN_USERNAME) { $env:API_ADMIN_USERNAME } else { $null }
}

if ([string]::IsNullOrWhiteSpace($AdminPassword)) {
    $AdminPassword = if ($env:API_ADMIN_PASSWORD) { $env:API_ADMIN_PASSWORD } else { $null }
}

if ($AnalysisTimeoutSec -le 0) {
    $AnalysisTimeoutSec = if ($env:ANALYSIS_TIMEOUT_SEC) { [int]$env:ANALYSIS_TIMEOUT_SEC } else { 120 }
}

if ($AnalysisPollIntervalSec -le 0) {
    $AnalysisPollIntervalSec = if ($env:ANALYSIS_POLL_INTERVAL_SEC) { [int]$env:ANALYSIS_POLL_INTERVAL_SEC } else { 2 }
}

if ([string]::IsNullOrWhiteSpace($AdminUsername) -or [string]::IsNullOrWhiteSpace($AdminPassword)) {
    Write-Host "CRITICAL CONFIGURATION ERROR:" -ForegroundColor Red
    Write-Host "Admin credentials (API_ADMIN_USERNAME, API_ADMIN_PASSWORD) are not specified." -ForegroundColor Red
    Write-Host "Please set them via .env, environment variables, or script parameters." -ForegroundColor Yellow
    exit 1
}

# Trust development self-signed certificates
try {
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
} catch {}

try {
    Add-Type -TypeDefinition @"
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public static class SslPolicyUtil {
    public static void DisableValidation() {
        ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) => true;
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
    }
}
"@ -Language CSharp -ErrorAction SilentlyContinue
    [SslPolicyUtil]::DisableValidation()
} catch {}

$RunGuid = [System.Guid]::NewGuid().ToString("N").Substring(0, 8)
$Timestamp = (Get-Date -Format "yyyyMMddHHmmss")
$RunId = "${Timestamp}_${RunGuid}"

$TestUser1Name = "TestUser1_" + $RunId
$TestUser1Password = "TestPass123!"
$TestUser1ChangedName = $TestUser1Name + "_Changed"
$TestUser1ChangedPass = "ChangedPass123!"

$TestUser2Name = "TestUser2_" + $RunId
$TestUser2Password = "TestPass223!"

$TestProjectName = "TestProject_" + $RunId
$TestProjectNameUpdated = $TestProjectName + "_Updated"
$TestProjectNameDuplicate = "TestProject_Dup_" + $RunId

$TestRepoUrl = "https://github.com/Vol4ok-69/CourseProject"
$TestCommitHash = "e9ef5e43b99da445bb548b049d52e0e7cbf2a5ec"

$script:AdminToken = $null
$script:AdminUserId = $null

$script:User1Token = $null
$script:User1Id = $null

$script:User2Token = $null
$script:User2Id = $null

$script:TestProjectId = $null
$script:TestCommitId = $null
$script:TestAnalysisId = $null

$script:CreatedProjectIds = New-Object System.Collections.Generic.List[int]
$script:CreatedUserIds = New-Object System.Collections.Generic.List[int]

$script:Passed = 0
$script:Failed = 0
$script:HadCriticalError = $false


function Write-TestResult($Name, $Success, $Details = $null) {
    if ($Success) {
        Write-Host "[PASS] $Name" -ForegroundColor Green
        $script:Passed++
    }
    else {
        if ($Details) {
            Write-Host "[FAIL] $Name - $Details" -ForegroundColor Red
        } else {
            Write-Host "[FAIL] $Name" -ForegroundColor Red
        }
        $script:Failed++
    }
}


function Assert-Status($Response, $ExpectedStatus, $Name) {
    $actual = [int]$Response.StatusCode

    if ($actual -ne $ExpectedStatus) {
        Write-TestResult $Name $false "Expected HTTP $ExpectedStatus, got HTTP $actual. Response: $($Response.Content)"
        throw "$Name failed. Expected HTTP $ExpectedStatus, got HTTP $actual. Response: $($Response.Content)"
    }

    Write-TestResult $Name $true
}


function Invoke-Api {
    param(
        [string]$Method,
        [string]$Uri,
        [string]$Token = $null,
        [object]$Body = $null
    )

    $headers = @{}

    if ($Token) {
        $headers["Authorization"] = "Bearer $Token"
    }

    $params = @{
        Method = $Method
        Uri = "$BaseUrl$Uri"
        Headers = $headers
        UseBasicParsing = $true
    }

    if ($null -ne $Body) {
        $params["ContentType"] = "application/json"
        $params["Body"] = ($Body | ConvertTo-Json -Depth 10)
    }

    try {
        return Invoke-WebRequest @params
    }
    catch [System.Net.WebException] {
        $response = $_.Exception.Response

        if ($null -eq $response) {
            throw "Request failed: $Method $Uri. $($_.Exception.Message)"
        }

        $stream = $response.GetResponseStream()
        $reader = New-Object System.IO.StreamReader($stream)
        $content = $reader.ReadToEnd()

        $reader.Dispose()
        $stream.Dispose()

        return [PSCustomObject]@{
            StatusCode = [int]$response.StatusCode
            Content = $content
            Headers = $response.Headers
        }
    }
}


function Get-Json($Response) {
    if ([string]::IsNullOrWhiteSpace($Response.Content)) {
        return $null
    }

    return $Response.Content | ConvertFrom-Json
}


function Get-Token($Username, $Password) {
    $response = Invoke-Api `
        -Method "POST" `
        -Uri "/api/auth/login" `
        -Body @{
            Username = $Username
            Password = $Password
        }

    Assert-Status $response 200 "Login: $Username"

    $body = Get-Json $response

    if ([string]::IsNullOrWhiteSpace($body.accessToken)) {
        throw "Login for $Username returned no access token."
    }

    return $body.accessToken
}


function Register-User($Username, $Password) {
    $response = Invoke-Api `
        -Method "POST" `
        -Uri "/api/auth/register" `
        -Body @{
            Username = $Username
            Password = $Password
        }

    Assert-Status $response 200 "Register user: $Username"

    $body = Get-Json $response
    if ([string]::IsNullOrWhiteSpace($body.accessToken)) {
        throw "Registration for $Username returned no access token."
    }

    $token = $body.accessToken

    $usersResponse = Invoke-Api `
        -Method "GET" `
        -Uri "/api/admin/users" `
        -Token $script:AdminToken

    Assert-Status $usersResponse 200 "Get users list to resolve id for $Username"

    $users = Get-Json $usersResponse
    $userRecord = $users | Where-Object { $_.username -eq $Username } | Select-Object -First 1

    if ($null -eq $userRecord) {
        throw "User $Username registered successfully but not found in admin users list."
    }

    $userId = [int]$userRecord.id
    $script:CreatedUserIds.Add($userId)

    return @{
        Token = $token
        UserId = $userId
    }
}


function Test-Authentication {
    Write-Host ""
    Write-Host "=== 1. AUTHENTICATION & LOGIN ===" -ForegroundColor Cyan

    $script:AdminToken = Get-Token $AdminUsername $AdminPassword

    $invalidLogin = Invoke-Api `
        -Method "POST" `
        -Uri "/api/auth/login" `
        -Body @{
            Username = "nonexistent_user_99999"
            Password = "WrongPassword123!"
        }

    Assert-Status $invalidLogin 401 "Invalid credentials return 401 Unauthorized"

    $adminUsersResponse = Invoke-Api `
        -Method "GET" `
        -Uri "/api/admin/users" `
        -Token $script:AdminToken

    Assert-Status $adminUsersResponse 200 "Admin access to /api/admin/users"

    $users = Get-Json $adminUsersResponse
    $admin = $users | Where-Object { $_.username -eq $AdminUsername } | Select-Object -First 1

    if ($null -eq $admin) {
        throw "Admin user $AdminUsername was not found in users list."
    }

    $script:AdminUserId = [int]$admin.id
    Write-TestResult "Resolved Admin User Id: $script:AdminUserId" $true

    $user1Reg = Register-User $TestUser1Name $TestUser1Password
    $script:User1Token = $user1Reg.Token
    $script:User1Id = $user1Reg.UserId

    $user2Reg = Register-User $TestUser2Name $TestUser2Password
    $script:User2Token = $user2Reg.Token
    $script:User2Id = $user2Reg.UserId

    $dupRegister = Invoke-Api `
        -Method "POST" `
        -Uri "/api/auth/register" `
        -Body @{
            Username = $TestUser1Name
            Password = "AnotherPassword123!"
        }

    Assert-Status $dupRegister 400 "Duplicate username registration rejected (400)"
}


function Test-UserManagement {
    Write-Host ""
    Write-Host "=== 2. USER MANAGEMENT & PROFILE ===" -ForegroundColor Cyan

    $getUser = Invoke-Api `
        -Method "GET" `
        -Uri "/api/admin/users/$script:User1Id" `
        -Token $script:AdminToken

    Assert-Status $getUser 200 "Admin gets user by id"
    $userObj = Get-Json $getUser

    if ($userObj.username -ne $TestUser1Name) {
        throw "Expected username $TestUser1Name, got $($userObj.username)"
    }
    Write-TestResult "User details match expected username" $true

    $duplicateUsername = Invoke-Api `
        -Method "PUT" `
        -Uri "/api/admin/users/$script:User1Id" `
        -Token $script:AdminToken `
        -Body @{
            Username = $AdminUsername
        }

    Assert-Status $duplicateUsername 409 "Admin update duplicate username rejected (409)"

    $updateUser = Invoke-Api `
        -Method "PUT" `
        -Uri "/api/admin/users/$script:User1Id" `
        -Token $script:AdminToken `
        -Body @{
            Username = $TestUser1Name
        }

    Assert-Status $updateUser 200 "Admin updates test user 1"

    $userAccessAdminUsers = Invoke-Api `
        -Method "GET" `
        -Uri "/api/admin/users" `
        -Token $script:User1Token

    Assert-Status $userAccessAdminUsers 403 "Regular user cannot access admin users list (403)"

    $userChangeRole = Invoke-Api `
        -Method "PUT" `
        -Uri "/api/admin/users/$script:User1Id/role" `
        -Token $script:User1Token `
        -Body @{
            RoleId = 1
        }

    Assert-Status $userChangeRole 403 "Regular user cannot change role (403)"

    if ($script:AdminUserId) {
        $adminChangeOwnRole = Invoke-Api `
            -Method "PUT" `
            -Uri "/api/admin/users/$script:AdminUserId/role" `
            -Token $script:AdminToken `
            -Body @{
                RoleId = 2
            }

        Assert-Status $adminChangeOwnRole 403 "Admin cannot change own role (403)"
    }

    $adminUpdateRole = Invoke-Api `
        -Method "PUT" `
        -Uri "/api/admin/users/$script:User2Id/role" `
        -Token $script:AdminToken `
        -Body @{
            RoleId = 1
        }

    Assert-Status $adminUpdateRole 200 "Admin promotes User 2 to Admin role"

    $adminRestoreRole = Invoke-Api `
        -Method "PUT" `
        -Uri "/api/admin/users/$script:User2Id/role" `
        -Token $script:AdminToken `
        -Body @{
            RoleId = 2
        }

    Assert-Status $adminRestoreRole 200 "Admin demotes User 2 back to User role"

    $changeUsername = Invoke-Api `
        -Method "PUT" `
        -Uri "/api/auth/me/username" `
        -Token $script:User1Token `
        -Body @{
            Username = $TestUser1ChangedName
        }

    Assert-Status $changeUsername 200 "User changes own username"
    $script:User1Token = Get-Token $TestUser1ChangedName $TestUser1Password

    $changePassword = Invoke-Api `
        -Method "PUT" `
        -Uri "/api/auth/me/password" `
        -Token $script:User1Token `
        -Body @{
            CurrentPassword = $TestUser1Password
            NewPassword = $TestUser1ChangedPass
        }

    Assert-Status $changePassword 200 "User changes own password"
    $script:User1Token = Get-Token $TestUser1ChangedName $TestUser1ChangedPass
}


function Test-Projects {
    Write-Host ""
    Write-Host "=== 3. PROJECTS ===" -ForegroundColor Cyan

    $createResponse = Invoke-Api `
        -Method "POST" `
        -Uri "/api/projects" `
        -Token $script:User1Token `
        -Body @{
            Name = $TestProjectName
            RepoUrl = $TestRepoUrl
        }

    Assert-Status $createResponse 201 "User 1 creates project (201)"

    $project = Get-Json $createResponse

    if ($project.id -le 0) {
        throw "Create project returned invalid id."
    }

    $script:TestProjectId = [int]$project.id
    $script:CreatedProjectIds.Add($script:TestProjectId)

    if ([int]$project.ownerId -ne $script:User1Id) {
        throw "Created project has unexpected owner."
    }

    Write-TestResult "Project owner matches User 1 id" $true

    $getResponse = Invoke-Api `
        -Method "GET" `
        -Uri "/api/projects/$script:TestProjectId" `
        -Token $script:User1Token

    Assert-Status $getResponse 200 "User 1 gets own project by id"

    $projectsResponse = Invoke-Api `
        -Method "GET" `
        -Uri "/api/projects/owner/$script:User1Id" `
        -Token $script:User1Token

    Assert-Status $projectsResponse 200 "User 1 gets own projects by owner id"

    $projectList = Get-Json $projectsResponse

    if (-not ($projectList | Where-Object { $_.id -eq $script:TestProjectId })) {
        throw "Created project was not found in owner's projects list."
    }

    Write-TestResult "Created project exists in owner's projects list" $true

    $updateResponse = Invoke-Api `
        -Method "PUT" `
        -Uri "/api/projects/$script:TestProjectId" `
        -Token $script:User1Token `
        -Body @{
            Name = $TestProjectNameUpdated
            RepoUrl = $TestRepoUrl
        }

    Assert-Status $updateResponse 200 "User 1 updates own project"

    $updatedProject = Get-Json $updateResponse

    if ($updatedProject.name -ne $TestProjectNameUpdated) {
        throw "Project name was not updated."
    }

    Write-TestResult "Project name successfully updated" $true

    # Check 1: User 1 cannot create duplicate project with same name (409 Conflict)
    $dupCreateResponse = Invoke-Api `
        -Method "POST" `
        -Uri "/api/projects" `
        -Token $script:User1Token `
        -Body @{
            Name = $TestProjectNameUpdated
            RepoUrl = $TestRepoUrl
        }

    Assert-Status $dupCreateResponse 409 "User 1 cannot create duplicate project with same name (409 Conflict)"

    # Check 2: User 1 creates second project with different name
    $secondProjResponse = Invoke-Api `
        -Method "POST" `
        -Uri "/api/projects" `
        -Token $script:User1Token `
        -Body @{
            Name = $TestProjectNameDuplicate
            RepoUrl = $TestRepoUrl
        }

    Assert-Status $secondProjResponse 201 "User 1 creates second project (201)"
    $secondProject = Get-Json $secondProjResponse
    $script:CreatedProjectIds.Add([int]$secondProject.id)

    # Check 3: User 1 attempts to rename second project to name of first project (409 Conflict)
    $dupUpdateResponse = Invoke-Api `
        -Method "PUT" `
        -Uri "/api/projects/$($secondProject.id)" `
        -Token $script:User1Token `
        -Body @{
            Name = $TestProjectNameUpdated
            RepoUrl = $TestRepoUrl
        }

    Assert-Status $dupUpdateResponse 409 "User 1 cannot update project to duplicate name of own project (409 Conflict)"

    # Check 4: User 2 can create project with same name under different owner (201 Created)
    $u2ProjResponse = Invoke-Api `
        -Method "POST" `
        -Uri "/api/projects" `
        -Token $script:User2Token `
        -Body @{
            Name = $TestProjectNameUpdated
            RepoUrl = $TestRepoUrl
        }

    Assert-Status $u2ProjResponse 201 "User 2 can create project with same name under different owner (201)"
    $u2Project = Get-Json $u2ProjResponse
    $script:CreatedProjectIds.Add([int]$u2Project.id)

    $adminGetResponse = Invoke-Api `
        -Method "GET" `
        -Uri "/api/projects/$script:TestProjectId" `
        -Token $script:AdminToken

    Assert-Status $adminGetResponse 200 "Admin gets test project"
}


function Test-Commits {
    Write-Host ""
    Write-Host "=== 4. COMMITS ===" -ForegroundColor Cyan

    $createResponse = Invoke-Api `
        -Method "POST" `
        -Uri "/api/projects/$script:TestProjectId/commits" `
        -Token $script:User1Token `
        -Body @{
            CommitHash = $TestCommitHash
            CommitDate = (Get-Date).ToUniversalTime().ToString("o")
        }

    Assert-Status $createResponse 201 "User 1 creates commit (201)"

    $commit = Get-Json $createResponse

    if ($commit.id -le 0) {
        throw "Create commit returned invalid id."
    }

    $script:TestCommitId = [int]$commit.id

    $duplicateCommit = Invoke-Api `
        -Method "POST" `
        -Uri "/api/projects/$script:TestProjectId/commits" `
        -Token $script:User1Token `
        -Body @{
            CommitHash = $TestCommitHash
            CommitDate = (Get-Date).ToUniversalTime().ToString("o")
        }

    Assert-Status $duplicateCommit 400 "Duplicate commit in same project rejected (400)"

    $getCommitsResponse = Invoke-Api `
        -Method "GET" `
        -Uri "/api/projects/$script:TestProjectId/commits" `
        -Token $script:User1Token

    Assert-Status $getCommitsResponse 200 "User 1 gets commits list"

    $commitsList = Get-Json $getCommitsResponse

    if (-not ($commitsList | Where-Object { $_.id -eq $script:TestCommitId })) {
        throw "Created commit was not found in commits list."
    }

    Write-TestResult "Created commit exists in commits list" $true

    $getCommitResponse = Invoke-Api `
        -Method "GET" `
        -Uri "/api/projects/$script:TestProjectId/commits/$script:TestCommitId" `
        -Token $script:User1Token

    Assert-Status $getCommitResponse 200 "User 1 gets commit by id"

    $adminGetCommit = Invoke-Api `
        -Method "GET" `
        -Uri "/api/projects/$script:TestProjectId/commits/$script:TestCommitId" `
        -Token $script:AdminToken

    Assert-Status $adminGetCommit 200 "Admin gets commit"
}


function Test-GitHubSync {
    Write-Host ""
    Write-Host "=== 5. GITHUB SYNC ===" -ForegroundColor Cyan

    $syncResponse = Invoke-Api `
        -Method "POST" `
        -Uri "/api/projects/$script:TestProjectId/github/sync" `
        -Token $script:User1Token

    Assert-Status $syncResponse 200 "User 1 synchronizes project with GitHub"

    $syncResult = Get-Json $syncResponse

    $totalProcessed = [int]$syncResult.added + [int]$syncResult.skipped

    if ($totalProcessed -le 0) {
        throw "GitHub sync processed zero commits (Added: $($syncResult.added), Skipped: $($syncResult.skipped))."
    }

    Write-TestResult "GitHub sync succeeded (Added: $($syncResult.added), Skipped: $($syncResult.skipped))" $true

    $commitsAfterSync = Invoke-Api `
        -Method "GET" `
        -Uri "/api/projects/$script:TestProjectId/commits" `
        -Token $script:User1Token

    Assert-Status $commitsAfterSync 200 "User 1 gets commits list after GitHub sync"
    $commits = Get-Json $commitsAfterSync

    if ($commits.Count -lt $totalProcessed) {
        throw "Stored commits count ($($commits.Count)) is less than reported by sync ($totalProcessed)."
    }

    Write-TestResult "Commits successfully populated in database (Total: $($commits.Count))" $true
}


function Test-Analyses {
    Write-Host ""
    Write-Host "=== 6. ANALYSES (DURABLE QUEUE & BACKGROUND SERVICE) ===" -ForegroundColor Cyan

    $triggerResponse = Invoke-Api `
        -Method "POST" `
        -Uri "/api/projects/$script:TestProjectId/commits/$script:TestCommitId/analyses" `
        -Token $script:User1Token

    Assert-Status $triggerResponse 202 "Trigger analysis accepted into durable queue (202 Accepted)"

    $initialAnalysis = Get-Json $triggerResponse

    if ($initialAnalysis.id -le 0) {
        throw "Initial analysis returned invalid id."
    }

    $script:TestAnalysisId = [int]$initialAnalysis.id

    if ($initialAnalysis.status -ne 0 -and $initialAnalysis.status -ne 1) {
        throw "Expected initial status Pending (0) or Running (1), got $($initialAnalysis.status)"
    }

    Write-TestResult "Initial analysis status is Pending or Running (Status: $($initialAnalysis.status))" $true

    Write-Host "Polling analysis $script:TestAnalysisId until completion (Timeout: ${AnalysisTimeoutSec}s)..." -ForegroundColor Yellow

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $finalAnalysis = $null
    $lastStatus = -1

    while ($stopwatch.Elapsed.TotalSeconds -lt $AnalysisTimeoutSec) {
        Start-Sleep -Seconds $AnalysisPollIntervalSec

        $pollResponse = Invoke-Api `
            -Method "GET" `
            -Uri "/api/projects/$script:TestProjectId/commits/$script:TestCommitId/analyses/$script:TestAnalysisId" `
            -Token $script:User1Token

        if ([int]$pollResponse.StatusCode -ne 200) {
            throw "Polling analysis failed with status $([int]$pollResponse.StatusCode)"
        }

        $analysis = Get-Json $pollResponse
        $lastStatus = [int]$analysis.status

        Write-Host " - Elapsed: $([int]$stopwatch.Elapsed.TotalSeconds)s | Status: $lastStatus" -ForegroundColor DarkGray

        if ($lastStatus -eq 2 -or $lastStatus -eq 3) {
            $finalAnalysis = $analysis
            break
        }
        elseif ($lastStatus -eq 0 -or $lastStatus -eq 1) {
            continue
        }
        else {
            throw "Received unexpected analysis status: $lastStatus"
        }
    }
    $stopwatch.Stop()

    if ($lastStatus -eq 2) {
        Write-TestResult "Background analysis completed successfully (Status 2)" $true

        if ($null -eq $finalAnalysis.startedAt) {
            throw "Completed analysis has missing startedAt timestamp."
        }
        Write-TestResult "Completed analysis has startedAt timestamp" $true

        if ($null -eq $finalAnalysis.completedAt) {
            throw "Completed analysis has missing completedAt timestamp."
        }
        Write-TestResult "Completed analysis has completedAt timestamp" $true

        if ($null -eq $finalAnalysis.results) {
            throw "Completed analysis has null results collection."
        }
        Write-TestResult "Completed analysis contains results collection (Findings: $($finalAnalysis.results.Count))" $true
    }
    elseif ($lastStatus -eq 3) {
        $diagMsg = if ($finalAnalysis.errorMessage) { $finalAnalysis.errorMessage } else { "No detailed error message" }
        Write-TestResult "Background analysis failed" $false "Status 3 (Failed). Diagnosis: $diagMsg"
        throw "Analysis $script:TestAnalysisId failed during background execution. $diagMsg"
    }
    else {
        Write-TestResult "Background analysis polling timed out" $false "Last status was $lastStatus after ${AnalysisTimeoutSec}s."
        throw "Analysis $script:TestAnalysisId did not complete within ${AnalysisTimeoutSec} seconds. Last status: $lastStatus"
    }

    $getAnalysesResponse = Invoke-Api `
        -Method "GET" `
        -Uri "/api/projects/$script:TestProjectId/commits/$script:TestCommitId/analyses" `
        -Token $script:User1Token

    Assert-Status $getAnalysesResponse 200 "User 1 gets commit analyses list"
    $analysesList = Get-Json $getAnalysesResponse

    if (-not ($analysesList | Where-Object { [int]$_.id -eq $script:TestAnalysisId })) {
        throw "Completed analysis was not found in commit analyses list."
    }
    Write-TestResult "Analysis exists in commit analyses list" $true

    $getAnalysisResponse = Invoke-Api `
        -Method "GET" `
        -Uri "/api/projects/$script:TestProjectId/commits/$script:TestCommitId/analyses/$script:TestAnalysisId" `
        -Token $script:User1Token

    Assert-Status $getAnalysisResponse 200 "User 1 gets analysis by id"
    $analysisById = Get-Json $getAnalysisResponse

    if ([int]$analysisById.id -ne $script:TestAnalysisId) {
        throw "Returned analysis id does not match requested id."
    }
    Write-TestResult "Analysis by id matches requested id" $true
}


function Test-CrossUserAuthorization {
    Write-Host ""
    Write-Host "=== 7. CROSS-USER AUTHORIZATION & IDOR ISOLATION ===" -ForegroundColor Cyan

    $noTokenProject = Invoke-Api `
        -Method "GET" `
        -Uri "/api/projects/$script:TestProjectId"

    Assert-Status $noTokenProject 401 "Project request without JWT rejected (401)"

    $noTokenCommit = Invoke-Api `
        -Method "GET" `
        -Uri "/api/projects/$script:TestProjectId/commits"

    Assert-Status $noTokenCommit 401 "Commit request without JWT rejected (401)"

    $adminProject = Invoke-Api `
        -Method "GET" `
        -Uri "/api/projects/$script:TestProjectId" `
        -Token $script:AdminToken

    Assert-Status $adminProject 200 "Admin access to test project allowed (200)"

    $nonexistentProject = Invoke-Api `
        -Method "GET" `
        -Uri "/api/projects/999999" `
        -Token $script:User1Token

    Assert-Status $nonexistentProject 404 "Nonexistent project returns 404"

    $nonexistentCommit = Invoke-Api `
        -Method "GET" `
        -Uri "/api/projects/$script:TestProjectId/commits/999999" `
        -Token $script:User1Token

    Assert-Status $nonexistentCommit 404 "Nonexistent commit returns 404"

    $u2GetProject = Invoke-Api `
        -Method "GET" `
        -Uri "/api/projects/$script:TestProjectId" `
        -Token $script:User2Token

    Assert-Status $u2GetProject 403 "User 2 cannot read User 1's project (403)"

    $u2UpdateProject = Invoke-Api `
        -Method "PUT" `
        -Uri "/api/projects/$script:TestProjectId" `
        -Token $script:User2Token `
        -Body @{
            Name = "HackedProjectName"
            RepoUrl = $TestRepoUrl
        }

    Assert-Status $u2UpdateProject 403 "User 2 cannot update User 1's project (403)"

    $u2DeleteProject = Invoke-Api `
        -Method "DELETE" `
        -Uri "/api/projects/$script:TestProjectId" `
        -Token $script:User2Token

    Assert-Status $u2DeleteProject 403 "User 2 cannot delete User 1's project (403)"

    $u2GetOwnerProjects = Invoke-Api `
        -Method "GET" `
        -Uri "/api/projects/owner/$script:User1Id" `
        -Token $script:User2Token

    Assert-Status $u2GetOwnerProjects 403 "User 2 cannot list User 1's projects by owner id (403)"

    $u2GetCommits = Invoke-Api `
        -Method "GET" `
        -Uri "/api/projects/$script:TestProjectId/commits" `
        -Token $script:User2Token

    Assert-Status $u2GetCommits 403 "User 2 cannot list User 1's commits (403)"

    $u2GetCommitById = Invoke-Api `
        -Method "GET" `
        -Uri "/api/projects/$script:TestProjectId/commits/$script:TestCommitId" `
        -Token $script:User2Token

    Assert-Status $u2GetCommitById 403 "User 2 cannot get User 1's commit by id (403)"

    $u2CreateCommit = Invoke-Api `
        -Method "POST" `
        -Uri "/api/projects/$script:TestProjectId/commits" `
        -Token $script:User2Token `
        -Body @{
            CommitHash = "0000000000000000000000000000000000000000"
            CommitDate = (Get-Date).ToUniversalTime().ToString("o")
        }

    Assert-Status $u2CreateCommit 403 "User 2 cannot create commit in User 1's project (403)"

    $u2GetAnalyses = Invoke-Api `
        -Method "GET" `
        -Uri "/api/projects/$script:TestProjectId/commits/$script:TestCommitId/analyses" `
        -Token $script:User2Token

    Assert-Status $u2GetAnalyses 403 "User 2 cannot list User 1's analyses (403)"

    $u2CreateAnalysis = Invoke-Api `
        -Method "POST" `
        -Uri "/api/projects/$script:TestProjectId/commits/$script:TestCommitId/analyses" `
        -Token $script:User2Token

    Assert-Status $u2CreateAnalysis 403 "User 2 cannot trigger analysis in User 1's project (403)"

    $u2GetAnalysisById = Invoke-Api `
        -Method "GET" `
        -Uri "/api/projects/$script:TestProjectId/commits/$script:TestCommitId/analyses/$script:TestAnalysisId" `
        -Token $script:User2Token

    Assert-Status $u2GetAnalysisById 403 "User 2 cannot get User 1's analysis by id (403)"
}


function Cleanup-TestData {
    Write-Host ""
    Write-Host "=== CLEANUP ===" -ForegroundColor Cyan

    if (-not $script:AdminToken) {
        try {
            $script:AdminToken = Get-Token $AdminUsername $AdminPassword
        } catch {}
    }

    foreach ($projId in $script:CreatedProjectIds) {
        try {
            $delResp = Invoke-Api `
                -Method "DELETE" `
                -Uri "/api/projects/$projId" `
                -Token $script:AdminToken

            $st = [int]$delResp.StatusCode
            if ($st -eq 204 -or $st -eq 404) {
                Write-Host "[CLEANUP] Deleted test project $projId" -ForegroundColor DarkGray
            } else {
                Write-Host "[CLEANUP WARN] Failed to delete test project $projId (Status $st)" -ForegroundColor Yellow
            }
        } catch {
            Write-Host "[CLEANUP WARN] Error deleting project ${projId}: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    foreach ($userId in $script:CreatedUserIds) {
        if ($script:AdminUserId -and $userId -eq $script:AdminUserId) {
            continue
        }

        try {
            $delUserResp = Invoke-Api `
                -Method "DELETE" `
                -Uri "/api/admin/users/$userId" `
                -Token $script:AdminToken

            $st = [int]$delUserResp.StatusCode
            if ($st -eq 204 -or $st -eq 404) {
                Write-Host "[CLEANUP] Deleted test user $userId" -ForegroundColor DarkGray
            } else {
                Write-Host "[CLEANUP WARN] Failed to delete test user $userId (Status $st)" -ForegroundColor Yellow
            }
        } catch {
            Write-Host "[CLEANUP WARN] Error deleting user ${userId}: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}


try {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "       COURSE PROJECT API TESTS" -ForegroundColor Cyan
    Write-Host "Target: $BaseUrl" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan

    Test-Authentication
    Test-UserManagement
    Test-Projects
    Test-Commits
    Test-GitHubSync
    Test-Analyses
    Test-CrossUserAuthorization
}
catch {
    $script:HadCriticalError = $true
    $script:Failed++
    Write-Host ""
    Write-Host "TEST RUN FAILED: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    Cleanup-TestData

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Passed: $script:Passed" -ForegroundColor Green
    if ($script:Failed -gt 0) {
        Write-Host "Failed: $script:Failed" -ForegroundColor Red
    } else {
        Write-Host "Failed: $script:Failed" -ForegroundColor Green
    }
    Write-Host "========================================" -ForegroundColor Cyan
}

if ($script:Failed -gt 0 -or $script:HadCriticalError) {
    exit 1
}

exit 0
