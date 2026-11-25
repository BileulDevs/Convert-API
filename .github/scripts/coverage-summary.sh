#!/usr/bin/env bash
# Writes the line and branch coverage of a Cobertura report, overall and per assembly,
# to the job summary. Usage: coverage-summary.sh "<title>" <report.cobertura.xml>
set -euo pipefail

title=$1
report=$2

if [[ ! -f "$report" ]]; then
  echo "No coverage report at $report" >> "$GITHUB_STEP_SUMMARY"
  exit 0
fi

python3 - "$title" "$report" >> "$GITHUB_STEP_SUMMARY" <<'PY'
import sys
import xml.etree.ElementTree as ET

title, path = sys.argv[1], sys.argv[2]
root = ET.parse(path).getroot()
pct = lambda element, key: f"{float(element.get(key, 0)) * 100:.0f} %"

print(f"### {title}: coverage\n")
print("| Assembly | Lines | Branches |")
print("|---|---:|---:|")
for package in root.iter("package"):
    print(f"| {package.get('name')} | {pct(package, 'line-rate')} | {pct(package, 'branch-rate')} |")
print(f"| **Total** | **{pct(root, 'line-rate')}** | **{pct(root, 'branch-rate')}** |\n")
PY
