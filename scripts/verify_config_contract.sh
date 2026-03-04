#!/usr/bin/env bash
set -euo pipefail

config_files=(
  "global.json"
  "etc/tools.catalog.json"
)

for cfg in "${config_files[@]}"; do
  [[ -f "$cfg" ]] || { echo "verify.config_contract.missing: $cfg" >&2; exit 64; }
done

python - "${config_files[@]}" <<'PY'
import json
import pathlib
import re
import sys

stamp = re.compile(r'(timestamp|created_at|updated_at|time)$', re.IGNORECASE)
envexp = re.compile(r'\$\{[^}]+\}|%[A-Za-z_][A-Za-z0-9_]*%')

validated = []
for rel in sys.argv[1:]:
    path = pathlib.Path(rel)
    raw = path.read_text(encoding='utf-8')
    if envexp.search(raw):
        raise SystemExit(f'verify.config_contract.env_expansion_found: {path}')
    obj = json.loads(raw)

    stack = [([], obj)]
    while stack:
        pfx, node = stack.pop()
        if isinstance(node, dict):
            for k, v in node.items():
                if stamp.search(str(k)):
                    dotted = '.'.join(pfx + [str(k)])
                    raise SystemExit(f'verify.config_contract.dynamic_timestamp_key: {path}:{dotted}')
                stack.append((pfx + [str(k)], v))
        elif isinstance(node, list):
            for i, v in enumerate(node):
                stack.append((pfx + [str(i)], v))

    canonical = json.dumps(obj, sort_keys=True, separators=(',', ':'))
    if not canonical:
        raise SystemExit(f'verify.config_contract.empty_canonical: {path}')
    validated.append(str(path))

print('CONFIG_CONTRACT_OK=1')
print(f'CONFIG_FILES={len(validated)}')
PY
