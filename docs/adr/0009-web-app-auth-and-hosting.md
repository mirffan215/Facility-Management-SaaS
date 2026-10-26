# ADR-0009: Web app sign-in, hosting and testing
Status: Proposed

## Decisions
- **Sign-in is OIDC authorization code with PKCE, in the browser** (`oidc-client-ts`), against the provider the API advertises at
  `GET /api/v1/auth/providers` (name, authority, client id, scope). The API stays a pure resource server; it never sees a password or a
  cookie. The access token is kept in `sessionStorage` (gone when the tab closes), never in `localStorage`, and is sent as a bearer
  token. A 401 from the API ends the session and returns to the sign-in page. A returned `returnTo` is accepted only if it is a same-site path.
- **Silent renewal uses refresh tokens, not a hidden iframe.** The provider's `Scope` includes `offline_access`; `oidc-client-ts` renews shortly
  before expiry. This needs no extra page and no `frame-src` in the CSP, and works with third-party-cookie blocking. A failed renewal ends the
  session only once the access token is actually unusable, so a network blip does not sign anyone out. A provider that issues no refresh token
  degrades to the old behaviour (sign in again when the token expires). Entra issues rotating refresh tokens to single-page apps (limited lifetime).
- **One origin for the browser.** In production a small nginx serves the built files and proxies `/api` to the API, so there is no CORS
  surface. Security headers (CSP, `nosniff`, no framing, no referrer, locked-down permissions) are set once at server level. nginx drops
  inherited `add_header` lines when a location declares its own, so caching uses `expires` instead. Built files go to `/static/`
  because `/assets/<id>` is a page of the app. The CSP's `connect-src` lists the identity provider's origin (`CSP_CONNECT_SRC`).
- **The server is the authority; the UI mirrors it.** Buttons are hidden when the user lacks a permission, but every rule (hierarchy, status,
  meters, file types) is enforced by the API and shown back as field errors from RFC 7807 responses. A stale edit (409) asks the person to reload.
- **Arabic and English are first-class.** All text is translated (a test fails if a key is missing or placeholders differ), the page direction
  follows the language, CSS uses logical properties, names show in Arabic when one exists, mixed-direction breadcrumbs use Unicode isolates,
  and the language and calendar (Gregorian or Hijri, via `Intl`) are remembered per browser. Digits are Latin in both languages.
- **QR labels resolve through the app.** A label encodes `{PublicBaseUrl}/a/{tag}`; that route looks the tag up and opens the asset, signing
  the person in first if needed.
- **Testing has three layers.** Unit and component tests (Vitest, Testing Library) for logic and widgets. A full-stack browser journey
  (Playwright) against the real web build served by the production nginx configuration (so the strict CSP applies and any violation fails the run), real API, real SQL Server and a **fake OpenID Connect provider** (`web/e2e/fake-idp.mjs`) that
  does real PKCE and signs real tokens, so sign-in is exercised for real. Automated accessibility scans (axe) of the main screens in both
  languages. The fake provider is a test tool only: the API accepts an `http://` authority solely when `Authentication:AllowInsecureAuthority`
  is set **and** the environment is Development; anywhere else that setting stops the app from starting.

## Consequences
- Sessions last as long as the provider's refresh token (Entra limits it for single-page apps), then the person signs in again.
- A deployment must register the web app as a public client at its identity provider and list the provider's origin in `CSP_CONNECT_SRC`
  to get the tight policy (the default allows any https origin for the token call).
- The full-stack test needs Docker, .NET, Node and Chromium; `scripts/e2e-full.sh` runs it locally and in CI.
