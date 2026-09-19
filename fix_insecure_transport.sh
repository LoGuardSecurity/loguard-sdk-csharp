#!/usr/bin/env bash
set -euo pipefail

TESTS_DIR="$HOME/PythonProjects/final-bundle/loguard-csharp-sdk/tests/LoGuard.Tests"

python3 - "$TESTS_DIR" << 'PYEOF'
import sys, re, pathlib

tests_dir = pathlib.Path(sys.argv[1])
pattern = re.compile(r"new LoGuardClient\(([^)]*)\)")

total_files_changed = 0
total_calls_fixed = 0

for path in tests_dir.rglob("*.cs"):
    text = path.read_text()

    def fix(m):
        global total_calls_fixed
        args = m.group(1)
        if "allowInsecureTransport" in args:
            return m.group(0)  # already explicit, leave alone
        total_calls_fixed += 1
        return f"new LoGuardClient({args}, allowInsecureTransport: true)"

    new_text = pattern.sub(fix, text)

    if new_text != text:
        path.write_text(new_text)
        total_files_changed += 1
        print(f"patched: {path.relative_to(tests_dir)}")

print(f"\n{total_files_changed} files changed, {total_calls_fixed} LoGuardClient(...) calls fixed")
PYEOF
