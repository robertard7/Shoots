# Backend Setup for Local/Codex Smoke Checks

This guide provides copy/paste commands for running and validating Ollama and Qdrant backends used by Shoots.

## 1) Run Ollama locally (native install)

```bash
ollama serve
```

In another shell:

```bash
export OLLAMA_HOST="http://localhost:11434"
curl -fsS "$OLLAMA_HOST/api/tags"
```

Expected: JSON payload containing a top-level `models` array.

## 2) Run Ollama in Docker

```bash
docker run --rm -d --name ollama -p 11434:11434 ollama/ollama
```

Then:

```bash
export OLLAMA_HOST="http://localhost:11434"
# On Docker Desktop (Windows/macOS), this host alias is also common:
# export OLLAMA_HOST="http://host.docker.internal:11434"
curl -fsS "$OLLAMA_HOST/api/tags"
```

Expected: JSON payload containing a top-level `models` array.

## 3) Optional: Run Qdrant in Docker

```bash
docker run --rm -d --name qdrant -p 6333:6333 qdrant/qdrant
export QDRANT_URL="http://localhost:6333"
curl -fsS "$QDRANT_URL/healthz"
```

Expected: healthy response body (endpoint reachable and HTTP 200).

## 4) Export environment variables for Shoots scripts

```bash
export OLLAMA_HOST="http://localhost:11434"
export QDRANT_URL="http://localhost:6333"
```

## 5) Run smoke check

```bash
bash scripts/smoke_backends.sh --ollama "$OLLAMA_HOST" --qdrant "$QDRANT_URL" --timeout-secs 3
```

Or skip Qdrant:

```bash
bash scripts/smoke_backends.sh --ollama "$OLLAMA_HOST" --skip-qdrant
```

Expected success output:
- `Ollama probe passed.`
- `Qdrant probe passed.` (unless skipped)
- `Backend smoke checks passed.`
