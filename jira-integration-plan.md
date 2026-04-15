# Jira Integration — Implementation Plan

## Overview

Fetch Jira issues on-demand from the Jira Cloud REST API, display them as a Kanban board grouped by status, and provide one-click actions to push an issue to a SWE-AF build or an agent session. Issues in the "On Deck" column are synced into Claude Manager automatically. When a build or session completes and is approved, the linked issue is transitioned to "Review" in Jira automatically. Only the link records (issue key → local build/session) are persisted in SQLite — no full Jira mirroring.

---

## Phase 1 — Configuration & HTTP Client

**New file:** `Persistence/Entities/JiraConfigEntity.cs`

Single-row DB table (same pattern as `SweAfConfigEntity`):
```
Id (long, PK, identity)
BaseUrl          [MaxLength(500)]   // https://yourco.atlassian.net
Email            [MaxLength(500)]   // for Basic auth
ApiToken         [MaxLength(500)]   // Atlassian API token
DefaultProjectKey [MaxLength(50)]
DefaultJql       [MaxLength(1000)]  // default: "project = {key} AND statusCategory != Done ORDER BY rank ASC"
DefaultRepoUrl   [MaxLength(1000)]
OnDeckStatusName [MaxLength(100)]   // default: "On Deck"
ReviewStatusName [MaxLength(100)]   // default: "Review"
PollingIntervalSecs (int)           // default: 60
WebhookSecret    [MaxLength(500)]
[NotMapped] IsConfigured            // computed: BaseUrl + Email + ApiToken all set
```

**New file:** `Services/JiraConfigService.cs` (implements `IHostedService`)

Follows `SweAfConfigService` exactly:
- Loads the single `JiraConfigEntity` row at startup and caches it
- `GetConfig()` — sync, returns cached entity
- `GetConfigAsync()` — async, bypasses cache (for settings UI load)
- `SaveAsync(entity)` — upserts the single row, refreshes cache
- `IsConfigured` — sync property from cache

**Modify:** `Persistence/ClaudeManagerDbContext.cs`
- Add `DbSet<JiraConfigEntity> JiraConfigs`

**New migration:** `AddJiraConfig`

**Modify:** `Program.cs`
- Register `JiraConfigService` as both singleton and hosted service (same pattern as `SweAfConfigService`)
- Register named `HttpClient` for Jira — headers are set per-request from `JiraConfigService.GetConfig()` rather than at registration time, since credentials can change without restart

---

## Phase 2 — DTO Models

**New file:** `Services/JiraDtos.cs`

Records needed (deserialized from Jira REST API v3):
- `JiraIssue` — key, summary, description (Atlassian Document Format → plain text helper), status, issuetype, priority, assignee, labels, story points, URL
- `JiraStatus` — id, name, statusCategory (To Do / In Progress / Done)
- `JiraSearchResult` — issues list, total, maxResults
- `JiraTransition` — id, name, to (status)

A `ToPlainText(AtlassianDocument)` helper to strip the ADF JSON into readable text for use as a build goal/agent prompt.

---

## Phase 3 — Service Layer

**New file:** `Services/JiraService.cs`

```
GetIssuesAsync(jql, maxResults)       → fetches /rest/api/3/search?jql=...&fields=...
GetIssueAsync(issueKey)               → fetches /rest/api/3/issue/{key}
GetTransitionsAsync(issueKey)         → fetches /rest/api/3/issue/{key}/transitions
TransitionIssueAsync(key, statusName) → resolves transition by name, then POSTs to /rest/api/3/issue/{key}/transitions
TransitionToReviewAsync(issueKey)     → calls TransitionIssueAsync with ReviewStatusName from config
FormatAsPrompt(issue)                 → combines key + summary + description into agent/build goal string
```

Follows the `SweAfService` pattern: constructor-injected `HttpClient` + `JiraConfigService` + `IDbContextFactory` + `ILogger`.

`TransitionIssueAsync` resolves the target transition by name at call time (one extra GET per transition) rather than caching IDs, since transition IDs are project-specific and can change.

---

## Phase 1b — Inbound Sync: "On Deck" → Claude Manager

Two mechanisms are implemented and run concurrently. Polling is the baseline (always works); webhooks provide instant updates when the Hub is publicly accessible.

### Option A — Background Polling (baseline, always enabled)

**New file:** `Services/JiraPollingService.cs` (implements `BackgroundService`)

- On each tick (configurable via `PollingIntervalSecs`, default 60s), queries:
  ```
  project = PROJ AND status = "On Deck" AND updated >= -{interval+buffer}s
  ```
- Compares results against known issue keys already in the board (held in a shared `JiraIssueCache` singleton)
- Newly discovered issues are added to the cache and a `JiraNotifier.OnDeckIssueAdded` event fires to refresh the UI in real-time
- Startup: also runs a full `status = "On Deck"` query to load any issues that arrived while the Hub was offline

