// Tiny mock of what the HDT plugin will eventually serve.
// Lets you test index.html's Live mode without HDT + Hearthstone.
//
//   node plugin/test-sidecar/mock-sidecar.js
//
// Then open index.html in a browser, flip the Live toggle.
// The mock cycles through a few canned lobby states every ~6 seconds so you
// can see tribes and hero slots updating live.

const http = require('http');

const CANNED = [
  null,                                                          // first 2s: no lobby yet (204)
  { banned: [14, 23],         heroes: ['TB_BaconShop_HERO_36'] },
  { banned: [14, 23, 17],     heroes: ['TB_BaconShop_HERO_36', 'TB_BaconShop_HERO_43'] },
  { banned: [14, 23, 17, 28], heroes: ['TB_BaconShop_HERO_36', 'TB_BaconShop_HERO_43', 'BG31_HERO_802', 'BG22_HERO_001'] },
];

let tick = 0;
setInterval(() => { tick = (tick + 1) % CANNED.length; console.log('[mock] state index =', tick); }, 6000);

const server = http.createServer((req, res) => {
  // Permissive CORS so file:// and any localhost dev server can hit us.
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Cache-Control', 'no-store');

  if (req.method !== 'GET' || req.url !== '/lobby') {
    res.writeHead(404); res.end(); return;
  }

  const state = CANNED[tick];
  if (state == null) { res.writeHead(204); res.end(); return; }
  res.writeHead(200, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(state));
});

const PORT = 9876;
server.listen(PORT, '127.0.0.1', () => {
  console.log(`[mock] HsDecktrackBgReader mock listening on http://localhost:${PORT}/lobby`);
});
