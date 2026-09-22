# Stage A — Windows Quota Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a local-only Windows 11 tray Bridge that owns Codex App Server, completes ChatGPT login, safely normalizes the required quota windows, maintains current state and 24-hour SQLite history, and survives Codex/history failures without exposing any LAN API yet.

**Architecture:** Domain types live in `CodexQuota.Core`; Codex JSONL/JSON-RPC and child-process lifecycle live in `CodexQuota.Codex`; SQLite and settings live in `CodexQuota.Storage`; the WPF tray shell in `CodexQuota.Desktop` composes them through hosted services. Current quota is always in memory and history writes are secondary side effects.

**Tech Stack:** C# 12, .NET 8, WPF, `System.Text.Json`, `Microsoft.Data.Sqlite`, `Microsoft.Extensions.Hosting`, built-in `ILogger`, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-22-codex-quota-bridge-design.md`

## Global Constraints

- Target `net8.0` for class libraries and `net8.0-windows` for the desktop host.
- Start Codex App Server as an explicitly versioned child process using stdio JSONL; do not attach to Codex Desktop internals.
- Initialization sends one `initialize` request and then one `initialized` notification before any account method.
- ChatGPT login uses App Server `account/login/start` with `{ "type": "chatgpt", "useHostedLoginSuccessPage": true, "appBrand": "chatgpt" }` and opens the returned `authUrl` in the system browser.
- Quota initial/read/watchdog path uses `account/rateLimits/read`; primary event path uses `account/rateLimits/updated`.
- Required logical windows are 300-minute short window and 10080-minute weekly window when those are present; if the source cannot map both unambiguously, do not fabricate values.
- Database timestamps are UTC; visible history is 24 hours and retention cleanup uses a 25-hour cutoff.
- `/quota` and all LAN/network work are out of scope for Stage A.

## Review Focus

1. Source responses with duplicate 300-minute buckets must be rejected as ambiguous, not pick the first.
2. A malformed JSON line from App Server must fail the request/connection cleanly without corrupting the last trusted snapshot.
3. SQLite write failure must not replace a valid in-memory snapshot with an error state.
4. Repeated child crashes (five within five minutes) must stop automatic restart and expose `CodexFaulted`.
5. Login cancellation/failure must return to `AuthRequired` without crashing or busy-looping.

---

### Task 1: Scaffold the Windows solution and canonical domain types

**Files:**
- Create: `src/windows/CodexQuota.sln`
- Create: `src/windows/Directory.Build.props`
- Create: `src/windows/CodexQuota.Core/CodexQuota.Core.csproj`
- Create: `src/windows/CodexQuota.Core/Quota/QuotaWindow.cs`
- Create: `src/windows/CodexQuota.Core/Quota/QuotaSnapshot.cs`
- Create: `src/windows/CodexQuota.Core/Quota/QuotaSourceStatus.cs`
- Create: `src/windows/CodexQuota.Core/Quota/QuotaEvent.cs`
- Create: `src/windows/CodexQuota.Core/Quota/HistoryPoint.cs`
- Create: `src/windows/CodexQuota.Core/Quota/IQuotaStateStore.cs`
- Create: `src/windows/CodexQuota.Core/Quota/InMemoryQuotaStateStore.cs`
- Create: `tests/windows/CodexQuota.Core.Tests/CodexQuota.Core.Tests.csproj`
- Create: `tests/windows/CodexQuota.Core.Tests/InMemoryQuotaStateStoreTests.cs`

**Interfaces:**
- Produces: `record QuotaWindow(double UsedPercent, double RemainingPercent, int WindowMinutes, DateTimeOffset ResetsAt)`
- Produces: `record QuotaSnapshot(int SchemaVersion, DateTimeOffset GeneratedAt, string Source, QuotaSourceStatus Status, DateTimeOffset? LastSuccessfulSyncAt, QuotaWindow ShortWindow, QuotaWindow Weekly)`
- Produces: `interface IQuotaStateStore { QuotaSnapshot? Current { get; } long Sequence { get; } QuotaStateUpdate Replace(QuotaSnapshot snapshot); }`
- Produces: `record QuotaStateUpdate(long Sequence, QuotaSnapshot? Previous, QuotaSnapshot Current)`

