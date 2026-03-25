# Claude Manager — Implementation Plan

## Overview

A centralized dashboard to monitor and control multiple Claude Code sessions across multiple machines.

- **ClaudeManager.Hub** — ASP.NET Core + Blazor Server + SignalR. Central state and UI.
- **ClaudeManager.Agent** — .NET console app, one per monitored machine. Spawns claude processes.
- **ClaudeManager.Shared** — Shared DTOs referenced by both projects.

---

## Solution Structure

```
claude_manager/
├── ClaudeManager.sln
├── src/
│   ├── ClaudeManager.Hub/
│   │   ├── ClaudeManager.Hub.csproj
│   │   ├── Program.cs
│   │   ├── Hubs/
│   │   │   └── AgentHub.cs
│   │   ├── Services/
│   │   │   ├── SessionStore.cs
│   │   │   ├── DashboardNotifier.cs
│   │   │   └── AgentCommandService.cs
│   │   ├── Persistence/
│   │   │   ├── ClaudeManagerDbContext.cs
│   │   │   ├── Entities/
│   │   │   │   ├── MachineAgentEntity.cs
│   │   │   │   ├── ClaudeSessionEntity.cs
│   │   │   │   └── StreamedLineEntity.cs
│   │   │   ├── PersistenceQueue.cs
│   │   │   ├── StartupRecoveryService.cs
│   │   │   └── Migrations/
│   │   ├── Models/
│   │   │   ├── MachineAgent.cs
│   │   │   ├── ClaudeSession.cs
│   │   │   └── StreamedLine.cs
│   │   ├── Components/
│   │   │   ├── App.razor
│   │   │   ├── Routes.razor
│   │   │   ├── Layout/
│   │   │   │   ├── MainLayout.razor
│   │   │   │   └── NavMenu.razor
│   │   │   └── Pages/
│   │   │       ├── Dashboard.razor
│   │   │       └── SessionDetail.razor
│   │   └── wwwroot/
│   │       ├── app.css
│   │       └── app.js
│   └── ClaudeManager.Agent/
│       ├── ClaudeManager.Agent.csproj
│       ├── Program.cs
│       ├── AgentService.cs
│       ├── ClaudeProcess.cs
│       ├── MachineIdProvider.cs
│       └── Models/
│           └── AgentConfig.cs
└── shared/
    └── ClaudeManager.Shared/
        ├── ClaudeManager.Shared.csproj
        └── Dto/
            ├── StreamLineDto.cs
            ├── StartSessionRequest.cs
            ├── SendPromptRequest.cs
            ├── AgentRegistration.cs
            └── SessionStartedNotification.cs
```

---

## Process Model

> **Critical:** `claude -p` exits after each response. There is no long-running process that accepts
> sequential prompts on stdin. Each prompt is a separate process invocation.

```
Prompt 1:  spawn → claude -p "first prompt" --output-format stream-json
           ← streams JSON lines to stdout → process exits naturally

Prompt 2:  spawn → claude -p "follow-up" --resume <session_id> --output-format stream-json
           ← streams JSON lines → process exits naturally
```

Consequences:
- `ClaudeProcess` represents **one request/response cycle**, not a long-lived connection.
- `SendPrompt` on the hub side spawns a **new** `ClaudeProcess` with `--resume <session_id>`.
- A follow-up prompt can only be sent **after the current process exits** (or queued).
- `ClaudeSession` has a 1:N relationship with `ClaudeProcess` invocations.
- No stdin management needed; stdout is still streamed line-by-line.
- The "IsWaiting" UI concept maps to: a process is currently running for this session.
- The session ID is known before the first invocation only when using `--resume`. For brand-new
  sessions, the ID is unknown until the first `system/init` JSON line arrives.

---

## Data Models

### Hub in-memory models

