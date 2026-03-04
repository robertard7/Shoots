#!/usr/bin/env bash
set -euo pipefail

python - <<'PY'
import pathlib
import re
import shlex

allowed_heads = {
    'bash', 'pwsh', 'dotnet', 'python', 'git', 'curl', 'ollama', 'docker', 'setx', 'export'
}

docs = [
    pathlib.Path('docs/startup-walkthrough.md'),
    pathlib.Path('docs/dev/backends.md'),
    pathlib.Path('docs/dev/fixtures.md'),
]

cmds = []
inline_re = re.compile(r'`([^`\n]+)`')
for path in docs:
    if not path.exists():
        continue
    text = path.read_text(encoding='utf-8')
    for match in inline_re.finditer(text):
        candidate = match.group(1).strip()
        if not candidate:
            continue
        if any(candidate.startswith(prefix) for prefix in ('bash ', 'pwsh ', 'dotnet ', 'python ', 'git ', 'curl ', 'docker ', 'ollama ', 'setx ', 'export ')):
            cmds.append((path, candidate))

if not cmds:
    raise SystemExit('verify.docs_examples.no_commands_found')

for path, cmd in cmds:
    parts = shlex.split(cmd)
    if not parts:
        continue
    head = parts[0]
    if head not in allowed_heads:
        raise SystemExit(f'verify.docs_examples.unknown_command_head: {head} ({path})')

print('DOC_EXAMPLES_OK=1')
print(f'DOC_COMMAND_COUNT={len(cmds)}')
PY
