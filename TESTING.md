# Claude Manager — Testing Plan

## Test project structure

```
claude_manager/
└── tests/
    ├── ClaudeManager.Hub.Tests/          # Unit tests for Hub components
    ├── ClaudeManager.Agent.Tests/        # Unit tests for Agent components
    └── ClaudeManager.Integration.Tests/  # Integration tests (real SQLite, real SignalR)
```

### Project references

| Test project | References |
|---|---|
| `Hub.Tests` | `ClaudeManager.Hub`, `ClaudeManager.Shared` |
| `Agent.Tests` | `ClaudeManager.Agent`, `ClaudeManager.Shared` |
| `Integration.Tests` | `ClaudeManager.Hub`, `ClaudeManager.Agent`, `ClaudeManager.Shared` |

### NuGet packages (all three projects)

```xml
<PackageReference Include="xunit" Version="2.9.*" />
<PackageReference Include="xunit.runner.visualstudio" Version="2.8.*" />
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.*" />
<PackageReference Include="Moq" Version="4.20.*" />
<PackageReference Include="FluentAssertions" Version="7.*" />
<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.*" />
```

Additional for `Integration.Tests` only:

```xml
<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.*" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.*" />
<PackageReference Include="Microsoft.AspNetCore.SignalR.Client" Version="10.0.*" />
```

---

## Production changes already applied for testability

The following refactors have already been made to the production code. No `[InternalsVisibleTo]` attributes are needed.

**Agent project:**
- `ClaudeArgumentBuilder` (public static class) — extracted from `ClaudeProcess`. Tests call `Build(prompt, resumeId)` directly.
- `ClaudeStreamJsonParser` (public static class) — extracted from `ClaudeProcess`. Tests call `ExtractSessionId` and `IsResumeError` directly.
- `IProcessRunner` / `ProcessRunner` — injected into `ClaudeValidator`. Tests mock `IProcessRunner`.
- `MachineIdProvider.GetOrCreate(string? storagePath)` — optional parameter lets tests pass a temp path.
- `IClaudeProcess` / `IClaudeProcessFactory` — `ClaudeProcess` implements `IClaudeProcess`; tests inject a fake factory returning fake processes.
- `SessionProcessManager` — extracted from `AgentService`. Contains all queuing and lifecycle logic; fully testable without SignalR.

**Hub project:**
- `WebApplicationFactory` requires the Hub's entry point assembly to be accessible. Add to `ClaudeManager.Hub.csproj`:
  ```xml
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <_Parameter1>ClaudeManager.Integration.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
  ```
  This is the only `[InternalsVisibleTo]` still needed, and only for the integration test project.

---

## Unit tests

### Priority 1 — Core state (write first)

#### `ClaudeSession` — line buffer invariants
**`tests/ClaudeManager.Hub.Tests/Models/ClaudeSessionTests.cs`**

```
AppendLine_WhenUnder2000Lines_AddsLine
AppendLine_WhenAt2000Lines_DropsOldestAndAddsNew
AppendLine_ConcurrentWriters_NeverExceedsCap        // 10 tasks × 300 appends each
AppendLine_ConcurrentWriters_NeverThrows
OutputLines_ReturnsSnapshotNotLiveList
SetOutputLines_ReplacesExistingBuffer
SetOutputLines_WithMoreThan2000_StoresAll            // SetOutputLines has no cap; only AppendLine does
```

#### `SessionStore` — in-memory state mutations and event firing
**`tests/ClaudeManager.Hub.Tests/Services/SessionStoreTests.cs`**

Use a real `DashboardNotifier` and a Moq `IPersistenceQueue`.

