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
- **Session filtering** — filter sessions by directory substring and status (Active / Ended / Disconnected).
- **Nav badge** — the Dashboard nav link shows a count of currently active sessions.
- **New Session** — opens a modal to start a Claude session on any online Agent, with optional initial prompt.
- **Kill Session** — stops an active Claude process immediately.
- **Launch Agent** — appears for machines that have SSH launch config (`KnownMachines`). Starts the Agent remotely without needing a terminal.

### Session Detail

Live session viewer at `/session/{machineId}/{sessionId}`.

- Streams assistant tokens, tool use/results, errors, and process exits in real time.
- Oversized content is truncated with an indicator.
- Follow-up prompt input (Ctrl+Enter to send) when the session is active.
- Historical sessions load from the database when opened after the fact.
- **Download transcript** — exports the full session as a plain-text file (`claude-session-{id}-{date}.txt`) with a metadata header and per-line timestamps.

### Notifications

The Hub surfaces status changes as in-app toasts and browser notifications.

- **Build events** — toasts when a job succeeds, fails, is waiting for approval, or is cancelled.
- **Session events** — toast when a session ends (success or non-zero exit).
- **Browser notifications** — shown when the tab is not in focus (requires browser permission; a prompt appears on first load).

### Wiki

A shared knowledge base at `/wiki`, readable and writable by both the UI and Claude sessions via MCP tools.

- **Entries** have a title, category (`project`, `decision`, `bug`, `note`), content, and optional tags.
- **Search/filter** — filter entries by title, category, tags, or content. Toggle to show archived entries.
- **Claude can write** to the wiki by calling `wiki_save(title, category, content, tags)` during any session (when `McpServerPath` is configured on the Agent). Existing entries are upserted by title.
- **Claude can read** the wiki by calling `wiki_list()`, which returns all non-archived entries.
- The UI supports full CRUD plus archive/restore.

### Foundry (SWE-AF / AgentField)

Autonomous build orchestration at `/foundry`. All SWE-AF configuration is managed through the UI (stored in the database); no `appsettings.json` entries are required.

The Foundry page has two tabs:

**Builds tab**

- **Trigger a build** — provide a goal description and a GitHub repo URL. The Hub provisions a fresh isolated agent stack, waits for it to become healthy, registers the webhook on it, then calls AgentField's `swe-planner.build` agent, which autonomously implements the feature and opens a draft PR. Provisioning status messages are shown in the modal during the process and written to the build log for later reference.
- **Build stats** — summary bar showing total, succeeded, failed, running, and waiting counts.
- **Build detail page** — click any build goal to open `/builds/{id}` with full metadata, timeline, PR links, error details, build log, and the raw AgentField execution result.
- **Live status updates** — AgentField sends observability webhook events per-job; the Hub verifies the HMAC-SHA256 signature and updates job status in real time.
- **Externally-triggered builds** — the webhook fires for all executions under the API key, not just Hub-initiated ones. Builds triggered by Jira, CLI, or other sources are automatically discovered and shown.
- **Recovery** — on startup the Hub polls AgentField for any in-flight jobs to reconcile state missed during downtime, including extracting PR URLs from completed jobs.
- **Job controls** (visible for active jobs):
  - **Cancel** — sends a cancel request; status updates when the resulting webhook arrives.
  - **Approve / Reject** — shown when a job is in `Waiting` state (human-in-the-loop approval gate).
  - **Retry** — re-triggers a failed or cancelled build with the same goal and repository.

**Settings tab**

All Foundry configuration is set here and stored in the database:

| Setting | Description |
|---------|-------------|
| Control Plane URL | Fallback AgentField URL (used when per-build provisioning is not configured). |
| API Key | Bearer token for the AgentField API. |
| Webhook Secret | HMAC-SHA256 secret for verifying webhook payloads. |
| Hub Public URL | Public URL of this Hub — used to register the observability webhook on each per-job control plane. |
| Runtime | `claude_code` (Claude) or `open_code` (open-source models). |
| Models | Per-role model overrides (Default, Coder, QA). |
| **Provisioning Host** | SSH host, port, user, key/password, and sudo settings for the machine that runs Docker. |
| **SWE-AF Repo Path** | Path to the SWE-AF Docker Compose repo on the provisioning host (default: `~/swe-af`). |
| **Port Range** | Start and end of the port block pool. Each build claims 3 consecutive ports (control-plane, swe-agent, swe-fast). |
| **LLM Deployment** | Optional: select a configured LLM deployment. Sets `ANTHROPIC_BASE_URL` in the agent's `.env` to point at the nginx proxy for that host. |
| **Control Plane Image Tag** | Overrides `AGENTFIELD_IMAGE_TAG` in the `.env` file (e.g. `latest`, `v1.2.3`). |
| **Compose Override** | Raw YAML written to `docker-compose.override.yml` on the provisioning host before each build. |

