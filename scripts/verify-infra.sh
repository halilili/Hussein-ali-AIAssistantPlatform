#!/usr/bin/env bash
# Lab 0 acceptance: every dependency answers.
set -uo pipefail
ok()   { printf "  \033[32mOK\033[0m   %s\n" "$1"; }
fail() { printf "  \033[31mFAIL\033[0m %s\n" "$1"; }
D="docker compose -f docker/docker-compose.yml"

$D exec -T postgres psql -U lab -d assistant -tAc "select count(*) from platform.tenants" | grep -q 2 && ok "Postgres (2 tenants seeded)" || fail "Postgres"
$D exec -T redis redis-cli ping | grep -q PONG && ok "Redis" || fail "Redis"
curl -sf -u lab:lab_pwd http://localhost:15672/api/overview > /dev/null && ok "RabbitMQ management" || fail "RabbitMQ"
curl -sf http://localhost:6333/collections > /dev/null && ok "Qdrant" || fail "Qdrant"
curl -sf http://localhost:11434/v1/models | grep -q qwen2.5 && ok "Ollama (models pulled)" || fail "Ollama / models"
curl -sf http://localhost:6006 > /dev/null && ok "Phoenix UI" || fail "Phoenix"

# One real chat completion through the OpenAI-compatible API (first call loads the model: 10-60 s on CPU)
curl -sf http://localhost:11434/v1/chat/completions -H "Content-Type: application/json" \
  -d '{"model":"qwen2.5:7b","messages":[{"role":"user","content":"Reply with the single word: ready"}]}' \
  | grep -qi ready && ok "LLM chat completion" || fail "LLM chat completion"

curl -sf http://localhost:11434/v1/embeddings -H "Content-Type: application/json" \
  -d '{"model":"bge-m3","input":"مرحبا"}' | grep -q embedding && ok "Embeddings (bge-m3, Arabic input)" || fail "Embeddings"