- [ ] **Step 1: Write the failing state-store tests**

```csharp
[Fact]
public void Replace_IncrementsSequenceAndReturnsPreviousSnapshot()
{
    var store = new InMemoryQuotaStateStore();
    var first = Fixtures.Snapshot(shortRemaining: 80, weeklyRemaining: 60);
    var second = Fixtures.Snapshot(shortRemaining: 70, weeklyRemaining: 55);

    var update1 = store.Replace(first);
    var update2 = store.Replace(second);

    Assert.Equal(1, update1.Sequence);
    Assert.Null(update1.Previous);
    Assert.Equal(2, update2.Sequence);
    Assert.Equal(first, update2.Previous);
    Assert.Equal(second, store.Current);
}
```

Add a test fixture helper that always builds UTC timestamps and validates remaining/used values sum to 100 within `0.0001`.

- [ ] **Step 2: Run the test and verify the scaffold is not implemented yet**

Run:

```powershell
 dotnet test tests/windows/CodexQuota.Core.Tests/CodexQuota.Core.Tests.csproj
```

Expected: FAIL because the domain/store types do not exist.

- [ ] **Step 3: Create the solution/projects and minimal thread-safe store**

Use:

```powershell
 dotnet new sln -n CodexQuota -o src/windows
 dotnet new classlib -n CodexQuota.Core -o src/windows/CodexQuota.Core -f net8.0
 dotnet new xunit -n CodexQuota.Core.Tests -o tests/windows/CodexQuota.Core.Tests -f net8.0
 dotnet sln src/windows/CodexQuota.sln add src/windows/CodexQuota.Core/CodexQuota.Core.csproj
 dotnet sln src/windows/CodexQuota.sln add tests/windows/CodexQuota.Core.Tests/CodexQuota.Core.Tests.csproj
 dotnet add tests/windows/CodexQuota.Core.Tests/CodexQuota.Core.Tests.csproj reference src/windows/CodexQuota.Core/CodexQuota.Core.csproj
```

Implement `InMemoryQuotaStateStore.Replace` under a single `lock`, incrementing a private `long _sequence` and atomically replacing `_current`.

- [ ] **Step 4: Run the core tests**

```powershell
 dotnet test tests/windows/CodexQuota.Core.Tests/CodexQuota.Core.Tests.csproj
```

Expected: PASS.

- [ ] **Step 5: Commit the domain scaffold**

```powershell
 git add src/windows tests/windows
 git commit -m "feat(windows): add quota domain model and state store"
```

---

### Task 2: Implement Codex rate-limit DTOs and safe window mapping

**Files:**
- Create: `src/windows/CodexQuota.Codex/CodexQuota.Codex.csproj`
- Create: `src/windows/CodexQuota.Codex/Protocol/RateLimitDtos.cs`
- Create: `src/windows/CodexQuota.Codex/Quota/RateLimitAdapter.cs`
- Create: `src/windows/CodexQuota.Codex/Quota/RateLimitAdaptResult.cs`
- Create: `tests/windows/CodexQuota.Codex.Tests/CodexQuota.Codex.Tests.csproj`
- Create: `tests/windows/CodexQuota.Codex.Tests/RateLimitAdapterTests.cs`
- Create: `tests/windows/CodexQuota.Codex.Tests/RateLimitTestData.cs`

**Interfaces:**
- Consumes: `QuotaSnapshot`, `QuotaWindow` from Task 1.
- Produces: `RateLimitAdaptResult RateLimitAdapter.TryAdapt(RateLimitsReadResult source, DateTimeOffset receivedAt)`.
- Produces: `RateLimitAdaptResult.Success(QuotaSnapshot snapshot)` or `.Unsupported(string reason)`.

- [ ] **Step 1: Write mapping and rejection tests before implementation**

Include at least these exact cases:

```csharp
[Theory]
[InlineData(25, 75)]
[InlineData(0, 100)]
[InlineData(100, 0)]
public void MapsShortWindowFromDuration(double used, double expectedRemaining)
{
    var source = RateLimitTestData.ReadResult(
        RateLimitTestData.Bucket("short", used, 300, 1_800_000_000),
        RateLimitTestData.Bucket("weekly", 40, 10080, 1_800_100_000));

    var result = new RateLimitAdapter().TryAdapt(source, DateTimeOffset.UnixEpoch);

    var success = Assert.IsType<RateLimitAdaptResult.Success>(result);
    Assert.Equal(expectedRemaining, success.Snapshot.ShortWindow.RemainingPercent);
}

[Fact]
public void RejectsDuplicateShortWindowBucketsAsAmbiguous()
{
    var source = RateLimitTestData.ReadResult(
        RateLimitTestData.Bucket("short-a", 10, 300, 1_800_000_000),
        RateLimitTestData.Bucket("short-b", 20, 300, 1_800_000_001),
        RateLimitTestData.Bucket("weekly", 40, 10080, 1_800_100_000));

    var result = new RateLimitAdapter().TryAdapt(source, DateTimeOffset.UnixEpoch);

    Assert.IsType<RateLimitAdaptResult.Unsupported>(result);
}

[Fact]
public void RejectsMissingWeeklyWindow()
{
    var source = RateLimitTestData.ReadResult(
        RateLimitTestData.Bucket("short", 10, 300, 1_800_000_000));

    Assert.IsType<RateLimitAdaptResult.Unsupported>(
        new RateLimitAdapter().TryAdapt(source, DateTimeOffset.UnixEpoch));
}

[Fact]
public void RejectsPercentOutsideZeroToHundred()
{
    var source = RateLimitTestData.ReadResult(
        RateLimitTestData.Bucket("short", 101, 300, 1_800_000_000),
        RateLimitTestData.Bucket("weekly", 40, 10080, 1_800_100_000));

    Assert.IsType<RateLimitAdaptResult.Unsupported>(
        new RateLimitAdapter().TryAdapt(source, DateTimeOffset.UnixEpoch));
}
```

`RateLimitTestData` is a test-only builder defined in this task; its `ReadResult(params SourceRateLimitBucket[] buckets)` distributes candidates across `primary`, `secondary`, and `rateLimitsByLimitId` in separate tests so mapping cannot depend on field position.

- [ ] **Step 2: Run the adapter tests and verify failure**

```powershell
 dotnet test tests/windows/CodexQuota.Codex.Tests/CodexQuota.Codex.Tests.csproj --filter RateLimitAdapterTests
```

Expected: FAIL because adapter/DTO types are missing.

- [ ] **Step 3: Implement parsing and mapping rules**

Create source DTOs with nullable fields matching App Server. Flatten every non-null source bucket into candidates, deduplicate the same logical object, then require exactly one `windowDurationMins == 300` and exactly one `windowDurationMins == 10080` candidate. Convert Unix `resetsAt` seconds with `DateTimeOffset.FromUnixTimeSeconds` and calculate `remaining = 100d - used` only after validating `0 <= used <= 100`.

Do not clamp invalid values. Return `Unsupported(reason)`.

- [ ] **Step 4: Run all Codex adapter tests**

```powershell
 dotnet test tests/windows/CodexQuota.Codex.Tests/CodexQuota.Codex.Tests.csproj
```

Expected: PASS, including ambiguity and invalid-data cases.

- [ ] **Step 5: Commit the adapter**

```powershell
 git add src/windows/CodexQuota.Codex tests/windows/CodexQuota.Codex.Tests
 git commit -m "feat(windows): normalize Codex rate-limit windows safely"
```

---

### Task 3: Build the JSONL JSON-RPC transport and initialization handshake

**Files:**
- Create: `src/windows/CodexQuota.Codex/Protocol/IJsonRpcTransport.cs`
- Create: `src/windows/CodexQuota.Codex/Protocol/JsonlProcessTransport.cs`
- Create: `src/windows/CodexQuota.Codex/Protocol/CodexRpcClient.cs`
- Create: `src/windows/CodexQuota.Codex/Protocol/CodexNotification.cs`
- Create: `tests/windows/CodexQuota.Codex.Tests/Fakes/FakeJsonRpcTransport.cs`
- Create: `tests/windows/CodexQuota.Codex.Tests/CodexRpcClientTests.cs`

