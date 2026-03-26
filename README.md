# Claude Manager

A centralized dashboard for monitoring and controlling Claude Code sessions across multiple machines, with support for autonomous builds via AgentField/SWE-AF and a shared wiki knowledge base.

## Architecture

| Component | What it is |
|-----------|------------|
| **Hub** | ASP.NET Core + Blazor Server web app. Runs once, centrally. All agents and the MCP server connect to it. |
| **Agent** | .NET console app. Runs on each machine you want to monitor. Spawns and manages `claude` processes, streams output, and optionally launches the MCP server as a child process. |
| **MCP Server** | Lightweight MCP (Model Context Protocol) server. Launched by the Agent as a child process of each Claude session. Exposes `wiki_save` and `wiki_list` tools so Claude can read from and write to the Hub's wiki. |
| **Shared** | DTOs and enums (`SessionStatus`, `BuildStatus`, `StreamLineKind`) referenced by Hub and Agent. |

---

## Before First Run

### 1. Set the shared secret

The Hub and every Agent must share the same secret. Set it in both config files:

**`src/ClaudeManager.Hub/appsettings.json`**
```json
{
  "AgentSecret": "your-secret-here"
}
```

**`src/ClaudeManager.Agent/appsettings.json`**
```json
{
  "Agent": {
    "SharedSecret": "your-secret-here"
  }
}
```

Use any strong random string. This is the only authentication between Hub and Agents.

### 2. Set the Hub URL in each Agent

**`src/ClaudeManager.Agent/appsettings.json`**
```json
{
  "Agent": {
    "HubUrl": "https://<hub-hostname-or-ip>:7247"
  }
}
```

For a local single-machine setup use `https://localhost:7247` (the default HTTPS port from `launchSettings.json`).

### 3. Set a default working directory for each Agent

```json
{
  "Agent": {
    "DefaultWorkingDirectory": "C:\\Projects"
  }
}
```

This is the working directory used when no explicit path is given for a new session.

### 4. (Optional) Display name, Claude binary, and MCP server

```json
{
  "Agent": {
    "DisplayName": "My Dev Laptop",
    "ClaudeBinaryPath": null,
    "McpServerPath": "/path/to/ClaudeManager.McpServer"
  }
}
```

- `DisplayName` — how the machine appears in the dashboard. Defaults to `Environment.MachineName`.
- `ClaudeBinaryPath` — absolute path to the `claude` binary. Leave `null` to resolve from `PATH`.
- `McpServerPath` — path to the `ClaudeManager.McpServer` executable. When set, every Claude session gets `wiki_save` and `wiki_list` MCP tools automatically. Leave `null` to disable.

### 5. Ensure Claude Code is authenticated

The Agent validates `claude` authentication at startup. Run `claude auth login` (or set `ANTHROPIC_API_KEY`) on each Agent machine before starting.

---

## Running

**Start the Hub first:**
```bash
cd src/ClaudeManager.Hub
dotnet run
```

Dashboard: `https://localhost:7247` (or `http://localhost:5258`).

**Then start an Agent on each machine:**
```bash
cd src/ClaudeManager.Agent
dotnet run
```

The Agent connects to the Hub and appears in the dashboard automatically. Machines show as offline when the Agent is not running; session history is preserved.

---

## Features

### Dashboard

The main page shows all machines and their sessions in real time via SignalR.

- **Machine cards** — each machine shows online/offline status, platform icon, and all sessions.
- **New Session** — opens a modal to start a Claude session on any online Agent, with optional initial prompt.
- **Kill Session** — stops an active Claude process immediately.
- **Launch Agent** — appears for machines that have SSH launch config (`KnownMachines`). Starts the Agent remotely without needing a terminal.

### Session Detail

Live session viewer at `/session/{machineId}/{sessionId}`.

- Streams assistant tokens, tool use/results, errors, and process exits in real time.
- Oversized content is truncated with an indicator.
- Follow-up prompt input (Ctrl+Enter to send) when the session is active.
- Historical sessions load from the database when opened after the fact.

### Wiki

A shared knowledge base at `/wiki`, readable and writable by both the UI and Claude sessions via MCP tools.

- **Entries** have a title, category (`project`, `decision`, `bug`, `note`), content, and optional tags.
- **Claude can write** to the wiki by calling `wiki_save(title, category, content, tags)` during any session (when `McpServerPath` is configured on the Agent). Existing entries are upserted by title.
- **Claude can read** the wiki by calling `wiki_list()`, which returns all non-archived entries.
- The UI supports full CRUD plus archive/restore.