```
// RegisterAgent
RegisterAgent_NewMachine_ReturnsMachineAgent
RegisterAgent_NewMachine_SetsIsOnlineTrue
RegisterAgent_NewMachine_FiresAgentConnectedEvent
RegisterAgent_NewMachine_EnqueuesUpsertAgent
RegisterAgent_ExistingMachine_UpdatesConnectionIdAndTimestamps
RegisterAgent_ExistingMachine_KeepsExistingSessions
RegisterAgent_ExistingMachine_FiresAgentConnectedEvent

// MarkAgentDisconnected
MarkAgentDisconnected_SetsIsOnlineFalse
MarkAgentDisconnected_MarksActiveSessionsAsDisconnected
MarkAgentDisconnected_DoesNotTouchEndedOrDisconnectedSessions
MarkAgentDisconnected_EnqueuesStatusChangeForEachActiveSession
MarkAgentDisconnected_SetsIsProcessRunningFalseOnActiveSessions
MarkAgentDisconnected_FiresAgentDisconnectedEvent
MarkAgentDisconnected_UnknownMachineId_DoesNotThrow

// StartSession
StartSession_KnownMachine_ReturnsSession
StartSession_KnownMachine_SetsStatusActive
StartSession_KnownMachine_SetsIsProcessRunningTrue
StartSession_KnownMachine_EnqueuesUpsertSession
StartSession_KnownMachine_FiresSessionStartedEvent
StartSession_KnownMachine_TruncatesInitialPromptAt4000Chars
StartSession_InitialPromptExactly4000Chars_NotTruncated
StartSession_InitialPromptNull_EnqueuesNullInitialPrompt
StartSession_UnknownMachine_ThrowsInvalidOperationException

// AppendLine
AppendLine_KnownSession_DelegatesToClaudeSessionAppendLine
AppendLine_KnownSession_EnqueuesLine
AppendLine_KnownSession_FiresLineStreamedEvent
AppendLine_UnknownMachine_SilentlyIgnores
AppendLine_UnknownSession_SilentlyIgnores

// EndSession
EndSession_KnownSession_SetsStatusEnded
EndSession_KnownSession_SetsIsProcessRunningFalse
EndSession_KnownSession_SetsEndedAt
EndSession_KnownSession_EnqueuesSessionStatusChange
EndSession_KnownSession_FiresSessionEndedEvent
EndSession_UnknownSession_SilentlyIgnores

// SetProcessRunning
SetProcessRunning_True_UpdatesFlag
SetProcessRunning_False_UpdatesFlag
SetProcessRunning_FiresSessionStatusChangedEvent
SetProcessRunning_UnknownSession_SilentlyIgnores

// GetConnectionId
GetConnectionId_OnlineMachine_ReturnsConnectionId
GetConnectionId_OfflineMachine_ReturnsNull
GetConnectionId_UnknownMachine_ReturnsNull

// DB recovery helpers
EnsureAgentFromDb_NewMachine_AddsOfflineAgent
EnsureAgentFromDb_ExistingMachine_DoesNotOverwrite   // TryAdd semantics
RestoreSessionFromDb_KnownMachine_AddsSession
RestoreSessionFromDb_KnownMachine_SetsOutputLines
RestoreSessionFromDb_KnownMachine_SetsIsProcessRunningFalse
RestoreSessionFromDb_UnknownMachine_SilentlyIgnores
```

#### `AgentHub.StreamLine` — kind detection and truncation
**`tests/ClaudeManager.Hub.Tests/Hubs/AgentHubStreamLineTests.cs`**

Test through the public `StreamLine` method with a pre-registered agent. Requires a stub `HubCallerContext` (Moq).

```
StreamLine_ResultTypeJson_KindIsResultSummary
StreamLine_AssistantTypeJson_KindIsAssistantToken
StreamLine_ToolUseTypeJson_KindIsToolUse
StreamLine_ToolResultTypeJson_KindIsToolResult
StreamLine_StderrFieldJson_KindIsError
StreamLine_UnrecognizedJson_DefaultsToAssistantToken
StreamLine_ToolUseTypeJson_ExtractsToolName
StreamLine_ToolUseTypeJsonMissingNameField_ToolNameIsNull
StreamLine_ContentExactly8000Chars_NotTruncated
StreamLine_ContentExceeds8000Chars_TruncatesToFirst8000
StreamLine_ContentExceeds8000Chars_SetsIsTruncatedTrue
StreamLine_ContentUnder8000Chars_IsTruncatedFalse
```

