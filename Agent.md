# Shoots Agent Policy (STRICT · REPO-SAFE · RUNNER-ENFORCED)

This document defines **mandatory operating rules** for AI agents (Codex or otherwise) interacting with the **Shoots** repository.

This rewrite preserves intent while removing ambiguities and contradictions that can cause Codex refusal, deadlock, or non-build behavior.

---

## 1. Purpose

The Shoots repository is the **authoritative source of truth** for all Shoots development.

AI agents are allowed to:
- Propose **text-only code changes**
- Produce **git-apply-compatible unified diffs**
- Create or update files **inside the repository** when explicitly requested

AI agents are **not allowed** to:
- Execute code, builds, or tests
- Claim that builds or tests were executed
- Guess, simulate, or invent build/test results
- Modify repository structure unless explicitly instructed

All execution and validation occurs **only** via GitHub Actions on the **Windows self-hosted runner**.

---

## 2. Repository Root Resolution

Before proposing any change, the agent must resolve the repository root using the following priority:

1. `$REPO_ROOT` if defined
2. `$SHOOTS_REPO_DIR` if defined
3. `git rev-parse --show-toplevel`

The resolved path is treated as the **sole repository root** for all operations.

### Consistency Rule

If a `.git` directory exists, the resolved root **must match** `git rev-parse --show-toplevel`.

If this condition cannot be satisfied, the agent must **stop without producing output**.

---

## 3. Operating Scope

The agent may operate **only within the resolved repository root**.

Rules:
- All file paths must be **relative to the repository root**
- No access outside the repository
- No traversal of parent directories
- No assumptions about container paths, runners, or host OS layout

---

## 4. Pre-Flight Guardrails

Before proposing changes, the agent must internally confirm:
- Repository root resolved successfully
- `.git` (if present) aligns with the resolved root

If either check fails, the agent must:
- Produce **no output**
- Make **no changes**

No recovery or explanation is permitted.

---

## 5. Forbidden Paths

The agent must never read from or modify the following paths:

```
.venv/
ext/vcpkg/
.so-cas/objects/
```

Additional rules:
- Respect `.gitignore`
- Ignored paths are treated as nonexistent

---

## 6. File Safety Limits

- Maximum file size for read or write: **100 MB**
- No partial reads, streaming, or chunking of larger files

For large data artifacts:
- Generate **manifest files only** (`*.manifest.json`)
- Do not commit raw large artifacts

---

## 7. Patch Output Rules

When a patch is explicitly requested, output must be:
- A **single unified diff** compatible with `git apply`
- Contain **no prose, commentary, or explanation**

### Required diff format
- `diff --git`
- `---` / `+++` headers
- `@@` hunks
- No timestamps
- No file ownership metadata
- No ANSI or color formatting

### New files must include
```
new file mode 100644
--- /dev/null
+++ b/<relative/path>
```

All text files must:
- Be UTF-8 encoded
- End with exactly one trailing newline

---

## 8. Patch Location

When creating a patch file, it must be written to:

```
.ai/patches/patch-YYYY-MM-DD-HH-MM-SS.diff
```

If patch output is **not explicitly requested**, the agent must not emit diffs.

---

## 9. Branching Rules

If a branch name is required, it must follow this format:

```
codex/<feature>-YYYY-MM-DD-HH-MM-SS
```

If branching is not explicitly requested, the agent must not assume or create branches.

---

## 10. Project Layout Constraints

The Shoots repository enforces this top-level structure:

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
- Do not restructure existing directories
- Do not move files without direct instruction

---

## 11. Execution & Validation

The agent must never:
- Claim that execution occurred
- Claim builds or tests passed or failed
- Fabricate logs or outcomes

The **only** source of truth for validation is GitHub Actions output from:

```
[self-hosted, Windows, X64, Shoots]
```

---

## 12. Validation Workflow

When validation is required:
1. Changes are committed or proposed
2. GitHub Actions runs on the Windows self-hosted runner
3. Runner logs are treated as authoritative

The agent must not infer or restate results unless they appear in runner output.

---

## 13. Final Output Rules

The agent must follow the user’s instruction context:
- If **a patch is requested** → output unified diff only
- If **a document is requested** → output document only
- If **analysis or planning is requested** → no diffs

Any output that violates these rules is invalid.

