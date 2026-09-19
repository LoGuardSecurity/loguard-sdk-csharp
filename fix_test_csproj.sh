#!/usr/bin/env bash
set -euo pipefail

CSPROJ="$HOME/PythonProjects/final-bundle/loguard-csharp-sdk/tests/LoGuard.Tests/LoGuard.Tests.csproj"

python3 - "$CSPROJ" << 'PYEOF'
import sys, re
path = sys.argv[1]
with open(path, "r") as f:
    content = f.read()

# Replace every XML comment block, stripping any '--' inside it
# (XML forbids '--' anywhere inside a comment, not just at the end).
def fix_comment(m):
    inner = m.group(1)
    inner = inner.replace("--", "-")
    return f"<!--{inner}-->"

new_content = re.sub(r"<!--(.*?)-->", fix_comment, content, flags=re.DOTALL)

if new_content != content:
    with open(path, "w") as f:
        f.write(new_content)
    print("Комментарии исправлены.")
else:
    print("Изменений не потребовалось (либо не нашёл комментариев -- странно).")
PYEOF