**Interfaces:**
- Produces: `Task InitializeAsync(CancellationToken cancellationToken)`.
- Produces: `Task<T> CallAsync<T>(string method, object? @params, CancellationToken cancellationToken)`.
- Produces: `IAsyncEnumerable<CodexNotification> Notifications(CancellationToken cancellationToken)`.
- JSON transport sends one object per line and correlates numeric request IDs.

- [ ] **Step 1: Write handshake/correlation/malformed-line tests**

Test the exact ordering:

```text
initialize request
initialize response
initialized notification
account/read request
```

Also test two concurrent requests whose responses arrive in reverse order, and a malformed JSON line that faults the transport without delivering a fake notification.

- [ ] **Step 2: Run the JSON-RPC tests and verify failure**

```powershell
 dotnet test tests/windows/CodexQuota.Codex.Tests/CodexQuota.Codex.Tests.csproj --filter CodexRpcClientTests
```

Expected: FAIL.

- [ ] **Step 3: Implement minimal JSONL RPC behavior**

`JsonlProcessTransport` owns a writer lock for stdin and one background stdout reader. `CodexRpcClient` maintains `ConcurrentDictionary<long, TaskCompletionSource<JsonElement>>`, assigns IDs with `Interlocked.Increment`, dispatches response objects with `id`, and emits notification objects with `method` but no `id` through a `Channel<CodexNotification>`.

Initialization payload:

```json
{
  "method": "initialize",
  "id": 1,
  "params": {
    "clientInfo": {
      "name": "codex_quota_bridge",
      "title": "Codex Quota Bridge",
      "version": "1.0.0"
    }
  }
}
```

After success send `{ "method": "initialized" }` with no `id`.

- [ ] **Step 4: Run the transport tests**

```powershell
 dotnet test tests/windows/CodexQuota.Codex.Tests/CodexQuota.Codex.Tests.csproj --filter "CodexRpcClientTests|Jsonl"
```

Expected: PASS.

- [ ] **Step 5: Commit the transport**

```powershell
 git add src/windows/CodexQuota.Codex tests/windows/CodexQuota.Codex.Tests
 git commit -m "feat(windows): add Codex app-server JSONL RPC client"
```

---

### Task 4: Own and recover the Codex App Server child process

**Files:**
- Create: `src/windows/CodexQuota.Codex/Process/ICodexProcess.cs`
- Create: `src/windows/CodexQuota.Codex/Process/CodexProcessManager.cs`
- Create: `src/windows/CodexQuota.Codex/Process/CodexRuntimeLocator.cs`
- Create: `src/windows/CodexQuota.Codex/Process/RestartBackoff.cs`
- Create: `tests/windows/CodexQuota.Codex.Tests/CodexProcessManagerTests.cs`
- Create: `tests/windows/CodexQuota.Codex.Tests/RestartBackoffTests.cs`

**Interfaces:**
- Produces: `Task<CodexManagedSession> StartAsync(CancellationToken cancellationToken)`.
- Produces: `Task StopAsync(CancellationToken cancellationToken)`.
- Produces status event enum: `Starting`, `Running`, `Restarting`, `Faulted`, `Stopped`.
- Runtime path is resolved from `runtime/codex-app-server.exe` beside the desktop executable; Stage packaging copies the explicitly supported binary there.

- [ ] **Step 1: Write bounded-restart tests**

Pin delays to `1s, 2s, 5s, 10s, 30s` and assert a sixth crash within a rolling five-minute test clock leaves status `Faulted` with no next automatic launch. Use a fake clock/delay abstraction so tests do not sleep.

- [ ] **Step 2: Run the process tests and verify failure**

```powershell
 dotnet test tests/windows/CodexQuota.Codex.Tests/CodexQuota.Codex.Tests.csproj --filter "CodexProcessManagerTests|RestartBackoffTests"
```

Expected: FAIL.

- [ ] **Step 3: Implement process ownership**

Use `ProcessStartInfo` with `UseShellExecute=false`, redirected stdin/stdout/stderr, `CreateNoWindow=true`, and argument `--listen stdio://` when launching `codex-app-server.exe`. Set child environment `CODEX_HOME` to `%LOCALAPPDATA%\CodexQuotaBridge\codex-home` so Bridge authentication/configuration is isolated from Codex Desktop state. Tie the transport lifetime to the process lifetime. Unexpected exit reports failure to manager; explicit `StopAsync` suppresses restart.

