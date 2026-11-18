// Step 2 baseline: ramp VUs on POST /offers/{offerId}/redeem.
// Judge CORRECTNESS (SQL invariants), record p50/p95 + drain time for trends. Emulator RPS is not representative.
//
//   k6 run -e MAX_VUS=100 -e SLEEP=3 --summary-export infra\local\load\results\baseline-before.json infra\local\load\k6\redeem-ramp.js
//
// Env: BASE_URL (default http://localhost:5215), MAX_VUS (100), SLEEP seconds between requests per VU (3),
//      RAMP (default "2m"), HOLD ("0s"), PASS_RATIO (share of requests with UserId "12345" -> Completed; others -> Rejected)
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Trend } from 'k6/metrics';

const BASE = __ENV.BASE_URL || 'http://localhost:5215';
// Several API instances (Step 3+): BASE_URLS="http://localhost:5215,http://localhost:5216" round-robins per VU
const BASES = (__ENV.BASE_URLS || BASE).split(',');
const MAX_VUS = parseInt(__ENV.MAX_VUS || '100');
const SLEEP = parseFloat(__ENV.SLEEP || '3');
const PASS_RATIO = parseFloat(__ENV.PASS_RATIO || '0.8');
const OFFER_ID = __ENV.OFFER_ID || '5b0b2d4e-7c1a-4f0e-9a55-0a1b2c3d4e5f'; // any Guid: handler has no offer lookup yet (Step 5 adds capacity)

const accepted = new Counter('redeem_accepted_202');
const submitMs = new Trend('redeem_submit_ms', true);

export const options = {
  scenarios: {
    ramp: {
      executor: 'ramping-vus',
      startVUs: 10,
      stages: [
        { duration: __ENV.RAMP || '2m', target: MAX_VUS },
        ...(__ENV.HOLD ? [{ duration: __ENV.HOLD, target: MAX_VUS }] : []),
        { duration: '10s', target: 0 },
      ],
      gracefulRampDown: '10s',
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],          // <1% errors
    redeem_submit_ms: ['p(95)<500'],         // 202 should be fast: the API only does ONE SaveChanges
    redeem_accepted_202: ['count>0'],
  },
};

export default function () {
  const base = BASES[(__VU - 1) % BASES.length];
  const userId = Math.random() < PASS_RATIO ? '12345' : `user-${__VU}-${__ITER}`;
  const res = http.post(
    `${base}/offers/${OFFER_ID}/redeem`,
    JSON.stringify({ userId }),
    {
      headers: {
        'Content-Type': 'application/json',
        'X-Correlation-ID': `k6-${__VU}-${__ITER}-${Date.now()}`,
        ...(__ENV.AUTH_TOKEN ? { Authorization: `Bearer ${__ENV.AUTH_TOKEN}` } : {}),
      },
    },
  );
  submitMs.add(res.timings.duration);
  if (check(res, { 'status is 202': (r) => r.status === 202 })) accepted.add(1);
  sleep(SLEEP);
}
