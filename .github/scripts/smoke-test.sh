#!/usr/bin/env bash
# Checks a deployed ConvertAPI end to end: it is ready, accepts a PNG, a worker converts it to
# JPEG, and the result downloads. Both apps may be scaled to zero, so it waits for cold starts.
# Usage: smoke-test.sh <base url> <API key>
set -euo pipefail

if [[ -z ${1:-} || -z ${2:-} ]]; then
  echo "Usage: smoke-test.sh <base url> <API key>" >&2
  exit 2
fi

base=${1%/}
key=$2
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# Every call is bounded: a connection hanging during a cold start must not outlast the deadlines.
curl() { command curl --silent --connect-timeout 10 --max-time 30 "$@"; }

wait_for() { # <seconds> <description> <command...>
  local deadline=$((SECONDS + $1)) what=$2
  shift 2
  until "$@"; do
    if ((SECONDS >= deadline)); then
      echo "Timed out waiting for $what" >&2
      return 1
    fi
    sleep 5
  done
}

ready() { [[ $(curl -o /dev/null -w '%{http_code}' "$base/health/ready") == 200 ]]; }
echo "Waiting for $base to be ready"
wait_for 300 "the API" ready

# A 1x1 red PNG, built here so the test needs no file of its own.
python3 - "$work/in.png" <<'PY'
import struct, sys, zlib
def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 1, 1, 8, 2, 0, 0, 0)) \
    + chunk(b"IDAT", zlib.compress(b"\x00\xff\x00\x00")) + chunk(b"IEND", b"")
open(sys.argv[1], "wb").write(png)
PY

echo "Submitting a PNG to JPEG conversion"
response=$(curl -w '\n%{http_code}' -H "X-Api-Key: $key" -F "file=@$work/in.png;type=image/png" -F targetFormat=jpg "$base/v1/jobs")
status=${response##*$'\n'}
body=${response%$'\n'*}
if [[ $status != 202 ]]; then
  echo "Expected 202, got $status: $body" >&2
  exit 1
fi
id=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])' <<< "$body") || { echo "Unexpected response: $body" >&2; exit 1; }
echo "Job $id queued"

job_status() { curl -H "X-Api-Key: $key" "$base/v1/jobs/$id" | python3 -c 'import json,sys; print(json.load(sys.stdin)["status"])' 2>/dev/null || echo Unknown; }
finished() { [[ $(job_status) =~ ^(Succeeded|Failed|Canceled)$ ]]; }
wait_for 300 "the conversion (a worker may be starting)" finished
final=$(job_status)
if [[ $final != Succeeded ]]; then
  echo "The job ended as $final" >&2
  exit 1
fi

status=$(curl -o "$work/out.jpg" -w '%{http_code}' -H "X-Api-Key: $key" "$base/v1/jobs/$id/result")
if [[ $status != 200 || $(head -c 2 "$work/out.jpg" | od -An -tx1 | tr -d ' ') != ffd8 ]]; then
  echo "The result did not download as a JPEG (HTTP $status)" >&2
  exit 1
fi

echo "Smoke test passed: $base converted a PNG to JPEG (job $id)"
