# AI Assistant Management Platform — Labs 0–5 starter (.NET 9 edition)

Companion code for **Chapter 10** of *AI Agentic .NET Reference*. First vertical: a clinic's virtual
secretary on WhatsApp (simulated), built on .NET 9, Semantic Kernel, Ollama, Qdrant, Redis, RabbitMQ and Postgres.

## Run order (workflow)
Start the platform **bottom-up**: infrastructure → business system → knowledge base → assistant.
Full explanation of every step: [`documents/lab1-5-workflow.md`](documents/lab1-5-workflow.md).

| # | Command | What it does | Role |
|---|---|---|---|
| 1 | `docker compose -f docker/docker-compose.yml up -d` | Starts Postgres, Redis, RabbitMQ, Qdrant, Ollama, Phoenix; Postgres seeds 2 clinics + 4 doctors on first start | Infrastructure backbone |
| 2 | `bash scripts/pull-models.sh` | Downloads `qwen2.5:7b`, `qwen2.5:1.5b`, `bge-m3` (~6 GB, once) | The LLM "brain" + embeddings |
| 3 | `bash scripts/verify-infra.sh` | 8 health checks incl. a real chat completion and an Arabic embedding | Lab 0 acceptance test |
| 4 | `dotnet build AssistantPlatform.sln` (or open the .sln in Visual Studio 2022 17.12+ / Rider / VS Code) | Restores NuGet (nuget.org via `nuget.config`) and compiles 7 projects | Proves the code compiles |
| 5 | `dotnet run --project src/Clinic.Api` *(keep open)* | Dummy clinic API on http://localhost:5100/scalar | Business system + safety boundary (the AI only acts through it) |
| 6 | `dotnet run --project src/Knowledge.Ingestion -- --tenant clinic-haddad --path knowledge/clinic-haddad` | Markdown → chunks → `bge-m3` vectors → Qdrant (tenant-tagged); runs and exits | Indexing half of RAG |
| 7 | `dotnet run --project src/Channel.Gateway` *(keep open)* | Webhook + simulator on http://localhost:5200; validate, de-dup, publish to RabbitMQ, deliver replies | Front door |
| 8 | `dotnet run --project src/Agent.Worker` *(keep open)* | Consumes RabbitMQ; system prompt + history + ReAct tool calls (Clinic.Api, Qdrant); publishes the reply. Diagnostics API on http://localhost:5300/diag | The AI brain |
| 9 | Run requests in `src/Channel.Gateway/Gateway.http` | Send a patient message, poll the conversation, reset it | WhatsApp simulator |
| 10 | `GET http://localhost:5300/diag/all`, then `POST http://localhost:5300/diag/flow` | Tests every dependency, then books → confirms → checks Postgres → cleans up through the real pipeline | Automated acceptance test |

```bash
# Lab 0 — infrastructure
docker compose -f docker/docker-compose.yml up -d
bash scripts/pull-models.sh
bash scripts/verify-infra.sh

# Build — or open AssistantPlatform.sln in Visual Studio 2022 17.12+ / Rider / VS Code
dotnet build AssistantPlatform.sln

# Lab 1 — dummy business API (http://localhost:5100/scalar)
dotnet run --project src/Clinic.Api

# Lab 5 — index the clinic knowledge base (one-shot; re-run after editing knowledge/)
dotnet run --project src/Knowledge.Ingestion -- --tenant clinic-haddad --path knowledge/clinic-haddad

# Labs 2-5 — the assistant
dotnet run --project src/Channel.Gateway      # http://localhost:5200
dotnet run --project src/Agent.Worker
# then use src/Channel.Gateway/Gateway.http to chat
```

**One message, end to end:**
```
Gateway.http → Channel.Gateway → RabbitMQ → Agent.Worker ⇄ Ollama / Clinic.Api / Qdrant / Redis
            Channel.Gateway ← RabbitMQ ← reply
```
**Visual Studio tip:** Solution → Configure Startup Projects → Multiple → start Clinic.Api, Channel.Gateway, Agent.Worker.

## Logs & diagnostics
- **Logs:** every service writes to `<repo>/logs/` (git-ignored): `all-`, `gateway-`, `worker-`, `clinic-`, `redis-`, `llm-YYYYMMDD.log`.
  Settings: `LabLogging:Directory`, `LabLogging:MinimumLevel`, `LabLogging:RetainDays` in each appsettings.json.
- **Diagnostics API** (Agent.Worker, port 5300): `/diag/all`, `/diag/redis`, `/diag/postgres`, `/diag/rabbitmq`, `/diag/clinic`,
  `/diag/qdrant`, `/diag/llm`, `/diag/llm/tools`, `/diag/state/{tenant}/{phone}`, `POST /diag/agent`, `POST /diag/flow`.
  200 = all checks passed, 503 = see `checks[].error` / `hint`. Postman folder **6 — Worker diagnostics**.

## Layout
| Path | Purpose |
|---|---|
| `documents/lab1-5-workflow.md` | step-by-step workflow: what each run step does and its role |
| `docker/` | compose stack + SQL schema/seed (runs on first Postgres start) |
| `prompts/clinic/system.v1.md` | versioned system prompt template |
| `knowledge/clinic-haddad/` | tenant knowledge base (RAG source) |
| `src/Assistant.Contracts` | RabbitMQ message contracts |
| `src/Assistant.Logging` | Serilog setup shared by all hosts: combined + per-service + Redis/LLM/Clinic log files |
| `src/Assistant.Core` | tenants, conversation memory, de-dup, LLM client, embeddings, Qdrant index, chunker |
| `src/Clinic.Api` | dummy clinic management API (doctors, slots, appointments) |
| `src/Channel.Gateway` | WhatsApp webhook, simulator, outbound delivery |
| `src/Agent.Worker` | Semantic Kernel agent, prompts, tools, booking holds, diagnostics API (`Diagnostics/`) |
| `src/Knowledge.Ingestion` | markdown → chunks → embeddings → Qdrant |

Requirements: .NET 10 SDK + Visual Studio 2022 17.12+ / Rider / VS Code (Visual Studio 2022 cannot target net10.0).
`nuget.config` forces nuget.org for this solution (overrides company Package Source Mapping).

Reset the database: `docker compose -f docker/docker-compose.yml down -v` (deletes all volumes, including pulled models).
