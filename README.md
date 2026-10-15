# AI Assistant Management Platform — Labs 0–5 starter (.NET 9 edition)

Companion code for **Chapter 10** of *AI Agentic .NET Reference*. First vertical: a clinic's virtual
secretary on WhatsApp (simulated), built on .NET 9, Semantic Kernel, Ollama, Qdrant, Redis, RabbitMQ and Postgres.

## Run order
```bash
# Lab 0 — infrastructure
docker compose -f docker/docker-compose.yml up -d
bash scripts/pull-models.sh
bash scripts/verify-infra.sh

# Open AssistantPlatform.sln (Visual Studio 2022 17.12+ / Rider / VS Code C# Dev Kit), or from the CLI:
dotnet build AssistantPlatform.sln

# Lab 1 — dummy business API (http://localhost:5100/scalar)
dotnet run --project src/Clinic.Api

# Lab 5 — index the clinic knowledge base
dotnet run --project src/Knowledge.Ingestion -- --tenant clinic-haddad --path knowledge/clinic-haddad

# Labs 2-5 — the assistant
dotnet run --project src/Channel.Gateway      # http://localhost:5200
dotnet run --project src/Agent.Worker
# then use src/Channel.Gateway/Gateway.http to chat
```

## Layout
| Path | Purpose |
|---|---|
| `docker/` | compose stack + SQL schema/seed (runs on first Postgres start) |
| `prompts/clinic/system.v1.md` | versioned system prompt template |
| `knowledge/clinic-haddad/` | tenant knowledge base (RAG source) |
| `src/Assistant.Contracts` | RabbitMQ message contracts |
| `src/Assistant.Core` | tenants, conversation memory, de-dup, LLM client, embeddings, Qdrant index, chunker |
| `src/Clinic.Api` | dummy clinic management API (doctors, slots, appointments) |
| `src/Channel.Gateway` | WhatsApp webhook, simulator, outbound delivery |
| `src/Agent.Worker` | Semantic Kernel agent, prompts, tools, booking holds |
| `src/Knowledge.Ingestion` | markdown → chunks → embeddings → Qdrant |

Requirements: .NET 9 SDK + Visual Studio 2022 17.12+ / Rider / VS Code.
This edition differs from the .NET 10 one only in `Directory.Build.props` (net9.0) and `Microsoft.AspNetCore.OpenApi` 9.*.
The Microsoft.Extensions.* packages stay on 10.* on purpose: they support net9.0 and match what Semantic Kernel pulls in (avoids NU1605 downgrade errors).
`nuget.config` forces nuget.org for this solution (overrides company Package Source Mapping).

Reset the database: `docker compose -f docker/docker-compose.yml down -v` (deletes all volumes, including pulled models).
# Hussein-ali-AIAssistantPlatform