**Known edge:** `DetectKind` checks `"type":"result"` before `"type":"tool_use"`. A malformed JSON blob containing both resolves to `ResultSummary`. This is documented, not a bug.

### Priority 2 — Command routing and security

#### `AgentCommandService` — offline machine handling
**`tests/ClaudeManager.Hub.Tests/Services/AgentCommandServiceTests.cs`**

Mock `IHubContext<AgentHub>`, use real `SessionStore`.

```
StartSessionAsync_OnlineMachine_SendsStartSessionToClient
StartSessionAsync_OnlineMachine_ReturnsTrue
StartSessionAsync_OfflineMachine_ReturnsFalse
StartSessionAsync_OfflineMachine_DoesNotCallSendAsync
StartSessionAsync_UnknownMachine_ReturnsFalse

SendPromptAsync_OnlineMachine_SendsSendPromptToClient
SendPromptAsync_OnlineMachine_ReturnsTrue
SendPromptAsync_OfflineMachine_ReturnsFalse

KillSessionAsync_OnlineMachine_SendsKillSessionToClient
KillSessionAsync_OnlineMachine_ReturnsTrue
KillSessionAsync_OfflineMachine_ReturnsFalse
```

#### `AgentSecretMiddleware` — security boundary
**`tests/ClaudeManager.Hub.Tests/Hubs/AgentSecretMiddlewareTests.cs`**

Use `DefaultHttpContext` directly — no web server needed.

```
InvokeAsync_AgentHubPath_CorrectHeader_CallsNext
InvokeAsync_AgentHubPath_CorrectHeader_Returns200
InvokeAsync_AgentHubPath_MissingHeader_Returns401
InvokeAsync_AgentHubPath_WrongHeader_Returns401
InvokeAsync_AgentHubPath_CorrectQueryParam_CallsNext
InvokeAsync_AgentHubPath_MissingHeaderAndQuery_Returns401
InvokeAsync_NonAgentHubPath_NoHeader_CallsNext
InvokeAsync_NonAgentHubPath_NoHeader_DoesNotReturn401
```

### Priority 3 — Agent-side logic

#### `ClaudeArgumentBuilder` — argument building
**`tests/ClaudeManager.Agent.Tests/ClaudeArgumentBuilderTests.cs`**

Public static class — no setup needed.

```
Build_NoResumeId_ReturnsExpectedFlags
Build_WithResumeId_IncludesResumeFlag
Build_PromptWithDoubleQuotes_EscapesQuotes
Build_PromptWithBackslashes_EscapesBackslashes
Build_PromptWithBothEscapableChars_EscapesBoth
```

#### `ClaudeStreamJsonParser` — JSON field extraction
**`tests/ClaudeManager.Agent.Tests/ClaudeStreamJsonParserTests.cs`**

Public static class — no setup needed.

```
ExtractSessionId_ValidInitLine_ReturnsSessionId
ExtractSessionId_NoSessionIdMarker_ReturnsNull
ExtractSessionId_SessionIdAtEndOfJson_ReturnsValue
ExtractSessionId_EmptyClosingQuote_ReturnsNull

IsResumeError_ErrorResultJson_ReturnsTrue
IsResumeError_ResultTypeButNotErrorSubtype_ReturnsFalse
IsResumeError_NoResultType_ReturnsFalse
IsResumeError_EmptyString_ReturnsFalse
```

#### `SessionProcessManager` — queuing and lifecycle
**`tests/ClaudeManager.Agent.Tests/SessionProcessManagerTests.cs`**

Inject a `FakeClaudeProcessFactory` that returns `FakeClaudeProcess` instances (no real OS processes). The fake exposes methods to simulate output and exit.

