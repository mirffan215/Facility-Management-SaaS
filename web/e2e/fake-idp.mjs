// A tiny OpenID Connect provider for end-to-end tests ONLY. It does real authorization-code + PKCE and signs real RS256 tokens, so the
// browser, the web app's OIDC client and the API's JWT validation are all exercised for real. It approves every sign-in automatically.
import crypto from 'node:crypto';
import http from 'node:http';

const PORT = Number(process.env.IDP_PORT ?? 9099);
const ISSUER = `http://localhost:${PORT}`;
const AUDIENCE = process.env.IDP_AUDIENCE ?? 'api://fms';
const { privateKey, publicKey } = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
const jwk = { ...publicKey.export({ format: 'jwk' }), kid: 'k1', use: 'sig', alg: 'RS256' };

let lastAiPrompt = '';
let currentUser = { sub: 'e2e-admin', name: 'E2E Admin', email: 'admin@example.test' };
const codes = new Map();
const refreshTokens = new Map();
let ttl = 3600; // seconds; a test can shorten it with /__ttl to watch silent renewal
let refreshes = 0;

const b64u = (input) => Buffer.from(input).toString('base64url');
const sign = (payload) => {
  const data = `${b64u(JSON.stringify({ alg: 'RS256', kid: 'k1', typ: 'JWT' }))}.${b64u(JSON.stringify(payload))}`;
  return `${data}.${crypto.sign('RSA-SHA256', Buffer.from(data), privateKey).toString('base64url')}`;
};
const now = () => Math.floor(Date.now() / 1000);

const accessToken = (user) => sign({ iss: ISSUER, aud: AUDIENCE, sub: user.sub, name: user.name, email: user.email, iat: now(), exp: now() + ttl });
const idToken = (user, clientId, nonce) => sign({ iss: ISSUER, aud: clientId, sub: user.sub, name: user.name, nonce, iat: now(), exp: now() + ttl });

const tokens = (user, clientId, nonce, scope = 'offline_access') => {
  const body = { access_token: accessToken(user), id_token: idToken(user, clientId, nonce), token_type: 'Bearer', expires_in: ttl, scope: 'openid profile email' };
  if (!scope.split(' ').includes('offline_access')) return body;
  const refresh = crypto.randomBytes(24).toString('hex');
  refreshTokens.set(refresh, { user, clientId, nonce });
  return { ...body, refresh_token: refresh };
};

const cors = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Headers': '*',
  'Access-Control-Allow-Methods': 'GET, POST, OPTIONS',
};
const send = (res, status, body, headers = {}) => {
  res.writeHead(status, { ...cors, 'Content-Type': 'application/json', ...headers });
  res.end(typeof body === 'string' ? body : JSON.stringify(body));
};
const readBody = (req) => new Promise((resolve) => { let data = ''; req.on('data', (c) => (data += c)); req.on('end', () => resolve(data)); });

http
  .createServer(async (req, res) => {
    const url = new URL(req.url, ISSUER);
    if (req.method === 'OPTIONS') return send(res, 204, '');

    switch (url.pathname) {
      case '/.well-known/openid-configuration':
        return send(res, 200, {
          issuer: ISSUER, authorization_endpoint: `${ISSUER}/authorize`, token_endpoint: `${ISSUER}/token`, jwks_uri: `${ISSUER}/jwks`,
          end_session_endpoint: `${ISSUER}/logout`, response_types_supported: ['code'], subject_types_supported: ['public'],
          id_token_signing_alg_values_supported: ['RS256'], code_challenge_methods_supported: ['S256'], scopes_supported: ['openid', 'profile', 'email', 'offline_access'], grant_types_supported: ['authorization_code', 'refresh_token'],
        });
      case '/jwks':
        return send(res, 200, { keys: [jwk] });
      case '/authorize': {
        const q = url.searchParams;
        if (q.get('response_type') !== 'code' || q.get('code_challenge_method') !== 'S256' || !q.get('code_challenge')) return send(res, 400, { error: 'PKCE required' });
        const code = crypto.randomBytes(16).toString('hex');
        codes.set(code, { challenge: q.get('code_challenge'), redirect: q.get('redirect_uri'), clientId: q.get('client_id'), nonce: q.get('nonce'), scope: q.get('scope') ?? '', user: currentUser });
        const back = new URL(q.get('redirect_uri'));
        back.searchParams.set('code', code);
        back.searchParams.set('state', q.get('state') ?? '');
        res.writeHead(302, { Location: back.toString() });
        return res.end();
      }
      case '/token': {
        const form = new URLSearchParams(await readBody(req));
        if (form.get('grant_type') === 'refresh_token') {
          // Rotating refresh tokens, as Microsoft Entra issues to single-page apps: each is good once.
          const held = refreshTokens.get(form.get('refresh_token'));
          refreshTokens.delete(form.get('refresh_token'));
          if (!held || held.clientId !== form.get('client_id')) return send(res, 400, { error: 'invalid_grant' });
          refreshes += 1;
          return send(res, 200, tokens(held.user, held.clientId, held.nonce));
        }
        const entry = codes.get(form.get('code'));
        codes.delete(form.get('code'));
        if (!entry || form.get('grant_type') !== 'authorization_code') return send(res, 400, { error: 'invalid_grant' });
        const verifier = crypto.createHash('sha256').update(form.get('code_verifier') ?? '').digest('base64url');
        if (verifier !== entry.challenge || form.get('redirect_uri') !== entry.redirect || form.get('client_id') !== entry.clientId) return send(res, 400, { error: 'invalid_grant' });
        return send(res, 200, tokens(entry.user, entry.clientId, entry.nonce, entry.scope));
      }
      case '/logout': {
        const back = url.searchParams.get('post_logout_redirect_uri');
        res.writeHead(302, { Location: back ?? '/' });
        return res.end();
      }
      // ---- test controls (not part of OIDC) ----
      case '/__user': {
        currentUser = JSON.parse(await readBody(req));
        return send(res, 200, currentUser);
      }
      case '/__token':
        return send(res, 200, { token: accessToken({ sub: url.searchParams.get('sub') ?? 'e2e-admin', name: 'Test', email: 'test@example.test' }) });
      // ---- a stand-in AI service (the Messages API) so the browser test can exercise the AI suggestions without a key or a network ----
      case '/v1/messages': {
        if (req.headers['x-api-key'] !== 'e2e-ai-key') return send(res, 401, { error: 'bad key' });
        const body = JSON.parse(await readBody(req));
        const user = body.messages?.[0]?.content ?? '';
        lastAiPrompt = user;
        const text = String(body.system).includes('file maintenance requests')
          ? JSON.stringify({ categoryCode: (/Categories:\n([^:\n]+):/.exec(user) ?? [])[1] ?? null, priority: 'High', confidence: 0.8, reason: 'Looks like a leak.' })
          : 'The work order is open and being looked at. Nothing looks stuck.';
        return send(res, 200, { content: [{ type: 'text', text }], usage: { input_tokens: 50, output_tokens: 20 } });
      }
      case '/__ai':
        return send(res, 200, { prompt: lastAiPrompt });
      case '/__ttl':
        ttl = Number(url.searchParams.get('seconds') ?? 3600);
        return send(res, 200, { ttl });
      case '/__stats':
        return send(res, 200, { refreshes });
      case '/__health':
        return send(res, 200, { ok: true });
      default:
        return send(res, 404, { error: 'not found' });
    }
  })
  .listen(PORT, () => console.log(`fake idp on ${ISSUER}`));
