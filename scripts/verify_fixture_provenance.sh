#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

fail() {
  echo "$1: $2" >&2
  exit 1
}

manifest="etc/fixtures/retrieval_golden/expected/manifest.json"
pack="etc/fixtures/retrieval_golden/expected/context_pack_first30.txt"
hits="etc/fixtures/retrieval_golden/expected/top_hits.ndjson"
stats="etc/fixtures/retrieval_golden/expected/stats.json"
query="etc/fixtures/retrieval_golden/query.json"

[[ -f "$manifest" && -f "$pack" && -f "$hits" && -f "$stats" && -f "$query" ]] || fail "verify.fixture_provenance.missing" "required retrieval golden files missing"

python - "$manifest" "$pack" "$hits" "$stats" "$query" <<'PY'
import hashlib, json, pathlib, re, sys
m_path=pathlib.Path(sys.argv[1])
pack=pathlib.Path(sys.argv[2])
hits=pathlib.Path(sys.argv[3])
stats=pathlib.Path(sys.argv[4])
query=pathlib.Path(sys.argv[5])
compact=lambda obj: json.dumps(obj, sort_keys=True, separators=(',',':')).encode('utf-8')
sha=lambda b: hashlib.sha256(b).hexdigest()
m=json.loads(m_path.read_text())
required=['version','queryHash','retrievalHash','contextPackHash','hitsHash','statsHash','createdUtc','commitSha']
missing=[k for k in required if k not in m]
if missing:
    print('verify.fixture_provenance.invalid: missing '+','.join(missing))
    raise SystemExit(1)
if m.get('queryHash')!=sha(compact(json.loads(query.read_text()))):
    print('verify.fixture_provenance.mismatch: queryHash')
    raise SystemExit(1)
if m.get('hitsHash')!=sha(hits.read_bytes()):
    print('verify.fixture_provenance.mismatch: hitsHash')
    raise SystemExit(1)
if m.get('statsHash')!=sha(stats.read_bytes()):
    print('verify.fixture_provenance.mismatch: statsHash')
    raise SystemExit(1)
ctx=m.get('contextPackHash',{})
if ctx.get('first30Sha256')!=sha(pack.read_bytes()):
    print('verify.fixture_provenance.mismatch: contextPackHash.first30Sha256')
    raise SystemExit(1)
if not isinstance(ctx.get('fullBytes'), int) or ctx.get('fullBytes') < len(pack.read_bytes()):
    print('verify.fixture_provenance.invalid: contextPackHash.fullBytes')
    raise SystemExit(1)
if not re.match(r'^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$', str(m.get('createdUtc',''))):
    print('verify.fixture_provenance.invalid: createdUtc')
    raise SystemExit(1)
commit=str(m.get('commitSha',''))
if commit and not re.match(r'^[0-9a-f]{40}$', commit):
    print('verify.fixture_provenance.invalid: commitSha')
    raise SystemExit(1)
print('verify.fixture_provenance.ok')
PY
