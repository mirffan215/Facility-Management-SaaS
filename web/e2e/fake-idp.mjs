// A tiny OpenID Connect provider for end-to-end tests ONLY. It does real authorization-code + PKCE and signs real RS256 tokens, so the
// browser, the web app's OIDC client and the API's JWT validation are all exercised for real. It approves every sign-in automatically.
import crypto from 'node:crypto';
import http from 'node:http';

const PORT = Number(process.env.IDP_PORT ?? 9099);
const ISSUER = `http://localhost:${PORT}`;
const AUDIENCE = process.env.IDP_AUDIENCE ?? 'api://fms';
const { privateKey, publicKey } = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
const jwk = { ...publicKey.export({ format: 'jwk' }), kid: 'k1', use: 'sig', alg: 'RS256' };

let currentUser = { sub: 'e2e-admin', name: 'E2E Admin', email: 'admin@example.test' };
const codes = new Map();

const b64u = (input) => Buffer.from(input).toString('base64url');
const sign = (payload) => {
  const data = `${b64u(JSON.stringify({ alg: 'RS256', kid: 'k1', typ: 'JWT' }))}.${b64u(JSON.stringify(payload))}`;
  return `${data}.${crypto.sign('RSA-SHA256', Buffer.from(data), privateKey).toString('base64url')}`;
};
const now = () => Math.floor(Date.now() / 1000);

const accessToken = (user) => sign({ iss: ISSUER, aud: AUDIENCE, sub: user.sub, name: user.name, email: user.email, iat: now(), exp: now() + 3600 });
const idToken = (user, clientId, nonce) => sign({ iss: ISSUER, aud: clientId, sub: user.sub, name: user.name, nonce, iat: now(), exp: now() + 3600 });

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
          id_token_signing_alg_values_supported: ['RS256'], code_challenge_methods_supported: ['S256'], scopes_supported: ['openid', 'profile', 'email'],
        });
      case '/jwks':
        return send(res, 200, { keys: [jwk] });
      case '/authorize': {
        const q = url.searchParams;
        if (q.get('response_type') !== 'code' || q.get('code_challenge_method') !== 'S256' || !q.get('code_challenge')) return send(res, 400, { error: 'PKCE required' });
        const code = crypto.randomBytes(16).toString('hex');
        codes.set(code, { challenge: q.get('code_challenge'), redirect: q.get('redirect_uri'), clientId: q.get('client_id'), nonce: q.get('nonce'), user: currentUser });
        const back = new URL(q.get('redirect_uri'));
        back.searchParams.set('code', code);
        back.searchParams.set('state', q.get('state') ?? '');
        res.writeHead(302, { Location: back.toString() });
        return res.end();
      }
      case '/token': {
        const form = new URLSearchParams(await readBody(req));
        const entry = codes.get(form.get('code'));
        codes.delete(form.get('code'));
        if (!entry || form.get('grant_type') !== 'authorization_code') return send(res, 400, { error: 'invalid_grant' });
        const verifier = crypto.createHash('sha256').update(form.get('code_verifier') ?? '').digest('base64url');
        if (verifier !== entry.challenge || form.get('redirect_uri') !== entry.redirect || form.get('client_id') !== entry.clientId) return send(res, 400, { error: 'invalid_grant' });
        return send(res, 200, {
          access_token: accessToken(entry.user), id_token: idToken(entry.user, entry.clientId, entry.nonce), token_type: 'Bearer', expires_in: 3600, scope: 'openid profile email',
        });
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
      case '/__health':
        return send(res, 200, { ok: true });
      default:
        return send(res, 404, { error: 'not found' });
    }
  })
  .listen(PORT, () => console.log(`fake idp on ${ISSUER}`));