```
StartSessionAsync_CreatesProcessAndStartsIt
StartSessionAsync_StoresWorkingDirectoryForLaterUse

SendPromptAsync_ProcessNotRunning_SpawnsNewProcess
SendPromptAsync_ProcessAlreadyRunning_QueuesPrompt
SendPromptAsync_ProcessAlreadyRunning_DoesNotSpawnNewProcess

KillSessionAsync_RunningProcess_KillsAndRemoves
KillSessionAsync_UnknownSession_DoesNotThrow

OnProcessExited_PendingQueueHasItem_DequeuesAndSpawnsNext
OnProcessExited_PendingQueueEmpty_DoesNotSpawnNext
OnProcessExited_PendingIdMatchesRealSessionId_DrainsByRealId
OnProcessExited_PendingIdMatchesPendingSessionId_DrainsByPendingId

OnOutputLine_ForwardedToCallback
OnStderrLine_ForwardedToCallback
OnSessionEnded_ForwardedToCallback
OnSessionEnded_UsesRealSessionIdNotPendingId

KillAllAsync_KillsAllActiveProcesses
```

#### `ClaudeValidator.ResolveBinary` — PATH resolution
**`tests/ClaudeManager.Agent.Tests/ClaudeValidatorTests.cs`**

`ResolveBinary` is already `public static`.

```
ResolveBinary_ConfiguredPathExists_ReturnsConfiguredPath
ResolveBinary_ConfiguredPathDoesNotExist_SearchesPath
ResolveBinary_NullConfigured_SearchesPath
ResolveBinary_EmptyStringConfigured_SearchesPath
ResolveBinary_NotOnPath_ReturnsNull
```

For PATH search tests, temporarily set `Environment.SetEnvironmentVariable("PATH", ...)` to a temp directory containing a fake `claude` / `claude.exe` file, and restore after each test.

#### `MachineIdProvider` — stable GUID persistence
**`tests/ClaudeManager.Agent.Tests/MachineIdProviderTests.cs`**

These tests touch the real filesystem. Use `[Collection("MachineIdTests")]` to prevent parallel execution, and `IDisposable` to back up and restore any pre-existing `machine_id` file.

```
GetOrCreate_NoExistingFile_WritesNewGuidToFile
GetOrCreate_NoExistingFile_ReturnsValidGuid
GetOrCreate_ExistingValidGuidFile_ReturnsSameGuid
GetOrCreate_ExistingValidGuidFile_DoesNotWriteNewValue
GetOrCreate_ExistingFileWithInvalidContent_GeneratesNewGuid
GetOrCreate_ExistingFileWithInvalidContent_OverwritesFile
```

**Known gap:** The write-failure fallback (returns `MachineName`) is not testable without extracting file I/O behind an interface. Documented as a known untested path.

#### `PersistenceQueue` — channel overflow warning
**`tests/ClaudeManager.Hub.Tests/Persistence/PersistenceQueueChannelTests.cs`**

Start the queue with a pre-cancelled token so the consumer never drains, fill 2001 items, verify the logger was called once.

```
EnqueueUpsertAgent_WithCapacityAvailable_DoesNotLogWarning
EnqueueAny_WhenChannelFull_LogsWarning
TryEnqueue_WhenChannelFull_DoesNotThrow
```

#### `InfiniteRetryPolicy`
**`tests/ClaudeManager.Agent.Tests/InfiniteRetryPolicyTests.cs`**

```
NextRetryDelay_FirstFiveRetries_MatchesInitialDelays
NextRetryDelay_RetryCountBeyondTable_Returns60Seconds
```

---

## Integration tests

### `PersistenceQueue` — real SQLite writes
**`tests/ClaudeManager.Integration.Tests/Persistence/PersistenceQueueIntegrationTests.cs`**

Use a shared `SqliteConnection("Data Source=:memory:")` kept open across `DbContext` instances (required for in-memory SQLite to retain data). Create the schema with `db.Database.EnsureCreatedAsync()`.

