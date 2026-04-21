# Backlog

## P0 — Unblock current work (bugs + foundational)

* \[ ] **bug:** build is not marked as complete, teardown not initiated for completed builds when Hub is running (has to be restarted)
* \[ ] **bug:** add all SWE-AF agent types to Foundry config, so we stop trying to use Haiku
* \[ ] **bug:** fix the error on the Build details page
* \[ ] **bug:** PR links are not showing up in the Builds list or details
* \[ ] **bug:** Build logs still aren't showing up in Build details

  * confirmed: observability webhook is configured on AgentField host
  * possible cause: Hub host isn't exposed on the LAN?
* \[ ] **bug:** config validation on triggering a build

  * valid vLLM/proxy deployments
  * available ports
* \[ ] **chore:** rename repo and project to "Foundry" *(do early — touches everything, cheaper now than later)*

## P1 — Foundry Builds core improvements

* \[ ] **feat:** update Foundry builds to be able to choose backend/inference engine per build
* \[ ] **feat:** figure out fallback for failed builds (how to better get those logs, agent executions, etc)
* \[ ] **feat:** add saved common repositories to the Foundry Builds/Config
* \[ ] **feat:** explore always-on options for programmatically-triggered builds (Hub has POST /foundry/build)
* \[ ] **feat:** add ability to submit PR feedback to agent swarm

  * related: need to keep containers alive until PR is accepted and merged
* \[ ] **chore:** add build logs as separate table
* \[ ] **chore:** get rid of Agents section in Foundry, related code/services

## P1 — LLM deployments

*Aligns with Phase 1 vLLM work in progress.*

* \[ ] **feat:** saved LLM deployment groups for 1-click deploys
* \[ ] **feat:** copy LLM deployment
* \[ ] **chore:** move LLM deployments under their corresponding host/proxy on LLM Servers page
* \[ ] **feat:** update LLM deployment logs to auto-scroll to bottom when new logs are received (only if log container is already at bottom)

## P2 — Integrations

* \[ ] **feat:** GitHub integration for PR feedback
* \[ ] **feat:** Slack integration for new request polling
* \[ ] **feat:** Claude Code config for agent host machine (skills, mcps, etc)
* \[ ] **feat:** update Wiki to files/filesystem, store in git, known bugs/issues, connect to ClaudeManager.McpServer

## P2 — UX / polish

* \[ ] **feat:** expand content container to use all available screen space
* \[ ] **feat:** right-side side panel for agent chat
* \[ ] **feat:** filters for Build/Container logs

## Rationale

1. **Bugs first** — several block observability of the Builds feature itself (log visibility, PR links, error page). Hard to land new build features while those are broken.
2. **Rename early** — renaming touches repo/project/namespaces; every day of delay adds churn to unrelated PRs.
3. **Builds before integrations** — GitHub/Slack integrations lean on PR-feedback and swarm-liveness work, so sequence the core Foundry Builds features first.
4. **LLM deployment group stays cohesive** — Phase 1 vLLM work is already in progress.