**New file:** `Services/JiraIssueCache.cs`

In-memory store (thread-safe) for the current set of "On Deck" issues. Shared between the polling service, webhook handler, and the Kanban UI. Mirrors the `SessionStore` pattern.

**New file:** `Services/JiraNotifier.cs`

Event source for real-time UI updates, following `BuildNotifier` / `DashboardNotifier` pattern:
```
event Action<JiraIssue> OnDeckIssueAdded
event Action<string>    IssueRemoved        // issueKey
```

### Option B — Jira Webhooks (enhancement, requires public Hub URL)

**Modify:** `Program.cs` — map `POST /api/webhooks/jira`

Handler logic:
1. Verify `X-Atlassian-Webhook-Identifier` header and optional HMAC signature (using `WebhookSecret` from config)
2. Deserialize the Jira webhook payload — filter for `jira:issue_updated` events where `changelog.items` contains a status change *to* `OnDeckStatusName`
3. Add the issue to `JiraIssueCache` and fire `JiraNotifier.OnDeckIssueAdded`

**Jira setup:** Register in Jira Project Settings → System → Webhooks, pointing to `https://your-hub/api/webhooks/jira`. Event: `Issue updated`. The polling service still runs as a reconciliation fallback.

**Alternative:** Configure via Jira Automation Rules ("When issue transitions to On Deck → send webhook") for finer-grained trigger conditions (e.g., only for specific assignees or priorities) without any additional Hub code changes.

---

## Phase 4 — Link Tracking (lightweight DB)

**New file:** `Persistence/Entities/JiraIssueLinkEntity.cs`
```
Id (long, PK)
IssueKey (string, max 50)      // e.g. "PROJ-123"
IssueSummary (string, max 500)
LinkType (enum: SweAfBuild, AgentSession)
SweAfJobId (long?, FK → SweAfJobs nullable)
SessionId (string?, FK → ClaudeSessions nullable)
LinkedAt (DateTimeOffset)
ReviewTransitionedAt (DateTimeOffset?)  // set when Hub moves issue to Review
```

**Modify:** `Persistence/ClaudeManagerDbContext.cs`
- Add `DbSet<JiraIssueLinkEntity> JiraIssueLinks`
- Index on `IssueKey` for fast per-issue link lookups

**New migration:** `AddJiraIssueLinks`

---

## Phase 5 — Kanban Board UI

**New file:** `Components/Pages/Issues.razor` at route `/jira`

**Layout:**
```
┌─────────────────────────────────────────────────────────────┐
│ Jira Issues              [JQL input]   [Refresh]            │
├──────────────┬──────────────┬──────────────┬────────────────┤
│   Backlog    │   On Deck    │  In Progress │     Done       │
│  (hidden     │   ← auto     │              │  (collapsed)   │
│  by default) │   populated  │              │                │
│              │ ┌──────────┐ │ ┌──────────┐ │                │
│              │ │PROJ-42   │ │ │PROJ-38   │ │                │
│              │ │Fix login │ │ │Add Kanban│ │                │
│              │ │bug       │ │ │board     │ │                │
│              │ │🔨 Build  │ │ │🤖 Agent  │ │                │
│              │ │⚡ Agent  │ │ │🔨 Build  │ │                │
│              │ └──────────┘ │ └──────────┘ │                │
└──────────────┴──────────────┴──────────────┴────────────────┘
```

**Issue card** shows:
- Issue key (links to Jira in new tab), summary, issuetype icon, priority badge, assignee avatar/name
- Linked builds/sessions badge (if any exist in `JiraIssueLinks`)
- "Push to Build" and "Push to Agent" action buttons

**Columns** are derived from the distinct statuses of the fetched issues, ordered by status category (To Do → In Progress → Done). "Done" column starts collapsed to reduce noise. The "On Deck" column is always shown and populated from `JiraIssueCache`.

**Modals:**
1. **Issue Detail modal** — full description (plain text), linked builds/sessions with status, transitions
2. **Push to Build modal** — pre-filled goal (`[PROJ-42] Fix login bug\n\n<description>`), editable repo URL (defaulting to config), then calls `SweAfService.TriggerBuildAsync` and saves a `JiraIssueLinkEntity`
3. **Push to Agent modal** — agent picker (dropdown of connected machines from `SessionStore`), pre-filled prompt, then starts a new session and saves a link

The component subscribes to `JiraNotifier.OnDeckIssueAdded` to update the board in real-time without manual refresh.

