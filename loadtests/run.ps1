<#
.SYNOPSIS
Runs the k6 load tests against the Compose stack, published on port 5081 so a local API on 5080
is left alone. Needs Docker only: k6 and the test image come from containers.

.EXAMPLE
.\loadtests\run.ps1                   # both scenarios, one worker
.\loadtests\run.ps1 -Workers 3 -Only worker-throughput
#>
[CmdletBinding()]
param(
    [int] $Workers = 1,

    # Throughput first, on an empty queue: the create test leaves thousands of jobs queued.
    [ValidateSet('create-jobs', 'worker-throughput')]
    [string[]] $Only = @('worker-throughput', 'create-jobs')
)

# Continue, not Stop: Docker and k6 write progress to stderr, which Windows PowerShell turns into
# errors once redirected. Failures are read from exit codes instead.
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
$failed = @()
$k6Image = 'grafana/k6:2.3.0'

# A Compose project of its own, so its database and files are separate from the usual local
# stack and are deleted at the end.
$compose = @('compose', '--project-name', 'convertapi-loadtest',
    '-f', (Join-Path $root 'compose.yaml'), '-f', (Join-Path $PSScriptRoot 'compose.loadtest.yaml'))

# The rate limit and the active-job quota are there to protect the service from one client;
# here that one client is the load test, so both are raised for the run (compose.loadtest.yaml).
try {
    docker @compose up --build --detach --scale "worker=$Workers"
    if ($LASTEXITCODE -ne 0) { throw 'Could not start the stack.' }

    $deadline = (Get-Date).AddMinutes(3)
    do {
        Start-Sleep -Seconds 2
        $ready = (curl.exe -s -o NUL -w '%{http_code}' http://localhost:5081/health/ready) -eq '200'
    } until ($ready -or (Get-Date) -gt $deadline)
    if (-not $ready) { throw 'The API did not become ready.' }

    $fixtures = Join-Path $PSScriptRoot 'fixtures'
    New-Item -ItemType Directory -Force $fixtures | Out-Null
    docker run --rm -v "${PSScriptRoot}:/scripts:ro" -v "${fixtures}:/out" python:3.13-slim `
        python /scripts/make-photo.py /out/photo.png
    if ($LASTEXITCODE -ne 0) { throw 'Could not generate the test image.' }

    foreach ($scenario in $Only) {
        Write-Host "--- $scenario ($Workers worker(s))"
        docker run --rm -v "${PSScriptRoot}:/scripts:ro" -v "${fixtures}:/fixtures:ro" `
            -e BASE_URL=http://host.docker.internal:5081 $k6Image run "/scripts/$scenario.js"
        # k6 exits with 99 when a threshold fails.
        if ($LASTEXITCODE -ne 0) { $failed += $scenario }
    }
}
finally {
    docker @compose down --volumes
}

if ($failed) {
    Write-Error "Failed: $($failed -join ', ')"
    exit 1
}
