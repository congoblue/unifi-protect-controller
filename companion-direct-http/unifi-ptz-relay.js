// UniFi Protect PTZ relay for Bitfocus Companion
//
// Protect's PTZ control needs a logged-in session (cookie + CSRF token),
// which Companion's Generic HTTP module can't maintain on its own. This
// tiny service logs in once, keeps the session alive, and exposes plain
// HTTP endpoints that Companion can hit with no auth of its own.
//
// Setup:
//   npm install express axios axios-cookiejar-support tough-cookie
//   node unifi-ptz-relay.js
//
// Then in Companion, add a "Generic HTTP" connection pointed at this
// relay (e.g. http://localhost:4000) and use GET requests like:
//   /goto/<cameraId>/<slot>   (slot -1 = home position)
//   /discover                 (lists cameras + their preset slots)

const express = require('express');
const axios = require('axios');
const https = require('https');
const { CookieJar } = require('tough-cookie');
const { wrapper } = require('axios-cookiejar-support');

// --- Configuration: edit these for your setup ---
const NVR_ADDRESS = 'https://192.168.1.1'; // your UDM / UNVR / UDR address
const USERNAME = 'api-user';               // a local (non-SSO) admin account
const PASSWORD = 'changeme';
const RELAY_PORT = 4000;
// --------------------------------------------------

const jar = new CookieJar();
const client = wrapper(axios.create({
  jar,
  withCredentials: true,
  httpsAgent: new https.Agent({ rejectUnauthorized: false }), // Protect uses a self-signed cert locally
  validateStatus: () => true, // let us inspect status codes ourselves
}));

let deviceToken = null;
let csrfToken = null;

async function login() {
  const res = await client.post(`${NVR_ADDRESS}/api/auth/login`, {
    username: USERNAME,
    password: PASSWORD,
    rememberMe: true,
    token: '',
  });
  if (res.status !== 200 || !res.data?.deviceToken) {
    throw new Error(`Login failed (HTTP ${res.status})`);
  }
  deviceToken = res.data.deviceToken;
  csrfToken = res.headers['x-csrf-token'];
  console.log('Authenticated to UniFi Protect');
}

function authHeaders() {
  return { TOKEN: deviceToken, 'X-CSRF-Token': csrfToken };
}

// Wraps a Protect API call; re-authenticates once and retries on 401/403.
async function protectRequest(method, path, data = {}) {
  let res = await client.request({
    method,
    url: `${NVR_ADDRESS}/proxy/protect/api${path}`,
    headers: authHeaders(),
    data,
  });
  if (res.status === 401 || res.status === 403) {
    await login();
    res = await client.request({
      method,
      url: `${NVR_ADDRESS}/proxy/protect/api${path}`,
      headers: authHeaders(),
      data,
    });
  }
  return res;
}

const app = express();

// Move a camera to a preset slot. Slot -1 = home position.
app.get('/goto/:cameraId/:slot', async (req, res) => {
  try {
    const { cameraId, slot } = req.params;
    const r = await protectRequest('post', `/cameras/${cameraId}/ptz/goto/${slot}`);
    res.sendStatus(r.status);
  } catch (err) {
    console.error(err.message);
    res.status(500).send(err.message);
  }
});

// List cameras and their available preset slots — use this once to find
// the camera IDs and slot numbers you'll wire into Companion buttons.
app.get('/discover', async (req, res) => {
  try {
    const camsRes = await protectRequest('get', '/cameras');
    const cameras = camsRes.data || [];
    const results = [];
    for (const cam of cameras) {
      const presetRes = await protectRequest('get', `/cameras/${cam.id}/ptz/preset`);
      results.push({
        id: cam.id,
        name: cam.name,
        presets: (presetRes.data || []).map((p) => ({ slot: p.slot, name: p.name })),
      });
    }
    res.json(results);
  } catch (err) {
    console.error(err.message);
    res.status(500).send(err.message);
  }
});

app.listen(RELAY_PORT, async () => {
  try {
    await login();
  } catch (err) {
    console.error('Initial login failed:', err.message);
  }
  console.log(`PTZ relay listening on http://localhost:${RELAY_PORT}`);
});
