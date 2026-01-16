# Shoots Agent Policy (STRICT · REPO-SAFE · RUNNER-ENFORCED)

This document defines **non-negotiable operating rules** for any AI agent (Codex or otherwise) interacting with the **Shoots** repository.

Failure to follow these rules invalidates the agent’s output.

---

## 1. Purpose

The Shoots repository is the **authoritative root** for all Shoots development.

AI agents are permitted to:
- Propose **text-only code changes**
- Produce **git-apply-ready patches**

AI agents are **not permitted** to:
- Execute code
- Run builds or tests
- Guess or simulate results
- Modify repository structure without instruction

All execution, builds, and validation occur **only** via GitHub Actions on the **Windows self-hosted runner**.

---

## 2. Repository Root Resolution (MANDATORY)

Before any operation, the agent **must resolve the repository root** using this exact order:

1. If `$REPO_ROOT` exists → use it
2. Else if `$SHOOTS_REPO_DIR` exists → use it
3. Else resolve via:
   ```bash
   git rev-parse --show-toplevel
   ```

### Hard Stop Rule

If a `.git` directory exists **and** the resolved root does **not** exactly match:
```bash
git rev-parse --show-toplevel
```

→ **Abort immediately with no output.**

### Absolute prohibitions
- Never assume fixed paths
- Never reference `/workspace`, containers, runners, or host OS paths
- Never infer repository layout

---

## 3. Operating Scope

Agents may operate **only** inside the resolved Shoots repository root.

Rules:
- All paths must be **relative to repo root**
- Never read or write outside the repository
- Never traverse parent directories
- Never reference mounted volumes or external environments

---

## 4. Pre-Flight Guardrails (REQUIRED)

Before proposing **any** change, the agent must internally verify:
- Repository root resolved correctly
- `.git` (if present) matches the resolved root

If **any** check fails:
- Produce **no output**
- Make **no changes**
- Do **not** explain, recover, or speculate

---

## 5. Forbidden Paths (ABSOLUTE)

The agent must never read from or modify:

```
.venv/
ext/vcpkg/
.so-cas/objects/
```

Additional rules:
- Always respect `.gitignore`
- Ignored paths are treated as **nonexistent**

---

## 6. File Safety Limits

- Maximum readable or writable file size: **100 MB**
- No chunking, streaming, or partial reads of larger files

For large data:
- Generate **CAS manifests only**
- Commit only `*.manifest.json`
- Never commit raw large artifacts

---

## 7. Patch Output Rules (NON-NEGOTIABLE)

Agent output **must be** a single unified diff, ready for `git apply`.

### Forbidden
- Prose
- Commentary
- Explanations
- Markdown outside the diff

### Required diff format
- `diff --git`
- `---` / `+++` headers
- `@@` hunks
- No timestamps
- No file owners
- No ANSI formatting

### New files must include
```
new file mode 100644
--- /dev/null
+++ b/<relative/path>
```

### Text file requirements
- UTF-8 encoding
- Exactly **one** trailing newline

---

## 8. Patch Location (MANDATORY)

All patches **must** be written to:

```
.ai/patches/patch-YYYY-MM-DD-HH-MM-SS.diff
```

No exceptions.

---

## 9. Branching Rules (When Applicable)

If a branch is required, it **must** be named:

```
codex/{feature}-YYYY-MM-DD-HH-MM-SS
```

No alternative naming schemes are permitted.

---

## 10. Project Layout Constraints (Shoots)

The Shoots repository enforces the following structure:

```
Shoots/
├─ Shoots.Runtime/
├─ Shoots.Builder/
├─ .ai/
├─ tools/
├─ etc/
├─ ui/
```

Rules:
- Do not create new top-level directories unless explicitly instructed
- Do not restructure or relocate submodules
- Do not move files without direct instruction

---

## 11. Execution & Validation Rules (STRICT)

Agents must **never**:
- Claim execution occurred
- Claim builds or tests passed or failed
- Fabricate logs or outcomes
- Say “cannot run in this environment”

### Source of Truth

All validation is determined **only** by GitHub Actions output from the Windows self-hosted runner:

```
[self-hosted, Windows, X64, Shoots]
```

If a build or test fails:
- Surface **only real errors**
- Fail fast
- Do not speculate
- Do not retry implicitly

---

## 12. Validation Workflow (REQUIRED)

When validation is required, the agent must:
1. Push changes or update an existing PR
2. Wait for the Windows runner workflow to complete
3. Treat runner logs as the **sole source of truth**

The agent must never claim results without runner confirmation.

---

## 13. Final Output Constraint

The agent’s final response **must be**:
- A clean unified diff **only**
- No prose
- No summaries
- No commentary

Any violation of this policy **invalidates the output**.
