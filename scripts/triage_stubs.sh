#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

input="artifacts/stubs/stubs.ndjson"
out_dir="artifacts/stubs"
mkdir -p "$out_dir"
triage="$out_dir/triage.md"

if [[ ! -f "$input" ]]; then
  echo "missing input: $input" >&2
  exit 2
fi

python - <<'PY'
import json
import pathlib
from collections import defaultdict

input_path = pathlib.Path('artifacts/stubs/stubs.ndjson')
out_dir = pathlib.Path('artifacts/stubs')
rows = []
for line in input_path.read_text(encoding='utf-8').splitlines():
    if not line.strip():
        continue
    rows.append(json.loads(line))

def bucket_for(row):
    path = row['path']
    code = row['code']
    match = row.get('match','')

    if path.startswith(('docs/','README.md','.github/')):
        return '4) False positive'

    if code in {'NOT_IMPLEMENTED_EXCEPTION','NOT_SUPPORTED_EXCEPTION','THROW_EXCEPTION_TODO','THROW_EXCEPTION_STUB'}:
        if path.startswith(('ui/Shoots.Ui/','src/Runtime/Shoots.Runtime.Runner/','src/Builder/')):
            return '1) Must implement now'
        return '2) Implement soon'

    if code in {'TODO_COLON','FIXME_COLON','STUB_COMMENT_LINE','STUB_COMMENT_BLOCK'}:
        if path.startswith(('ui/Shoots.Ui/','src/Runtime/Shoots.Runtime.Runner/','src/Builder/')):
            return '2) Implement soon'
        return '4) False positive'

    if code == 'RETURN_NULL':
        if path.startswith(('ui/Shoots.Ui/','src/Runtime/Shoots.Runtime.Runner/','src/Builder/')):
            return '2) Implement soon'
        return '3) Intentional not-supported'

    if code in {'CSHARP_ERROR_DIRECTIVE','CSHARP_WARNING_DIRECTIVE'}:
        return '3) Intentional not-supported'

    return '4) False positive'

bucket_order = [
    '1) Must implement now',
    '2) Implement soon',
    '3) Intentional not-supported',
    '4) False positive',
]

for row in rows:
    row['bucket'] = bucket_for(row)

rows.sort(key=lambda r: (bucket_order.index(r['bucket']), r['path'], int(r['line']), r['code'], r.get('match','')))

for bucket in bucket_order:
    bucket_file = out_dir / f"bucket-{bucket[0]}.ndjson"
    with bucket_file.open('w', encoding='utf-8', newline='\n') as f:
        for row in rows:
            if row['bucket'] == bucket:
                f.write(json.dumps(row, sort_keys=True, separators=(',', ':')))
                f.write('\n')

counts = defaultdict(int)
for row in rows:
    counts[row['bucket']] += 1

lines = ['# Stub Triage', '']
for bucket in bucket_order:
    lines.append(f"## {bucket}")
    lines.append(f"Count: {counts[bucket]}")
    lines.append('')
    for row in rows:
        if row['bucket'] != bucket:
            continue
        lines.append(f"- `{row['path']}:{row['line']}` `{row['code']}` `{row.get('match','')}`")
    lines.append('')

(out_dir / 'triage.md').write_text('\n'.join(lines), encoding='utf-8', newline='\n')
print('triage written:', (out_dir / 'triage.md').as_posix())
print('must-implement count:', counts['1) Must implement now'])
PY