```
EnqueueUpsertAgent_NewAgent_RowAppearsInDb
EnqueueUpsertAgent_ExistingAgent_UpdatesDisplayNameAndTimestamps
EnqueueUpsertAgent_ExistingAgent_DoesNotDuplicateRow
EnqueueUpsertSession_NewSession_RowAppearsInDb
EnqueueUpsertSession_ExistingSession_UpdatesRow
EnqueueLine_LineAppearsInDb
EnqueueLine_BumpsSessionLastActivityAt
EnqueueSessionStatusChange_UpdatesStatus
EnqueueSessionStatusChange_WithEndedAt_SetsEndedAt
EnqueueSessionStatusChange_NullEndedAt_ClearsEndedAt
```

Pattern: start the queue's `ExecuteAsync`, enqueue items, `await Task.Delay(200)`, cancel, assert DB state.

### `StartupRecoveryService` — DB → memory restore
**`tests/ClaudeManager.Integration.Tests/Persistence/StartupRecoveryIntegrationTests.cs`**

Seed data directly via `DbContext.SaveChangesAsync` before calling `service.StartAsync(ct)`.

```
StartAsync_PatchesActiveSessionsToDisconnected
StartAsync_DoesNotPatchEndedOrDisconnectedSessions
StartAsync_LoadsSessionsWithin7DaysIntoStore
StartAsync_DoesNotLoadSessionsOlderThan7Days
StartAsync_LoadsLast2000LinesPerSession
StartAsync_SessionHasOver2000Lines_OnlyLast2000Loaded
StartAsync_EmptyDb_DoesNotThrow
```

### `AgentHub` — end-to-end with real SignalR
**`tests/ClaudeManager.Integration.Tests/Hubs/AgentHubIntegrationTests.cs`**

Use `WebApplicationFactory<Program>` with overridden config (in-memory SQLite, test secret). Build a `HubConnection` from the factory's server URL with `X-Agent-Secret: test-secret`.

```
RegisterAgent_ValidSecret_ConnectsSuccessfully
RegisterAgent_MissingSecret_Returns401
RegisterAgent_WrongSecret_Returns401
RegisterAgent_AgentAppearsInSessionStore

StreamLine_AfterRegister_AppendedToSession
StreamLine_ContentOver8000_StoredTruncated
StreamLine_ContentUnder8000_StoredUntouched

SessionStarted_AfterRegister_SessionAppearsInStore
SessionEnded_AfterSessionStarted_SessionStatusIsEnded
Heartbeat_AfterRegister_UpdatesLastHeartbeat

OnDisconnected_MarksAgentOffline
OnDisconnected_SetsActiveSessionsToDisconnected
```

### `AgentSecretMiddleware` — full pipeline
**`tests/ClaudeManager.Integration.Tests/Hubs/AgentSecretMiddlewareIntegrationTests.cs`**

```
AgentHubEndpoint_MissingSecret_Returns401
AgentHubEndpoint_WrongSecret_Returns401
AgentHubEndpoint_CorrectSecret_UpgradesToWebSocket
NonHubEndpoint_NoSecret_DoesNotReturn401
```

---

## What NOT to test

| Skipped | Reason |
|---|---|
| DTOs (`AgentRegistration`, `StreamLineDto`, etc.) | C# records with no behavior |
| `DashboardNotifier` | Eight lines of event wiring; implicitly covered by `SessionStore` tests |
| Blazor component rendering | Fragile, slow, not meaningful for a personal tool |
| `AgentService.GetPlatform()` | OS constant string; not meaningfully testable |
| `AgentService` (SignalR wiring) | Thin coordinator after extraction; covered by integration tests |
| `ClaudeValidator.ValidateAsync` with `ProcessRunner` | Integration path tested via mock; real process tested manually |
| `ClaudeManagerDbContext.OnModelCreating` | EF config; covered implicitly by integration tests |
| Entity classes | Data annotations only, no behavior |
| `MachineAgent` model | Property bag, no behavior |