### Builds (SWE-AF / AgentField)

Autonomous build orchestration at `/builds`. Requires `SweAf` config; see [Configuration](#hub-configuration) below.

- **Trigger a build** — provide a goal description and a GitHub repo URL. The Hub calls AgentField's `swe-planner.build` agent, which autonomously implements the feature and opens a draft PR.
- **Live status updates** — AgentField sends observability webhook events; the Hub verifies the HMAC-SHA256 signature and updates job status in real time.
- **Externally-triggered builds** — the AgentField webhook fires for all executions under the API key, not just Hub-initiated ones. Builds triggered by Jira, CLI, or other sources are automatically discovered and shown in the dashboard.
- **Recovery** — on startup the Hub polls AgentField for any in-flight jobs to reconcile state missed during downtime.
- **Job controls** (visible for active jobs):
  - **Cancel** — sends a cancel request; status updates when the resulting webhook arrives.
  - **Approve / Reject** — shown when a job is in `Waiting` state (human-in-the-loop approval gate). Sends the decision back to AgentField.
- **Host service control** — Start and Stop buttons in the header when `SweAfHost` is configured. Connects via SSH (or locally) and runs the configured service management commands.

---

## Configuration Reference

### Hub configuration

All settings go in `src/ClaudeManager.Hub/appsettings.json`.

**`AgentSecret`** *(required)*
Shared secret for Agent authentication.

```json
{ "AgentSecret": "your-secret-here" }
```

**`KnownMachines`** *(optional)*
Pre-configures machines so they appear in the dashboard before their Agent connects and enables remote Agent launch via SSH. Add one entry per machine.

```json
{
  "KnownMachines": [
    {
      "MachineId":   "my-workstation",
      "DisplayName": "Dev Workstation",
      "Platform":    "win32",
      "Host":        "192.168.1.10",
      "Port":        22,
      "SshUser":     "alice",
      "SshKeyPath":  "~/.ssh/id_rsa",
      "AgentCommand": "nohup /opt/cm/ClaudeManager.Agent > /dev/null 2>&1 &"
    }
  ]
}
```

| Field | Required | Default | Description |
|-------|----------|---------|-------------|
| `MachineId` | Yes | — | Stable ID; must match what the Agent reports on registration. |
| `DisplayName` | Yes | — | Label shown in the UI. |
| `Platform` | No | `"linux"` | `"win32"`, `"darwin"`, or `"linux"` — controls the dashboard icon. |
| `Host` | Yes | — | Hostname or IP. Use `"localhost"` / `"127.0.0.1"` / `"::1"` to launch locally via `Process.Start` instead of SSH. |
| `Port` | No | `22` | SSH port. |
| `SshUser` | Yes (remote) | — | SSH username. |
| `SshKeyPath` | One of | — | Path to SSH private key. Supports `~` expansion. |
| `SshPassword` | One of | — | SSH password. Key-based auth preferred. |
| `AgentCommand` | Yes | — | Full shell command to launch the Agent, including any backgrounding syntax. |

**`SweAf`** *(optional — enables the Builds page)*

```json
{
  "SweAf": {
    "BaseUrl":       "https://api.agentfield.com",
    "ApiKey":        "af_...",
    "WebhookSecret": "your-webhook-hmac-secret"
  }
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `BaseUrl` | Yes (if using Builds) | AgentField API base URL. |
| `ApiKey` | Yes (if using Builds) | Bearer token for the AgentField API. |
| `WebhookSecret` | No | HMAC-SHA256 secret for verifying AgentField webhook payloads. Leave unset to skip signature verification. |
| `ClaudeBaseUrl` | No | Passed to AgentField in the build trigger payload as `config.base_url`. Use to direct SWE-AF's `claude_code` runtime at a locally-hosted LLM server. |
| `ClaudeApiKey` | No | Passed to AgentField in the trigger payload as `config.api_key`. Pair with `ClaudeBaseUrl` when the local server requires a different key. |

To receive webhook events, register the Hub's endpoint with AgentField:
```
POST https://api.agentfield.com/api/v1/settings/observability-webhook
{ "url": "https://<your-hub>/api/webhooks/agentfield", "secret": "<WebhookSecret>" }
```

**`SweAfHost`** *(optional — enables Start/Stop Service buttons on the Builds page)*

```json
{
  "SweAfHost": {
    "Host":            "192.168.1.20",
    "Port":            22,
    "SshUser":         "ubuntu",
    "SshKeyPath":      "~/.ssh/id_rsa",
    "StartCommand":    "sudo systemctl start agentfield",
    "StopCommand":     "sudo systemctl stop agentfield",
    "AnthropicBaseUrl": "http://localhost:11434",
    "AnthropicApiKey":  "local"
  }
}
```

Uses the same SSH transport as `KnownMachines`. Set `Host` to `"localhost"` to run commands locally. Stop commands that return a non-zero exit code are treated as success when stderr is empty (handles the case where the service was already stopped).

`AnthropicBaseUrl` and `AnthropicApiKey` control how the AgentField process reaches the LLM:
- **Localhost:** set directly as environment variables on the child process.
- **SSH:** prepended as inline shell assignments before the command (e.g. `ANTHROPIC_BASE_URL='...' sudo nohup start.sh`). This works for scripts launched directly but **does not** inject into systemd-managed services — for those, add `Environment=ANTHROPIC_BASE_URL=...` to the `[Service]` section of the unit file instead.

### Agent configuration

All settings go in `src/ClaudeManager.Agent/appsettings.json` under the `"Agent"` key.

| Field | Required | Default | Description |
|-------|----------|---------|-------------|
| `HubUrl` | Yes | — | Hub address (e.g. `https://192.168.1.5:7247`). |
| `SharedSecret` | Yes | — | Must match the Hub's `AgentSecret`. |
| `DisplayName` | No | `MachineName` | Label shown in the dashboard. |
| `ClaudeBinaryPath` | No | (PATH) | Absolute path to the `claude` binary. |
| `DefaultWorkingDirectory` | No | User home | Default `cwd` for new sessions. |
| `McpServerPath` | No | — | Path to `ClaudeManager.McpServer` executable. Enables `wiki_save` / `wiki_list` MCP tools in every session. |
| `ClaudeBaseUrl` | No | — | Sets `ANTHROPIC_BASE_URL` on every `claude` child process. Use to point Claude at a locally-hosted LLM server. |
| `ClaudeApiKey` | No | — | Sets `ANTHROPIC_API_KEY` on every `claude` child process. Typically paired with `ClaudeBaseUrl` when the local server uses a different key (e.g. `"local"`). |

---

## Cross-machine setup

1. The Hub machine must be reachable on port 7247 from all Agent machines — open the firewall port if needed.
2. If using a self-signed TLS certificate (the default in development), Agents will reject it. Either use a trusted certificate on the Hub, or accept the self-signed cert in `AgentService.cs` (acceptable for a private LAN).
3. Set `HubUrl` in each Agent's `appsettings.json` to the Hub's LAN or DNS address.

---

## Data persistence

Session history is stored in a SQLite database at:
- **Windows:** `%LocalAppData%\ClaudeManager\claude_manager.db`
- **Linux/macOS:** `~/.local/share/ClaudeManager/claude_manager.db`

The database is created automatically on first run.

**Retention:** Sessions older than **30 days** are pruned automatically. The pruning job runs every 24 hours (with a 5-minute delay at startup) and cascade-deletes all associated output lines.

**Startup recovery:** On Hub startup, sessions from the last 30 days are loaded into memory. Any session that was `Active` at the time of the last shutdown is marked `Disconnected`. The last 2000 output lines per session are pre-loaded.

**SWE-AF job history:** Build jobs are retained in the same database and are not pruned.

---

## Project structure

```
claude_manager/
├── src/
│   ├── ClaudeManager.Hub/        # Web dashboard (run once, centrally)
│   ├── ClaudeManager.Agent/      # Agent process (run on each monitored machine)
│   └── ClaudeManager.McpServer/  # MCP server (launched by Agent per session)
├── shared/
│   └── ClaudeManager.Shared/     # Shared DTOs and enums
└── tests/
    ├── ClaudeManager.Hub.Tests/         # Unit tests for Hub services
    ├── ClaudeManager.Agent.Tests/       # Unit tests for Agent components
    ├── ClaudeManager.Integration.Tests/ # Full Hub+SignalR integration tests
    └── ClaudeManager.McpServer.Tests/   # MCP wiki tool tests
```