- [ ] **Step 4: Run process tests**

```powershell
 dotnet test tests/windows/CodexQuota.Codex.Tests/CodexQuota.Codex.Tests.csproj --filter "CodexProcessManagerTests|RestartBackoffTests"
```

Expected: PASS without real time delays.

- [ ] **Step 5: Commit process lifecycle**

```powershell
 git add src/windows/CodexQuota.Codex tests/windows/CodexQuota.Codex.Tests
 git commit -m "feat(windows): manage Codex app-server lifecycle"
```

---

### Task 5: Implement account login and quota synchronization service

**Files:**
- Create: `src/windows/CodexQuota.Codex/Account/CodexAccountService.cs`
- Create: `src/windows/CodexQuota.Codex/Account/AccountState.cs`
- Create: `src/windows/CodexQuota.Codex/Quota/CodexQuotaSyncService.cs`
- Create: `src/windows/CodexQuota.Core/Runtime/BridgeRuntimeState.cs`
- Create: `tests/windows/CodexQuota.Codex.Tests/CodexAccountServiceTests.cs`
- Create: `tests/windows/CodexQuota.Codex.Tests/CodexQuotaSyncServiceTests.cs`

**Interfaces:**
- Produces: `Task<AccountState> ReadAccountAsync(CancellationToken cancellationToken)`.
- Produces: `Task<Uri> StartChatGptLoginAsync(CancellationToken cancellationToken)`.
- Produces: `Task LogoutAsync(CancellationToken cancellationToken)`.
- Produces: `Task RefreshNowAsync(CancellationToken cancellationToken)`.
- `CodexQuotaSyncService` consumes notifications and writes successful adapted snapshots to `IQuotaStateStore`.

- [ ] **Step 1: Write auth and quota-service tests**

Test that login sends exactly:

```json
{
  "type": "chatgpt",
  "useHostedLoginSuccessPage": true,
  "appBrand": "chatgpt"
}
```

Test that `account/rateLimits/updated` with a valid payload replaces state immediately; an unsupported payload leaves the previous trusted snapshot untouched and sets runtime source status to `SourceSchemaUnsupported`; watchdog `RefreshNowAsync` performs exactly one `account/rateLimits/read` call. Add login-completion tests for `success=false` and `account/updated` with `authMode=null`; both must settle at `AuthRequired` without scheduling a busy retry loop.

- [ ] **Step 2: Run tests and verify failure**

```powershell
 dotnet test tests/windows/CodexQuota.Codex.Tests/CodexQuota.Codex.Tests.csproj --filter "CodexAccountServiceTests|CodexQuotaSyncServiceTests"
```

Expected: FAIL.

- [ ] **Step 3: Implement account and sync services**

On startup: initialize → `account/read` → if authenticated call `account/rateLimits/read`; otherwise expose `AuthRequired`. Start one five-minute `PeriodicTimer` only while authenticated/running. Notification handler reacts to `account/rateLimits/updated`, `account/updated`, and `account/login/completed`; it never blocks the stdout reader—queue work onto an internal `Channel`.

Open the returned `authUrl` with `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })` in the desktop layer, not in the RPC service.

- [ ] **Step 4: Run account/sync tests**

```powershell
 dotnet test tests/windows/CodexQuota.Codex.Tests/CodexQuota.Codex.Tests.csproj --filter "CodexAccountServiceTests|CodexQuotaSyncServiceTests"
```

Expected: PASS.

- [ ] **Step 5: Commit account/quota sync**

```powershell
 git add src/windows/CodexQuota.Core src/windows/CodexQuota.Codex tests/windows/CodexQuota.Codex.Tests
 git commit -m "feat(windows): add Codex login and quota synchronization"
```

---

### Task 6: Add SQLite history/event persistence with failure isolation

