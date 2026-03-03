#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

out_dir="artifacts/stubs"
mkdir -p "$out_dir"
ndjson="$out_dir/stubs.ndjson"
txt="$out_dir/stubs.txt"

python - <<'PY'
import json
import pathlib
import subprocess

patterns = [
    ("TODO_COLON", "TODO:"),
    ("FIXME_COLON", "FIXME:"),
    ("NOT_IMPLEMENTED_EXCEPTION", "NotImplementedException"),
    ("NOT_SUPPORTED_EXCEPTION", "NotSupportedException"),
    ("THROW_EXCEPTION_TODO", r'throw new Exception\\("TODO'),
    ("THROW_EXCEPTION_STUB", r'throw new Exception\\("stub'),
    ("RETURN_NULL", "return null;"),
    ("STUB_COMMENT_LINE", "// stub"),
    ("STUB_COMMENT_BLOCK", "/* stub */"),
    ("CSHARP_ERROR_DIRECTIVE", "#error"),
    ("CSHARP_WARNING_DIRECTIVE", "#warning"),
]

exclude_args = [
    "-g", "!artifacts/**",
    "-g", "!.git/**",
    "-g", "!.venv/**",
    "-g", "!ext/vcpkg/**",
    "-g", "!.so-cas/objects/**",
]

repo = pathlib.Path('.').resolve()
rows = []
for code, pattern in patterns:
    cmd = [
        "rg",
        "--line-number",
        "--no-heading",
        "--color", "never",
        "--fixed-strings",
        *exclude_args,
        pattern,
        ".",
    ]
    proc = subprocess.run(cmd, cwd=repo, text=True, capture_output=True)
    if proc.returncode not in (0, 1):
        raise SystemExit(proc.stderr.strip() or f"rg failed for {code}")

    for line in proc.stdout.splitlines():
        path_part, line_part, text_part = line.split(':', 2)
        if path_part.startswith('./'):
            path_part = path_part[2:]
        rows.append({
            "path": path_part,
            "line": int(line_part),
            "code": code,
            "match": text_part.rstrip('\n'),
        })

rows.sort(key=lambda r: (r["path"], r["line"], r["code"], r["match"]))
out_dir = repo / "artifacts" / "stubs"
out_dir.mkdir(parents=True, exist_ok=True)

with (out_dir / "stubs.ndjson").open("w", encoding="utf-8", newline="\n") as f:
    for row in rows:
        f.write(json.dumps(row, sort_keys=True, separators=(',', ':')))
        f.write("\n")

with (out_dir / "stubs.txt").open("w", encoding="utf-8", newline="\n") as f:
    for row in rows:
        f.write(f"{row['path']}:{row['line']}:{row['code']}:{row['match']}\n")

print(f"wrote {len(rows)} stub markers")
print(str((out_dir / 'stubs.ndjson').as_posix()))
print(str((out_dir / 'stubs.txt').as_posix()))
PY
