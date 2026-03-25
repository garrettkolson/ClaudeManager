# Claude Manager

A centralized dashboard for monitoring and controlling Claude Code sessions across multiple machines.

- **Hub** — ASP.NET Core + Blazor Server web app. Runs on one machine; all agents connect to it.
- **Agent** — .NET console app. Runs on each machine you want to monitor; spawns and manages `claude` processes.

---

## Before First Run

### 1. Set the shared secret

The hub and every agent must share the same secret. Edit both files:

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

Use any strong random string. This is the only authentication between hub and agents.

### 2. Set the Hub URL in each agent

Each agent needs to know where the hub is. Edit the agent config on every machine you plan to run an agent on:

**`src/ClaudeManager.Agent/appsettings.json`**
```json
{
  "Agent": {
    "HubUrl": "https://<hub-machine-hostname-or-ip>:7247"
  }
}
```

When running locally for the first time, use `https://localhost:7247` (the default HTTPS port from `launchSettings.json`).

### 3. Set a default working directory for each agent

```json
{
  "Agent": {
    "DefaultWorkingDirectory": "C:\\Projects"
  }
}
```

This is used when a follow-up prompt is sent and no explicit directory is specified. It should be a path that exists on the agent's machine.

### 4. (Optional) Set a display name and claude binary path

```json
{
  "Agent": {
    "DisplayName": "My Dev Laptop",
    "ClaudeBinaryPath": null
  }
}
```

- `DisplayName` — how the machine appears in the dashboard. Defaults to `Environment.MachineName`.
- `ClaudeBinaryPath` — absolute path to the `claude` binary. Leave `null` to resolve from `PATH`.

### 5. Ensure Claude Code is authenticated

The agent validates `claude` authentication at startup. If it isn't authenticated, the agent will exit with a clear error message. Run `claude auth login` (or set `ANTHROPIC_API_KEY`) on each agent machine before starting the agent.

---

## Running

**Start the hub first:**
```bash
cd src/ClaudeManager.Hub
dotnet run
```

The dashboard will be available at `https://localhost:7247` (or `http://localhost:5258`).

**Then start an agent on each machine you want to monitor:**
```bash
cd src/ClaudeManager.Agent
dotnet run
```

The agent will validate claude, connect to the hub, and appear in the dashboard automatically. Machines show as offline when the agent is not running; their session history is preserved.

---

## Cross-machine setup

When the hub and agents run on different machines:

1. The hub machine must be reachable on port 7247 (or whichever port you configure) from agent machines.
2. Open the firewall port if needed.
3. If using a self-signed TLS certificate (the default in development), the agent will reject it. Either:
   - Use a trusted certificate on the hub, or
   - Add `HttpClientHandler.ServerCertificateCustomValidationCallback` in `AgentService.cs` to allow the self-signed cert (acceptable for a personal LAN setup)
4. Set `HubUrl` in each agent's `appsettings.json` to the hub's LAN address.

---

## Project structure

```
claude_manager/
├── src/
│   ├── ClaudeManager.Hub/       # Web dashboard (run once, centrally)
│   └── ClaudeManager.Agent/     # Agent process (run on each monitored machine)
└── shared/
    └── ClaudeManager.Shared/    # Shared DTOs referenced by both projects
```

## Data persistence

Session history is stored in a SQLite database at:
- **Windows:** `%LocalAppData%\ClaudeManager\claude_manager.db`
- **Linux/macOS:** `~/.local/share/ClaudeManager/claude_manager.db`

The database is created automatically on first run. Sessions from the last 7 days are loaded into memory on hub startup. If the hub is restarted mid-session, sessions are marked as `Disconnected` and their history is preserved.