**Settings panel** (collapsible section at the top of the page, same pattern as Foundry's config panel):
- Loads via `JiraConfigService.GetConfigAsync()` on first expand
- Fields: Base URL, Email, API Token (password input), Default Project Key, Default JQL, Default Repo URL, On Deck Status Name, Review Status Name, Polling Interval (seconds), Webhook Secret (password input)
- Save calls `JiraConfigService.SaveAsync(entity)` and re-initializes the Jira `HttpClient` credentials
- Shows an "unconfigured" banner on the board when `JiraConfigService.IsConfigured` is false, with a link to expand the settings panel

**State:**
```
_issues              List<JiraIssue>?
_links               Dictionary<string, List<JiraIssueLinkEntity>>  // keyed by IssueKey
_loading             bool
_error               string?
_jql                 string   // editable, seeded from config
_selectedIssue       JiraIssue?
_showDetailModal     bool
_showBuildModal      bool
_showAgentModal      bool
_triggering          bool
```

---

## Phase 6 — Outbound Sync: Approved → Jira "Review"

When work is approved in Claude Manager, the linked Jira issue is automatically transitioned to the "Review" column.

### SWE-AF Builds

**Modify:** `Services/SweAfService.cs` — `ApplyEventAsync`

`ApproveJobAsync` no longer sets local status — it only POSTs an approval response to the AgentField control plane. The actual `Succeeded` transition happens inside `ApplyEventAsync` when an `execution_completed` webhook arrives. Hook the Jira transition there: after `job.Status = BuildStatus.Succeeded` and `db.SaveChangesAsync()`, look up any `JiraIssueLinkEntity` with `SweAfJobId = job.Id`. If found, call `JiraService.TransitionToReviewAsync(issueKey)` and set `ReviewTransitionedAt`. Errors are logged but do not fail the webhook — the user can manually transition in Jira if needed.

### Agent Sessions

Sessions don't have a formal approval step, so a manual trigger is used:

**Modify:** `Components/Pages/SessionDetail.razor`

When a session has a linked `JiraIssueLinkEntity`, show a "Mark Complete & Move to Review" button in the session actions bar. Clicking it calls `JiraService.TransitionToReviewAsync` and records `ReviewTransitionedAt` on the link entity. The button is hidden once `ReviewTransitionedAt` is set.

---

## Phase 7 — Navigation

**Modify:** `Components/Layout/NavMenu.razor`
- Add `<a href="/jira" class="nav-link">Jira</a>` after the Foundry link (SWE-AF/builds were consolidated into `/foundry` — there is no separate Builds link)
- Optionally show a badge with count of unlinked "On Deck" issues (nice-to-have)

---

## Phase 8 — Agent Session Triggering

The "Push to Agent" action needs to start a Claude session on a selected connected machine. Look at how `Dashboard.razor` / `AgentHub` initiates sessions today and reuse that mechanism. This is likely a SignalR hub method call or a direct HTTP call to the agent. If the mechanism isn't already exposed as a service, extract a small `SessionLauncherService.StartSessionAsync(machineId, prompt, workingDirectory)` that wraps whatever the dashboard currently does.

---

## Deferred / Optional

- **Issue transitions from UI** — drag-to-move between columns (requires transition API); can start with just the detail modal's transition dropdown instead.
- **Per-project repo URL mapping** — config list of `{ ProjectKey, RepoUrl }` pairs so "Push to Build" auto-selects the right repo.
- **Jira as build source** — show `Source: PROJ-42` in the Builds table when a build was triggered from an issue.

---

## File Summary

| File | Action |
|------|--------|
| `Persistence/Entities/JiraConfigEntity.cs` | Create |
| `Services/JiraConfigService.cs` | Create |
| `Services/JiraDtos.cs` | Create |
| `Services/JiraService.cs` | Create |
| `Services/JiraIssueCache.cs` | Create |
| `Services/JiraNotifier.cs` | Create |
| `Services/JiraPollingService.cs` | Create |
| `Persistence/Entities/JiraIssueLinkEntity.cs` | Create |
| `Persistence/ClaudeManagerDbContext.cs` | Modify |
| `Persistence/Migrations/AddJiraConfig` | Generate |
| `Persistence/Migrations/AddJiraIssueLinks` | Generate |
| `Components/Pages/Issues.razor` | Create |
| `Components/Pages/SessionDetail.razor` | Modify |
| `Components/Layout/NavMenu.razor` | Modify |
| `Services/SweAfService.cs` | Modify |
| `Program.cs` | Modify |

---

## Implementation Order

1. **Phase 1 + 2 + 3** — `JiraConfigEntity` + `JiraConfigService` + DTOs + `JiraService`. Run the `AddJiraConfig` migration and verify Jira API connection via the settings panel before building anything else.
2. **Phase 4** — DB entity and migration for link tracking (`AddJiraIssueLinks`).
3. **Phase 1b (polling)** — `JiraPollingService` + `JiraIssueCache` + `JiraNotifier`. Verify "On Deck" issues appear automatically.
4. **Phase 5 + 7** — Kanban board UI and nav link.
5. **Phase 6** — Outbound "Review" transition, hooked into `ApproveJobAsync` and `SessionDetail`.
6. **Phase 8** — Agent session triggering (depends on understanding the existing session-start mechanism).
7. **Phase 1b (webhooks)** — Add the webhook endpoint once the Hub has a stable public URL.
