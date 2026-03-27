# LLM Server Management — Implementation Plan

A vLLM-based LLM server management feature for Claude Manager Hub.
Enables configuring, starting, and stopping GPU-hosted vLLM instances from the dashboard,
with multi-GPU support, a remote nginx proxy for resilience, and HuggingFace model selection.

---

## Architecture

```
Hub (management plane)
  │
  ├── GpuHostService        — CRUD for registered GPU hosts (DB-stored)
  ├── HubSecretService      — Key/value store for Hub-wide secrets (HF token, etc.)
  ├── LlmGpuDiscoveryService — SSH → nvidia-smi → GPU inventory
  ├── LlmInstanceService    — Start/stop vLLM Docker containers per deployment
  ├── NginxProxyService     — Generate upstream config, SSH-push, reload nginx
  └── LlmDeploymentService  — Orchestrate deployments across hosts

GPU Host (1..N machines with NVIDIA GPUs)
  ├── Docker daemon         — runs vllm/vllm-openai containers
  └── nginx                — reverse proxy; survives Hub restarts
```

**Key design decisions:**
- **Docker** containers for vLLM (not bare-metal) — cleaner lifecycle management
- **Multi-host** — each GPU host is declared explicitly in the UI
- **Remote nginx proxy** on the GPU host — independent of Hub; SWE-AF survives Hub restarts/failures
- **No LiteLLM** — recently compromised (2026); nginx upstream is the aggregation layer
- **Model download progress** — surfaced via Docker log streaming from the container during pull

---

## Data Model

### GpuHostEntity (Table: GpuHosts)
| Column | Type | Notes |
|--------|------|-------|
| Id | long PK | auto-increment |
| HostId | string(100) | unique human-readable slug |
| DisplayName | string(200) | label in UI |
| Host | string(500) | hostname or IP; "localhost" → Process.Start |
| SshPort | int | default 22 |
| SshUser | string(100) | SSH username |
| SshKeyPath | string(500) | path to private key; ~ expanded |
| SshPassword | string(500) | alternative to key auth |
| AddedAt | DateTimeOffset | |

### HubSecretEntity (Table: HubSecrets)
| Column | Type | Notes |
|--------|------|-------|
| Key | string(100) PK | e.g. "HuggingFaceToken" |
| Value | string(2000)? | nullable (unset = not configured) |

### LlmDeploymentEntity (Table: LlmDeployments) — Phase 3
| Column | Type | Notes |
|--------|------|-------|
| Id | long PK | auto-increment |
| DeploymentId | string(100) | unique slug |
| HostId | string(100) | FK → GpuHosts.HostId |
| ModelId | string(500) | e.g. "meta-llama/Llama-3.1-8B-Instruct" |
| GpuIndices | string(200) | comma-separated GPU indices |
| HostPort | int | container port on the GPU host |
| Status | int (enum) | Stopped/Starting/Running/Error |
| HfTokenOverride | string(500)? | per-deployment HF token override |
| ExtraArgs | string(1000)? | extra vLLM CLI args |
| ContainerId | string(100)? | Docker container ID when running |
| CreatedAt | DateTimeOffset | |
| StartedAt | DateTimeOffset? | |

---

## HuggingFace Token

Stored as `HubSecretEntity` with key `"HuggingFaceToken"`. Surfaced in the UI as a
password input in the Global Settings panel on `/llm`. Each deployment can optionally
override it with its own token (for org-scoped tokens or different HF accounts).

---

## Phases

### Phase 1 — GPU Host Registry + `/llm` Dashboard Page ✅ IN PROGRESS

**Entities:** `GpuHostEntity`, `HubSecretEntity`
**Migration:** `20260327100000_AddLlmHosting`
**Services:** `GpuHostService`, `HubSecretService`, `LlmGpuDiscoveryService`
**Page:** `/llm` — Global Settings panel + GPU Hosts panel (add/remove/inspect)

GPU discovery runs `nvidia-smi --query-gpu=index,name,memory.total,memory.free --format=csv,noheader,nounits`
via SSH (or locally) and parses the output into `GpuInfo` records.

### Phase 2 — vLLM Container Lifecycle

**Entity:** `LlmDeploymentEntity`
**Migration:** `AddLlmDeployments`
**Services:**
- `LlmInstanceService` — SSH → `docker run -d --gpus "device=N" -p PORT:8000 \
  -v ~/.cache/huggingface:/root/.cache/huggingface \
  -e HUGGING_FACE_HUB_TOKEN=... vllm/vllm-openai:latest --model {modelId} ...`
- Docker log streaming for model download progress

**VRAM estimation heuristic** (shown before deploying):
- fp16: `params × 2 × 1.25 GB`
- int8: `params × 1.0 × 1.25 GB`
- AWQ/GPTQ: `params × 0.5 × 1.25 GB`

**UI additions to `/llm`:**
- "New Deployment" button → form: host, model, GPU selector (checkboxes from discovered GPUs),
  port, quantization, extra args, per-deployment HF token override
- Deployment cards per host showing status badge, model, GPUs, VRAM estimate, start/stop/delete
- Log streaming panel for active deployments (model download progress)

### Phase 3 — Remote nginx Proxy

**Service:** `NginxProxyService`
- Generates `/etc/nginx/conf.d/vllm-upstream.conf` from active deployments on a host
- SSHes config to the host, runs `nginx -t && nginx -s reload`
- Called automatically on deployment start/stop

**Upstream pattern:**
```nginx
upstream vllm_pool {
    server 127.0.0.1:8001;
    server 127.0.0.1:8002;
    keepalive 32;
}
server {
    listen 8080;
    location / {
        proxy_pass http://vllm_pool;
        proxy_read_timeout 300s;
    }
}
```

**UI additions:**
- Nginx status badge per host (installed / not installed / config out of sync)
- "Apply Config" button to push and reload nginx
- "View Config" button to show generated nginx config

### Phase 4 — Hub Integration (ClaudeBaseUrl / SweAf Models)

- Agent `ClaudeBaseUrl` can point to `http://<gpu-host>:8080/v1` (the nginx proxy)
- SWE-AF `Models.Default` / `Models.Coder` can point to the proxy
- UI: "Copy endpoint" button on each GPU host's proxy entry
- Optional: "Set as default" to automatically populate Agent config URLs

### Phase 5 — Model Browser + HuggingFace Search

- Search HuggingFace Hub API for text-generation models
- Filter by VRAM fit (based on GPU discovery + VRAM estimate)
- "Deploy" button directly from search results

### Phase 6 — YARP Aggregation Proxy (Optional)

An optional in-Hub YARP reverse proxy that exposes a single unified endpoint
`https://<hub>/api/llm/v1` and load-balances across all GPU host nginx proxies.
This is purely a convenience feature — the per-host nginx proxies continue to work
independently. Enable/disable from the `/llm` settings panel.

---

## Configuration Reference (appsettings.json additions)

No new appsettings.json keys required — all GPU host configuration is stored in the DB.
The HuggingFace token is stored in HubSecrets (UI-managed), not in appsettings.json.