**Files:**
- Create: `src/windows/CodexQuota.Storage/CodexQuota.Storage.csproj`
- Create: `src/windows/CodexQuota.Storage/Database/BridgeDatabase.cs`
- Create: `src/windows/CodexQuota.Storage/History/IHistoryRepository.cs`
- Create: `src/windows/CodexQuota.Storage/History/SqliteHistoryRepository.cs`
- Create: `src/windows/CodexQuota.Storage/History/HistoryWritePolicy.cs`
- Create: `src/windows/CodexQuota.Storage/History/HistoryPersistenceWorker.cs`
- Create: `tests/windows/CodexQuota.Storage.Tests/CodexQuota.Storage.Tests.csproj`
- Create: `tests/windows/CodexQuota.Storage.Tests/HistoryWritePolicyTests.cs`
- Create: `tests/windows/CodexQuota.Storage.Tests/SqliteHistoryRepositoryTests.cs`
- Create: `tests/windows/CodexQuota.Storage.Tests/HistoryFailureIsolationTests.cs`

**Interfaces:**
- Produces: `Task AppendSnapshotAsync(QuotaStateUpdate update, CancellationToken ct)`.
- Produces: `Task<IReadOnlyList<HistoryPoint>> ReadHistoryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)`.
- Produces: `Task<IReadOnlyList<QuotaEvent>> ReadEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)`.

- [ ] **Step 1: Write persistence policy and failure-isolation tests**

Test writes on first sample, changed quota, reset, or elapsed ≥5 minutes; identical samples within 5 minutes are skipped. Use an in-memory SQLite connection for repository tests. Inject a repository that throws on write and assert `IQuotaStateStore.Current` remains the new valid snapshot while a persistence-health flag becomes unavailable.

- [ ] **Step 2: Run storage tests and verify failure**

```powershell
 dotnet test tests/windows/CodexQuota.Storage.Tests/CodexQuota.Storage.Tests.csproj
```

Expected: FAIL.

- [ ] **Step 3: Implement schema and asynchronous persistence worker**

First pin the .NET 8 line of SQLite used by this .NET 8 project:

```powershell
 dotnet add src/windows/CodexQuota.Storage/CodexQuota.Storage.csproj package Microsoft.Data.Sqlite --version 8.0.31
```

Create tables `quota_samples`, `quota_events`, `paired_devices`, `bridge_metadata`. `HistoryPersistenceWorker` subscribes to state updates through a bounded channel and catches repository exceptions around persistence only. Cleanup executes `DELETE FROM quota_samples WHERE recorded_at_utc < $cutoff; DELETE FROM quota_events WHERE occurred_at_utc < $cutoff;` with cutoff `UtcNow - 25h`.

- [ ] **Step 4: Run storage tests**

```powershell
 dotnet test tests/windows/CodexQuota.Storage.Tests/CodexQuota.Storage.Tests.csproj
```

Expected: PASS.

- [ ] **Step 5: Commit persistence**

```powershell
 git add src/windows/CodexQuota.Storage tests/windows/CodexQuota.Storage.Tests src/windows/CodexQuota.sln
 git commit -m "feat(windows): persist 24-hour quota history safely"
```

---

### Task 7: Add mandatory secret redaction and structured file logging

**Files:**
- Create: `src/windows/CodexQuota.Core/Logging/SensitiveDataRedactor.cs`
- Create: `src/windows/CodexQuota.Desktop/Logging/RedactingFileLoggerProvider.cs`
- Create: `tests/windows/CodexQuota.Core.Tests/SensitiveDataRedactorTests.cs`

**Interfaces:**
- Produces: `string SensitiveDataRedactor.Redact(string value)`.
- Redacts Bearer authorization values, OAuth/access/refresh-token property names, cookies, PEM private keys, and pairing/device secrets before disk output.

- [ ] **Step 1: Write secret-leak tests**

Use test inputs containing:

```text
Authorization: Bearer abcdef123
"accessToken":"secret"
"refresh_token":"secret2"
Cookie: session=secret3
-----BEGIN PRIVATE KEY-----\nsecret\n-----END PRIVATE KEY-----
pairingSecret=secret4
```

Assert none of `abcdef123`, `secret`, `secret2`, `secret3`, `secret4` appears in output and safe fields such as `component=Codex` remain.

- [ ] **Step 2: Run redaction tests and verify failure**

```powershell
 dotnet test tests/windows/CodexQuota.Core.Tests/CodexQuota.Core.Tests.csproj --filter SensitiveDataRedactorTests
```

Expected: FAIL.

- [ ] **Step 3: Implement redaction and rotating logger**