```csharp
public class MachineAgent
{
    public string MachineId { get; init; }          // Stable GUID (see MachineIdProvider)
    public string SignalRConnectionId { get; set; } // Changes on each reconnect
    public string DisplayName { get; init; }
    public string Platform { get; init; }           // "win32", "linux", "darwin"
    public DateTimeOffset ConnectedAt { get; set; }
    public DateTimeOffset LastHeartbeatAt { get; set; }
    public Dictionary<string, ClaudeSession> Sessions { get; } = new();
}

public class ClaudeSession
{
    public string SessionId { get; set; }           // Set after first system/init line
    public string MachineId { get; init; }
    public string WorkingDirectory { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? InitialPrompt { get; init; }
    public SessionStatus Status { get; set; }       // Active, Ended, Disconnected
    public bool IsProcessRunning { get; set; }      // True while a ClaudeProcess is live
    public List<StreamedLine> OutputLines { get; } = new(); // Capped at 2000
}

public class StreamedLine
{
    public long DbId { get; init; }                 // From DB; used for ordering (not Timestamp)
    public DateTimeOffset Timestamp { get; init; }  // Wall-clock time on hub receipt
    public string SessionId { get; init; }
    public StreamLineKind Kind { get; init; }
    public string Content { get; init; }
    public bool IsContentTruncated { get; init; }
    public string? ToolName { get; init; }
}

public enum StreamLineKind
{
    AssistantToken = 0,
    ToolUse        = 1,
    ToolResult     = 2,
    ResultSummary  = 3,
    Error          = 4,
    ProcessExit    = 5,
}

public enum SessionStatus
{
    Active       = 0,
    Ended        = 1,
    Disconnected = 2,
}
```

### Shared DTOs

```csharp
public record StreamLineDto(
    string MachineId,
    string SessionId,
    string RawJson,
    DateTimeOffset Timestamp
);

public record StartSessionRequest(
    string MachineId,
    string WorkingDirectory,
    string InitialPrompt,
    string? ResumeSessionId  // null = new session
);

public record SendPromptRequest(
    string MachineId,
    string SessionId,
    string Prompt
);

public record AgentRegistration(
    string MachineId,
    string DisplayName,
    string Platform,
    string AgentVersion
);
```

---

## Persistence Layer

### Database

SQLite via EF Core. File stored at `%LocalAppData%/ClaudeManager/claude_manager.db`.
Use `IDbContextFactory<ClaudeManagerDbContext>` throughout (not `AddDbContext`) because
writes occur on thread-pool threads outside request scope.

Enable WAL mode on startup:
```sql
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
```
This allows concurrent Blazor reads alongside the single queue writer.

### Entities

```csharp
[Table("MachineAgents")]
public class MachineAgentEntity
{
    [Key] public string MachineId { get; set; }
    public string DisplayName { get; set; }
    public string Platform { get; set; }
    public DateTimeOffset FirstConnectedAt { get; set; }
    public DateTimeOffset LastConnectedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public ICollection<ClaudeSessionEntity> Sessions { get; set; } = [];
}

[Table("ClaudeSessions")]
public class ClaudeSessionEntity
{
    [Key] public string SessionId { get; set; }
    public string MachineId { get; set; }
    public string WorkingDirectory { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    [MaxLength(4000)] public string? InitialPrompt { get; set; }
    public SessionStatus Status { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }  // denormalised, updated on each line
    public MachineAgentEntity Machine { get; set; }
    public ICollection<StreamedLineEntity> OutputLines { get; set; } = [];
}

[Table("StreamedLines")]
public class StreamedLineEntity
{
    [Key] public long Id { get; set; }              // surrogate PK, auto-increment
    public string SessionId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public StreamLineKind Kind { get; set; }
    [MaxLength(8000)] public string Content { get; set; }
    public bool IsContentTruncated { get; set; }
    public string? ToolName { get; set; }
    public ClaudeSessionEntity Session { get; set; }
}
```

Indexes: `ClaudeSessions.MachineId`, `ClaudeSessions.LastActivityAt`,
`ClaudeSessions.Status`, `StreamedLines.SessionId`.

### Async Write Queue

`PersistenceQueue : BackgroundService` — bounded `Channel<PersistenceWork>` with
`capacity: 2000, FullMode: DropOldest, SingleReader: true`.

`SessionStore` calls `_queue.Enqueue*(...)` fire-and-forget (no await). The single consumer
loop creates a fresh `DbContext` per item via the factory, writes, and `SaveChangesAsync`.

Five enqueue call sites in `SessionStore`:
1. `RegisterAgent` → `EnqueueUpsertAgent`
2. `StartSession` → `EnqueueUpsertSession`
3. `AppendLine` (non-partial lines only) → `EnqueueLine`
4. `EndSession` → `EnqueueSessionStatusChange(..., Ended)`
5. `MarkAgentDisconnected` → `EnqueueSessionStatusChange(..., Disconnected)` for all active sessions

Do NOT persist:
- `IsPartial = true` lines (volume)
- SignalR `ConnectionId` (ephemeral)
- Heartbeat timestamps (live-only)
- Agent disconnect events as rows (use `SessionStatus.Disconnected` flag instead)

### Startup Recovery (`StartupRecoveryService : IHostedService`)

