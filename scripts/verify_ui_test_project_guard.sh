#!/usr/bin/env bash
set -euo pipefail

python3 - <<'PY'
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

project = Path('ui/Shoots.Ui.Tests/Shoots.Ui.Tests.csproj')
if not project.exists():
    sys.stderr.write(f"error: missing {project}\n")
    sys.exit(1)

root = ET.parse(project).getroot()
found_expected = False
for elem in root.iter():
    if elem.tag.split('}')[-1] != 'IsTestProject':
        continue
    condition = (elem.attrib.get('Condition') or '').strip()
    value = (elem.text or '').strip().lower()
    if condition == "'$(OS)' != 'Windows_NT'" and value == 'false':
        found_expected = True
    else:
        sys.stderr.write(
            "error: invalid IsTestProject guard in ui/Shoots.Ui.Tests/Shoots.Ui.Tests.csproj "
            f"(Condition={condition!r}, Value={value!r})\n"
        )
        sys.exit(1)

if not found_expected:
    sys.stderr.write(
        "error: expected IsTestProject guard with Condition \"'$(OS)' != 'Windows_NT'\" and value false\n"
    )
    sys.exit(1)

print('ui test project guard passed')
PY