### Per-build isolated provisioning

When provisioning is configured, each build gets a fully isolated Docker Compose stack:

- The Hub allocates a block of 3 consecutive ports from the configured range (one each for control-plane, swe-agent, swe-fast).
- A unique Compose project (`agentfield-{id}`) is created so multiple builds can run in parallel without port conflicts.
- `docker-compose.hub.yml` is written to the repo on the provisioning host, overriding the port mappings for all three services and setting `AGENTFIELD_SERVER` / `AGENT_CALLBACK_URL` to Docker service DNS names so intra-stack communication is isolated to the project network.
- The Hub polls the control plane's `/health` endpoint (5-second per-request timeout) until it responds, then registers the observability webhook on it and triggers the build. If the `swe-planner` agent hasn't finished registering yet, the trigger is retried up to 10 times (5 s apart) before failing.
- On completion (succeeded, failed, or cancelled), the stack is torn down automatically via `docker compose down`.

### LLM Servers

GPU host and vLLM deployment management at `/llm`.

- **GPU hosts** — register remote machines (SSH credentials, optional sudo) that run vLLM Docker containers.
- **Deployments** — configure and start vLLM instances on a host, specifying model ID, GPU indices, quantization, host port, image tag, and extra args. The Hub starts the container and polls for health; large models that take longer than 30 s to load continue polling in the background.
- **Detect containers** — scan a host for running vLLM containers and import any that aren't already tracked.
- **nginx proxy** — each GPU host can have a proxy port configured. Clicking **Apply Proxy** generates a round-robin nginx config across all `Running` deployments on that host, writes it to `~/.vllm-proxy/nginx.conf` on the host, and starts or reloads the `vllm-nginx-proxy` container (`--network host`). The config is automatically regenerated whenever a deployment starts or stops. The proxy URL from a configured host is automatically used as `ANTHROPIC_BASE_URL` when triggering Foundry builds via that host's associated LLM deployment.
- **GPU info** — query NVIDIA GPU status (model, VRAM, utilization) from the host via `nvidia-smi`.

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

**Foundry / SWE-AF** *(UI-configured — no `appsettings.json` entries required)*

All Foundry settings (API key, webhook secret, provisioning host, port range, LLM deployment, model overrides, etc.) are entered in the **Settings** tab of the Foundry page (`/foundry`) and stored in the SQLite database. There is nothing to add to `appsettings.json` for this feature.

The SWE-AF stack that the Hub provisions consists of three Docker Compose services:

| Service | Internal port | Role |
|---------|--------------|------|
| `control-plane` | 8080 | AgentField REST API |
| `swe-agent` | 8003 | Planning / coding agent (`swe-planner` node) |
| `swe-fast` | 8004 | Fast reasoning agent |

Each build is assigned a block of 3 consecutive host ports from the configured range. The Hub writes a `docker-compose.hub.yml` to the provisioning host that overrides the port mappings for all three services and sets `AGENTFIELD_SERVER` / `AGENT_CALLBACK_URL` to Docker service-DNS names so the containers communicate within their isolated project network.

**LLM Servers** *(UI-configured — no `appsettings.json` entries required)*

GPU hosts and vLLM deployments are registered and managed entirely through the LLM Servers page (`/llm`). No `appsettings.json` configuration is needed.

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

**Retention:** The pruning job runs every 24 hours (with a 5-minute delay at startup):
- **Sessions** — pruned after **30 days** of inactivity; cascade-deletes all associated output lines.
- **Build jobs** — pruned after **90 days** from creation date.

**Startup recovery:** On Hub startup, sessions from the last 30 days are loaded into memory. Any session that was `Active` at the time of the last shutdown is marked `Disconnected`. The last 2000 output lines per session are pre-loaded.

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