Runs before hub accepts connections (register before `PersistenceQueue` in DI):
1. `db.Database.MigrateAsync()` — apply migrations
2. Set WAL mode pragmas
3. Patch any sessions still `Active` from previous run → `Disconnected`
4. Load sessions from last 7 days into `SessionStore` memory (last 2000 lines each)

### SessionDetail DB Fallback

Two-stage load:
1. Check `SessionStore` (in-memory) — use if present
2. If not found, query DB with `AsNoTracking` — build a local read-only view
   - Show "Loaded from history" indicator
   - Disable live-update subscription
   - Do NOT insert back into `SessionStore`

---

## SignalR Interface

### Agent → Hub (methods on `AgentHub`)

```csharp
Task RegisterAgent(AgentRegistration registration);
Task StreamLine(StreamLineDto line);
Task SessionStarted(string machineId, string sessionId, string workingDirectory);
Task SessionEnded(string machineId, string sessionId, int exitCode);
Task Heartbeat(string machineId);
```

### Hub → Agent (via `Clients.Client(connectionId).SendAsync(...)`)

```
"StartSession"  payload: StartSessionRequest
"SendPrompt"    payload: SendPromptRequest   ← spawns new ClaudeProcess with --resume
"KillSession"   payload: (machineId, sessionId)
```

### Hub → Blazor

Blazor components do not connect to SignalR directly. They subscribe to C# events on
`DashboardNotifier` (singleton), which is fired from `AgentHub` methods via `SessionStore`.
All event handlers in components must use `InvokeAsync(() => { ...; StateHasChanged(); })`.

```csharp
public class DashboardNotifier
{
    event Action<MachineAgent>          AgentConnected;
    event Action<string>                AgentDisconnected;   // machineId
    event Action<StreamedLine>          LineStreamed;
    event Action<ClaudeSession>         SessionStarted;
    event Action<string, string, int>   SessionEnded;        // machineId, sessionId, exitCode
    event Action<string>                HeartbeatReceived;   // machineId
}
```

### Hub configuration

```csharp
// 10MB limit — tool results reading large files will exceed the 32KB default
builder.Services.AddSignalR(opts => opts.MaximumReceiveMessageSize = 10 * 1024 * 1024);
```

Authentication: middleware checks `X-Agent-Secret` header on `/agenthub`. Reject connections
missing or mismatching the secret. Store the secret in `appsettings.json` / environment variable
on both hub and agent.

---

## Agent Architecture

### `MachineIdProvider`

Generates a stable GUID on first run and persists it to a local file
(`%LocalAppData%/ClaudeManager/machine_id`). Falls back to `Environment.MachineName` only if
the file cannot be written. This prevents collisions between machines that happen to share a hostname.

### `AgentConfig`

```csharp
public class AgentConfig
{
    public string HubUrl { get; set; }              // e.g. "https://hub-machine:5000"
    public string SharedSecret { get; set; }
    public string DisplayName { get; set; }         // defaults to MachineName
    public string? ClaudeBinaryPath { get; set; }   // optional override; defaults to PATH lookup
    public string DefaultWorkingDirectory { get; set; }
}
```

### Startup validation

Before connecting to the hub, the agent verifies:
1. `claude` binary is resolvable (PATH or `ClaudeBinaryPath` config)
2. `claude` is authenticated — run `claude -p "ping" --output-format json` with a short timeout;
   if it fails or returns an auth error, log a clear message and exit with a non-zero code

### `AgentService : BackgroundService`

1. Builds `HubConnection` with `X-Agent-Secret` header and custom retry policy (0, 2, 5, 15, 30s,
   then every 60s indefinitely — not the default finite retry)
