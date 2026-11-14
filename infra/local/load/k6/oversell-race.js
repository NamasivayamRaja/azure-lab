// Step 5 oversell test: fires exactly 200 concurrent redeem requests (all eligible UserId="12345")
// against an offer with Capacity = 50.
//   k6 run infra\local\load\k6\oversell-race.js
import http from 'k6/http';
import { check } from 'k6';

const BASE = __ENV.BASE_URL || 'http://localhost:5215';
const OFFER_ID = __ENV.OFFER_ID || '5b0b2d4e-7c1a-4f0e-9a55-0a1b2c3d4e5f';

export const options = {
  scenarios: {
    burst200: {
      executor: 'shared-iterations',
      vus: 50,
      iterations: 200,
      maxDuration: '30s',
    },
  },
};

export default function () {
  const res = http.post(
    `${BASE}/offers/${OFFER_ID}/redeem`,
    JSON.stringify({ userId: '12345' }),
    { headers: { 'Content-Type': 'application/json', 'X-Correlation-ID': `oversell-${__VU}-${__ITER}-${Date.now()}` } },
  );
  check(res, { 'status is 202': (r) => r.status === 202 });
}