Use compiled regular expressions plus JSON-key redaction for known secret keys. `RedactingFileLoggerProvider` must invoke the redactor on the final formatted message and exception text before writing. Configure 5 MB max file size and keep at most 5 files.

- [ ] **Step 4: Run core tests and a leak scan**

```powershell
 dotnet test tests/windows/CodexQuota.Core.Tests/CodexQuota.Core.Tests.csproj
 Select-String -Path .\artifacts\test-logs\* -Pattern 'Bearer abcdef123|secret2|BEGIN PRIVATE KEY' -Quiet
```

Expected: tests PASS; `Select-String` returns `False` when test logs exist.

- [ ] **Step 5: Commit logging**

```powershell
 git add src/windows/CodexQuota.Core src/windows/CodexQuota.Desktop tests/windows/CodexQuota.Core.Tests
 git commit -m "feat(windows): redact secrets from persistent logs"
```

---

### Task 8: Compose the WPF tray host and Stage A integration gate

**Files:**
- Create: `src/windows/CodexQuota.Desktop/CodexQuota.Desktop.csproj`
- Create: `src/windows/CodexQuota.Desktop/App.xaml`
- Create: `src/windows/CodexQuota.Desktop/App.xaml.cs`
- Create: `src/windows/CodexQuota.Desktop/Tray/TrayController.cs`
- Create: `src/windows/CodexQuota.Desktop/Views/StatusWindow.xaml`
- Create: `src/windows/CodexQuota.Desktop/Views/StatusWindow.xaml.cs`
- Create: `src/windows/CodexQuota.Desktop/ViewModels/StatusViewModel.cs`
- Create: `src/windows/CodexQuota.Desktop/Runtime/BridgeHostedService.cs`
- Create: `tests/windows/CodexQuota.IntegrationTests/CodexQuota.IntegrationTests.csproj`
- Create: `tests/windows/CodexQuota.IntegrationTests/FakeCodexAppServer.cs`
- Create: `tests/windows/CodexQuota.IntegrationTests/StageAIntegrationTests.cs`

**Interfaces:**
- Desktop host composes process manager, account service, quota sync, state store, history worker, logging.
- Tray commands: `Open Status`, `Refresh Now`, `Login`, `Logout`, `Open Logs`, `Exit`.
- Window close hides the window; only tray `Exit` stops the process.

- [ ] **Step 1: Write an end-to-end fake-App-Server integration test**

The fake child must accept `initialize`, reply, observe `initialized`, return authenticated account state, return 300/10080-minute quota, then emit one `account/rateLimits/updated`. Assert the Bridge reaches `Ready`, current state changes, and the history repository receives the update. Add a crash-loop variant that reaches `CodexFaulted` after the configured threshold.

- [ ] **Step 2: Run the integration test and verify failure**

```powershell
 dotnet test tests/windows/CodexQuota.IntegrationTests/CodexQuota.IntegrationTests.csproj --filter StageAIntegrationTests
```

Expected: FAIL before host composition exists.

- [ ] **Step 3: Implement host composition and tray shell**

Use `Host.CreateApplicationBuilder()`. Register singleton state, process manager, account/sync services, persistence worker, and ViewModel. Use `System.Windows.Forms.NotifyIcon` from the WPF host (`UseWindowsForms=true`) to avoid adding a tray framework dependency. `Refresh Now` calls only the quota refresh method. `Exit` performs cancellation and awaits hosted-service shutdown before closing the WPF dispatcher.

- [ ] **Step 4: Run the full Stage A suite and perform the real Codex smoke test**

Automated:

```powershell
 dotnet test src/windows/CodexQuota.sln -c Release
```

Expected: PASS.

Manual on Win11 with the supported `runtime/codex-app-server.exe` present:

```text
1. Launch CodexQuota.Desktop.exe.
2. Complete ChatGPT browser login if asked.
3. Confirm Status shows both quota windows and reset times.
4. Use Refresh Now and confirm no second child process is spawned.
5. Exit from tray and confirm codex-app-server.exe also exits.
```

- [ ] **Step 5: Commit Stage A and tag the gate**

```powershell
 git add src/windows tests/windows
 git commit -m "feat(windows): complete local Codex quota bridge core"
 git tag stage-a-windows-core
```