---

## Test helpers and fixtures

### `SessionStoreBuilder`
**`tests/ClaudeManager.Hub.Tests/Helpers/SessionStoreBuilder.cs`**

```csharp
public class SessionStoreBuilder
{
    private readonly DashboardNotifier _notifier = new();
    private IPersistenceQueue _queue = Mock.Of<IPersistenceQueue>();

    public SessionStoreBuilder WithQueue(IPersistenceQueue q) { _queue = q; return this; }

    public (SessionStore store, DashboardNotifier notifier) Build()
    {
        var store = new SessionStore(_notifier);
        store.SetPersistenceQueue(_queue);
        return (store, _notifier);
    }
}
```

### `TestData` — shared constants and object factories
**`tests/ClaudeManager.Hub.Tests/Helpers/TestData.cs`**

```csharp
public static class TestData
{
    public const string MachineId    = "machine-001";
    public const string ConnectionId = "conn-abc123";
    public const string SessionId    = "sess-xyz789";

    public static MachineAgentEntity AgentEntity(string? machineId = null) => new() { ... };
    public static ClaudeSessionEntity SessionEntity(SessionStatus status = SessionStatus.Active) => new() { ... };
    public static StreamedLine Line(StreamLineKind kind = StreamLineKind.AssistantToken) => new() { ... };
    public static StreamLineDto LineDto(string? rawJson = null) => new(...);
}
```

### `SessionStoreExtensions` — pre-registered agent shorthand

```csharp
public static class SessionStoreExtensions
{
    public static MachineAgent RegisterTestAgent(this SessionStore store,
        string machineId = TestData.MachineId,
        string connectionId = TestData.ConnectionId) =>
        store.RegisterAgent(machineId, connectionId, "Test Machine", "win32");
}
```

### `HubContextFactory` — Moq IHubContext builder

```csharp
public static class HubContextFactory
{
    public static (Mock<IHubContext<AgentHub>> ctx, Mock<IClientProxy> proxy) CreateMock()
    {
        var proxy   = new Mock<IClientProxy>();
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Client(It.IsAny<string>())).Returns(proxy.Object);
        var ctx = new Mock<IHubContext<AgentHub>>();
        ctx.Setup(h => h.Clients).Returns(clients.Object);
        return (ctx, proxy);
    }
}
```

### `SharedSqliteDbContextFactory` — in-memory SQLite across DbContext instances

```csharp
public class SharedSqliteDbContextFactory : IDbContextFactory<ClaudeManagerDbContext>
{
    private readonly SqliteConnection _conn;
    public SharedSqliteDbContextFactory(SqliteConnection conn) => _conn = conn;

    public ClaudeManagerDbContext CreateDbContext()
    {
        var opts = new DbContextOptionsBuilder<ClaudeManagerDbContext>()
            .UseSqlite(_conn).Options;
        return new ClaudeManagerDbContext(opts);
    }

    public Task<ClaudeManagerDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(CreateDbContext());
}
```

---

## Priority order

| Tier | Tests | Rationale |
|---|---|---|
| **1** | `ClaudeSessionTests`, `SessionStoreTests`, `AgentHubStreamLineTests` | Protect the core state invariants; fast pure unit tests; everything else builds on these |
| **2** | `AgentCommandServiceTests`, `AgentSecretMiddlewareTests`, `PersistenceQueueIntegrationTests` | Command routing correctness, security boundary, DB write path |
| **3** | `StartupRecoveryIntegrationTests`, `AgentHubIntegrationTests` | Recovery correctness and end-to-end confidence; highest setup cost |
| **4** | `ClaudeProcessTests`, `ClaudeValidatorTests`, `MachineIdProviderTests`, `PersistenceQueueChannelTests`, `InfiniteRetryPolicyTests` | Agent-side logic; limited by testability constraints or low risk |
