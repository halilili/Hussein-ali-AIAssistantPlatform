#!/usr/bin/env bash
# Downloads the local models into the ollama volume (one time, ~6 GB total).
set -euo pipefail
C="docker compose -f docker/docker-compose.yml exec ollama ollama"
$C pull qwen2.5:7b      # main assistant model: tool calling + good Arabic   (~4.7 GB)
$C pull qwen2.5:1.5b    # small/cheap model for routing & simple turns       (~1 GB)
$C pull bge-m3          # multilingual embeddings, 1024 dims                  (~1.2 GB)
$C list
