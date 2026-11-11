# Labs 0–5 Workflow — What Each Step Does and Its Role

Companion to **Chapter 10** of *AI Agentic .NET Reference*. The steps start the platform **bottom-up**:
infrastructure first, then the business system, then the knowledge base, then the assistant itself —
the same order a patient message travels through the system.

---

## Lab 0 — Infrastructure

### Step 1 — Start the containers
```bash
docker compose -f docker/docker-compose.yml up -d
```
- **What it does:** starts 6 containers in the background (`-d` = detached).
- **Role:** the platform's backbone — nothing else works without it.

| Container | Role |
|---|---|
| **postgres** | Stores tenants (clinics) and the dummy clinic system: doctors, schedules, appointments. On its **first** start it runs `docker/sql/*.sql` → creates the schema and seeds 2 clinics and 4 doctors. |
| **redis** | Short-term memory: chat history per patient, duplicate-message detection, 5-minute slot holds. |
| **rabbitmq** | Message bus between the Gateway and the Worker (management UI: http://localhost:15672, `lab` / `lab_pwd`). |
| **qdrant** | Vector database — stores the clinic knowledge base for RAG (dashboard: http://localhost:6333/dashboard). |
| **ollama** | Runs the LLM and the embedding model locally behind an OpenAI-compatible API (port 11434). |
| **phoenix** | Observability UI — every prompt, tool call and latency (http://localhost:6006). |

### Step 2 — Download the models
```bash
bash scripts/pull-models.sh
```
- **What it does:** downloads 3 models into the Ollama container (~6 GB, one time only — they stay in the `ollama_models` volume).
- **Role:** gives the platform its "brain":
  - `qwen2.5:7b` — the main assistant: reasons and calls tools.
  - `qwen2.5:1.5b` — small, fast model for routing (used from Lab 14).
  - `bge-m3` — embedding model: turns text into vectors for RAG; multilingual, so an Arabic question finds an English answer.

### Step 3 — Verify the infrastructure
```bash
bash scripts/verify-infra.sh
```
- **What it does:** 8 health checks — Postgres has 2 tenants, Redis replies, RabbitMQ is up, Qdrant is up,
  Ollama has the models, Phoenix is up, a real chat completion works, an Arabic text embeds.
- **Role:** Lab 0's acceptance test. When all lines are OK, any later problem is in the code, not the environment.

---

## Build

### Step 4 — Build the solution
```bash
dotnet build AssistantPlatform.sln
```
Or open `AssistantPlatform.sln` in the IDE
(.NET 9 edition → Visual Studio 2022 17.12+; .NET 10 edition → Visual Studio 2026; Rider / VS Code work for both).
- **What it does:** restores NuGet packages (from nuget.org, forced by `nuget.config`) and compiles the 6 projects.
- **Role:** proves the code compiles before anything runs. `Assistant.Contracts` and `Assistant.Core` are libraries —
  they never run on their own; they are compiled into the services below.

---

## Lab 1 — The Business System

### Step 5 — Start the dummy clinic API *(keep this terminal open)*
```bash
dotnet run --project src/Clinic.Api
```
- **What it does:** starts the clinic management API on port 5100. Try it in the browser at http://localhost:5100/scalar
  or with `src/Clinic.Api/Clinic.Api.http`.
- **Role:** stands in for the clinic's real system. It owns the business rules: no double booking, only slots on the
  doctor's schedule, at most 2 upcoming appointments per patient, cancellation at least 2 hours before.
- **Why it matters:** the AI never touches the database directly. It can only call this API — the API is the safety boundary.

---

## Lab 5 — Load the Knowledge Base *(run once, and again after editing the documents)*

### Step 6 — Index the clinic knowledge base
```bash
dotnet run --project src/Knowledge.Ingestion -- --tenant clinic-haddad --path knowledge/clinic-haddad
```
- **What it does:** reads the 4 markdown files (fees & insurance, location & hours, test preparation, policies), then:
  1. splits them into chunks by heading,
  2. embeds each chunk with `bge-m3` (a list of 1024 numbers),
  3. stores the chunks in Qdrant tagged with `tenant_id = clinic-haddad`.
- **Role:** the **indexing** half of RAG. It is a one-shot console app: it runs, prints how many chunks it stored, and exits.
  Re-running it replaces the old chunks instead of duplicating them.
- **Tip:** add `--ask "do you accept insurance?"` to see which chunks Qdrant returns — test retrieval before involving the assistant.
- **Needs:** Qdrant and Ollama only (not Clinic.Api).

---

## Labs 2–5 — The Assistant *(keep both terminals open)*

### Step 7 — Start the Channel Gateway (port 5200)
```bash
dotnet run --project src/Channel.Gateway
```
- **What it does:** starts the service that faces the outside world.
- **Role:** the platform's front door.
  - **Inbound:** receives the message (simulator now, real WhatsApp in Lab 17), validates it, ignores duplicates,
    publishes it to RabbitMQ, and replies "OK" immediately.
  - **Outbound:** takes the assistant's reply from RabbitMQ and delivers it to the patient.
- **Why it is separate:** WhatsApp needs a reply within seconds; the AI can take 5–30 seconds. The queue absorbs the gap.

### Step 8 — Start the Agent Worker
```bash
dotnet run --project src/Agent.Worker
```
- **What it does:** a background service listening on RabbitMQ (no web port).
- **Role:** the AI brain. For each message it:
  1. loads the clinic's profile,
  2. builds the **system prompt** (rules + doctors + date table),
  3. adds recent **chat history** from Redis,
  4. runs the **ReAct loop** with Semantic Kernel — the model decides which tools to call:
     `find_available_slots` / `propose_booking` / `confirm_booking` → Clinic.Api, `search_clinic_knowledge` → Qdrant,
  5. saves the conversation in Redis and publishes the reply back to RabbitMQ.
- **Needs:** Clinic.Api running (Step 5) and the knowledge base loaded (Step 6).

### Step 9 — Chat with the assistant
Open `src/Channel.Gateway/Gateway.http` in the IDE and run the requests:
- `POST /simulator/clinic-haddad/messages` → send a patient message, e.g. *"baddi maw3ad 3end Dr Haddad bukra"*.
- `GET /simulator/clinic-haddad/conversations/{phone}` → read the conversation (wait a few seconds — CPU inference is slow).
- `DELETE /simulator/clinic-haddad/conversations/{phone}` → reset the conversation.

- **Role:** a WhatsApp simulator — the exact same pipeline as real WhatsApp, without a Meta account.

---

## The Full Journey of One Message

```
Gateway.http → Channel.Gateway → RabbitMQ → Agent.Worker ⇄ Ollama (thinks)
                                                  ⇄ Clinic.Api → Postgres (slots, booking)
                                                  ⇄ Qdrant (knowledge)
                                                  ⇄ Redis (memory, holds)
            Channel.Gateway ← RabbitMQ ← reply
```

## Running Processes Summary

| Process | Type | Port | Keep running? |
|---|---|---|---|
| Docker containers | infrastructure | 5432, 6379, 5672/15672, 6333/6334, 11434, 6006 | Yes |
| Clinic.Api | web API | 5100 | Yes |
| Channel.Gateway | web API | 5200 | Yes |
| Agent.Worker | background worker | — | Yes |
| Knowledge.Ingestion | console (one-shot) | — | No — runs and exits |

**Visual Studio tip:** right-click the solution → **Configure Startup Projects…** → **Multiple startup projects** →
set Clinic.Api, Channel.Gateway and Agent.Worker to **Start**. One F5 launches all three.

## Reset

| What | How |
|---|---|
| One conversation | `DELETE /simulator/clinic-haddad/conversations/{phone}` |
| Knowledge base of a tenant | Re-run Step 6 (replaces chunks), or delete `kb_chunks` in the Qdrant dashboard |
| Everything (DB, Redis, Qdrant, **models**) | `docker compose -f docker/docker-compose.yml down -v`, then start again from Step 1 |
