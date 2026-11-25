// Load on POST /v1/jobs: requests per second ramp up to 50, each uploading a tiny PNG, so the
// time measured is the API's own work (authentication, quota check, upload checks, storage, the
// job row), not the transfer. Target from the spec: p95 below 200 ms.
// Run with loadtests/run.ps1, which raises the rate limit and quota for the duration.
import http from 'k6/http';
import { check } from 'k6';
import encoding from 'k6/encoding';

const baseUrl = __ENV.BASE_URL || 'http://localhost:5081';
const apiKey = __ENV.API_KEY || 'cvt_devlocal_DevelopmentOnlyKeyDoNotUseInProd';

// A 1x1 PNG.
const png = encoding.b64decode(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC',
  'std',
);

export const options = {
  scenarios: {
    create: {
      executor: 'ramping-arrival-rate',
      startRate: 5,
      timeUnit: '1s',
      preAllocatedVUs: 20,
      maxVUs: 100,
      stages: [
        { target: 20, duration: '30s' },
        { target: 50, duration: '30s' },
        { target: 50, duration: '60s' },
      ],
    },
  },
  thresholds: {
    'http_req_duration{name:create job}': ['p(95)<200'],
    checks: ['rate>0.99'],
    // A saturated load generator must not hide behind a good p95.
    dropped_iterations: ['count==0'],
  },
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export default function () {
  const response = http.post(
    `${baseUrl}/v1/jobs`,
    { file: http.file(png, 'photo.png', 'image/png'), targetFormat: 'jpg' },
    { headers: { 'X-Api-Key': apiKey }, tags: { name: 'create job' } },
  );
  check(response, { 'accepted (202)': (r) => r.status === 202 });
}