2. On `Reconnected`: re-calls `RegisterAgent` and re-announces any sessions with status `Active`
   from `SessionStore` (there shouldn't be any with the new process model, but defensively correct)
3. Registers handlers for `StartSession`, `SendPrompt`, `KillSession`
4. Maintains `ConcurrentDictionary<string, ClaudeProcess>` keyed by `sessionId`
5. On `SendPrompt`: if a process is currently running for that session, queue the prompt;
   otherwise spawn immediately with `--resume`

### `ClaudeProcess` — one request/response cycle

```
Constructor params: workingDirectory, prompt, sessionId (null for new sessions)

Launch args:
  claude -p "<prompt>" --output-format stream-json [--resume <sessionId>]

RedirectStandardInput  = false  (not needed — each invocation is one prompt)
RedirectStandardOutput = true
RedirectStandardError  = true
UseShellExecute        = false
WorkingDirectory       = <configured path>
```

Lifecycle:
1. `StartAsync()` — starts the process, begins stdout read loop in a background task
2. First JSON line is `system/init` containing `session_id` — extract and expose as `SessionId`
   property. This is the canonical ID for all future `--resume` calls.
3. For `--resume` invocations, the session ID is already known; confirm it matches the init line.
4. Each line raises `OnOutputLine(string rawJson)` — `AgentService` forwards to hub via `StreamLine`
5. `null` from `ReadLineAsync` = EOF = process exited. Read exit code, raise `OnExit(int exitCode)`.
6. `KillAsync()` — `Process.Kill(entireProcessTree: true)`

**Orphan prevention:**
- Windows: assign child process to a Job Object (`AssignProcessToJobObject`) with
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. When the agent process exits for any reason
  (including crash), the OS closes the Job Object handle and kills all children.
- Linux: set `prctl(PR_SET_PDEATHSIG, SIGTERM)` via P/Invoke on the child process, or use
  a process group and kill the group on agent shutdown.

**`--resume` with unknown/expired session:**
- Watch for an error result in the first JSON line (type `"result"`, subtype `"error"`)
- If detected, notify hub via `SessionEnded` with a specific exit code, and surface a
  user-visible error in the dashboard rather than silently starting a fresh session.

**stderr:**
- Read stderr in a parallel background task; forward lines to hub as `StreamLineKind.Error`.
  Do not ignore stderr — claude may emit auth errors, rate limit notices, or crash traces there.

---

## Blazor Component Structure

```
MainLayout
├── NavMenu              — subscribes to agent connect/disconnect; machine status dots; session links
├── Dashboard "/"        — machine cards + new session modal
│   ├── MachineCard      — name, online/offline badge, session list
│   └── SessionRow       — working dir, start time, IsProcessRunning indicator, link to detail
└── SessionDetail "/session/{machineId}/{sessionId}"
    ├── OutputPane        — Virtualize component (not a plain list); replays history then streams
    │   └── StreamLineView — styled by Kind: white/amber/blue/red; shows truncation indicator
    └── PromptInput       — textarea, Ctrl+Enter to submit; disabled when IsProcessRunning=true
```

### Auto-scroll behaviour

Track whether the user has manually scrolled up via a JS `scroll` event listener on the output
container. If they have, pause auto-scroll. Resume auto-scroll when they scroll back to the bottom.
Implement as a small JS module in `wwwroot/app.js` with `dotnet.invokeMethodAsync` callbacks.
Without this, auto-scroll fights the user whenever they try to read previous output.

### DOM performance

Use `<Virtualize>` for `OutputPane`. A session with thousands of lines rendered as DOM nodes
will freeze the browser. Virtualize renders only visible rows.

---

## Line Ordering

Display and store lines ordered by **`StreamedLineEntity.Id`** (auto-increment surrogate key),
not by `Timestamp`. Timestamps come from different machines with potential clock skew and are
not guaranteed to be monotonic. `Id` reflects true insertion order at the hub.

Set `Timestamp` to hub receipt time (`DateTimeOffset.UtcNow` in `AgentHub.StreamLine`), not
the agent's clock. This normalizes clock skew and keeps timestamps meaningful in the UI.

---

## Content Truncation

`StreamedLineEntity.Content` is capped at 8,000 characters. When truncating, set
`IsContentTruncated = true`. The `StreamLineView` component shows a visual indicator
(e.g., a muted "content truncated" note) for truncated lines so the user knows data was cut.

---

## Implementation Phases

### Phase 1 — Solution scaffold
- `dotnet new sln -n ClaudeManager`
- `dotnet new blazorserver -n ClaudeManager.Hub`
- `dotnet new console -n ClaudeManager.Agent`
- `dotnet new classlib -n ClaudeManager.Shared`
- Add all to solution; add project references (Hub → Shared, Agent → Shared)
- NuGet packages:
  - Hub: `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.EntityFrameworkCore.Design`
  - Agent: `Microsoft.AspNetCore.SignalR.Client`, `Microsoft.Extensions.Hosting`

### Phase 2 — Shared DTOs
All records in `ClaudeManager.Shared/Dto/`. No logic — pure data.

### Phase 3 — SessionStore + DashboardNotifier
- `SessionStore` singleton: `ConcurrentDictionary<string, MachineAgent>`
- Mutation methods: `RegisterAgent`, `MarkAgentDisconnected`, `StartSession`, `AppendLine`,
  `EndSession`, `GetSession`, `GetAllMachines`, `EnsureAgentFromDb`, `RestoreSessionFromDb`
- `DashboardNotifier` singleton: C# events, fired by `SessionStore` after mutations
- `OutputLines` list capped at 2000 with `lock(_session)` on append

### Phase 3.5 — EF Core + SQLite setup
- Create entity classes and `ClaudeManagerDbContext`
- Register `IDbContextFactory` in `Program.cs`
- `dotnet ef migrations add InitialSchema`
- Smoke test: hub starts, DB file created, tables exist

### Phase 3.6 — PersistenceQueue
- `PersistenceWork` discriminated union records
- `PersistenceQueue : BackgroundService` with bounded channel
- Inject into `SessionStore`; wire all five call sites
- Add `ToEntity()` mapping helpers

### Phase 3.7 — StartupRecoveryService
- Apply migrations, set WAL pragmas, patch stale Active sessions, load 7-day history
- Register before `PersistenceQueue` in DI
- Smoke test: run session, restart hub, verify history appears

### Phase 4 — AgentHub
- Wire all five agent→hub methods
- `X-Agent-Secret` auth middleware on `/agenthub`
- Configure SignalR 10MB message size limit
- `AgentCommandService` wrapping `IHubContext<AgentHub>` for hub→agent invocations

### Phase 5 — Agent: MachineIdProvider + startup validation
- `MachineIdProvider`: generate/persist stable GUID
- Startup: resolve `claude` binary, validate authentication

### Phase 6 — Agent: ClaudeProcess
- Process launch with stdout/stderr redirect (no stdin)
- Stdout read loop, JSON session ID extraction
- Stderr read loop (parallel task), forwarded as Error lines
- `KillAsync` with `entireProcessTree: true`
- Orphan prevention: Job Object (Windows) / process group (Linux)
- Handle `--resume` error detection (watch for error result on first line)

### Phase 7 — Agent: AgentService
- `HubConnection` with custom infinite retry policy
- Register handlers: `StartSession`, `SendPrompt` (queued if process running), `KillSession`
- Reconnect: re-register, re-announce active sessions
- Forward `ClaudeProcess` events to hub

### Phase 8 — Blazor: Dashboard
- `Dashboard.razor`, `MachineCard.razor`, `SessionRow.razor`
- New session modal: machine selector, working dir, prompt
- End-to-end test: agent connects, session appears, machine shows online

### Phase 9 — Blazor: SessionDetail
- `SessionDetail.razor` with two-stage load (in-memory → DB fallback)
- `OutputPane.razor` with `<Virtualize>`
- `StreamLineView.razor` styled by Kind, truncation indicator
- `PromptInput.razor` with Ctrl+Enter, disabled when `IsProcessRunning`
- Auto-scroll JS module with scroll position tracking

### Phase 10 — Polish and edge cases
- `NavMenu` machine status dots + session links
- "Loaded from history" indicator in SessionDetail
- Working directory validation on agent before spawn
- Log warning when `PersistenceQueue.TryWrite` returns false (channel full)
- Manual pruning endpoint or scheduled cleanup for old sessions (optional)

---

## Key Edge Cases

| Scenario | Handling |
|---|---|
| Agent reconnects | Re-register + re-announce; hub updates `SignalRConnectionId`; lines during disconnect are lost |
| Hub restarts | `StartupRecoveryService` patches stale sessions to `Disconnected`; reloads 7-day history |
| claude process dies unexpectedly | EOF on stdout → `OnExit` → `SessionEnded` on hub → `SessionStatus.Ended` |
| Agent crashes (not clean shutdown) | Job Object / process group kills orphan claude children |
| `--resume` session not found | Error on first JSON line → surface error in UI; do not silently start fresh |
| User scrolls up during stream | JS scroll tracker pauses auto-scroll; resumes at bottom |
| Two machines same hostname | `MachineIdProvider` uses stable persisted GUID, not hostname |
| Large tool result > 8KB | Truncate content, set `IsContentTruncated = true`, show indicator in UI |
| SignalR line > 10MB | Extremely unlikely but configure 10MB limit; log and drop if exceeded |
| Clock skew across machines | All timestamps set to hub receipt time; ordering uses DB `Id` |
| Stale `SignalRConnectionId` | `hub.Clients.Client(deadId).SendAsync` silently no-ops; wrap with timeout if needed |
| Multiple Blazor tabs | `InvokeAsync(StateHasChanged)` in all event handlers; each circuit is independent |
| SQLite lock conflict | WAL mode handles concurrent reads; single-writer queue prevents write conflicts |
