# Phase 10: AI assistance

Design record: [ADR-0030](adr/0030-ai-assistance.md). Scope chosen by the customer: AI assist with a provider interface, **off by default**; the Claude API is the first provider.

## Built
| Part | What |
|---|---|
| Foundation | `ai` module with its own schema, row-level security and license module (`ai`). Provider interface with a Claude Messages API provider and a "none" provider; the operator sets it in configuration. Organisation settings (on/off, which suggestions, daily limit), audited. Append-only usage log without any words. |
| Suggestions | **Category and priority** while writing a request (a button on the new-request form; a person uses or dismisses it). **Summary** of a work order (a button on the work order). Both labelled as written by AI. |
| Safeguards | Personal details removed before sending; the request is marked as untrusted data; the answer is checked against the real categories and the four priorities; summaries are plain text; daily limit per organisation and hourly limit per person; no call is made once a limit is reached. |
| Administration | Administration, AI assistance: switches, daily limit, what is sent, use in the last 30 days. English and Arabic. |
| Hints (no model) | Work orders, "Assets with repeat problems": three or more repair requests in 90 days, counted from the work orders. |

Permissions: `ai.use` (Facility Manager, Technician), `ai.manage` (Facility Manager; Administrator has all). API: `ai/status`, `ai/settings`, `ai/usage`, `ai/triage`, `ai/work-orders/{id}/summary`, and `work-orders/repeat-problems`.

## Tests
Unit: cleaning of personal details (e-mail, web addresses, long numbers; ordinary numbers and work order numbers kept), reading the triage reply (valid, wrapped in text or a code fence, invented values dropped, nothing usable), plain-text summaries, the Claude provider against a stand-in server (key in a header only, token counts, output cap, failure without leaking the body or key, safe addresses only), the average gap between repeat problems. Integration (real SQL Server, stand-in model): off by default and nothing sent; cannot be turned on without a provider; roles; suggestions checked against real categories; steering by the request's own words cannot invent a value; personal details never reach the model; each suggestion switchable on its own; daily limit stops calls; a failing service is reported without detail; usage log holds no text and cannot be changed (trigger); settings validation and versions; another organisation shares neither switch, limit, categories nor work orders; repeat-problem rules and access. Browser: a fake AI service stands in for the provider; the step checks off by default, turning it on, a suggestion that is applied only on request, personal details absent from what the service received, a summary, and accessibility scans in English and Arabic.

## Not done / known gaps
- **No real AI service has been called.** Everything was tested against stand-ins; the Claude provider's request and response handling follows the documented Messages API but has not been run against it. Try it with a real key on a test organisation before relying on it.
- Accept and ignore are not recorded, so quality cannot be measured; no suggested assignee or vendor, duplicate detection or predictive maintenance from meter readings.
- Suggestions are not offered in the technician mobile app or offline.
- The provider's data handling (retention, training) is governed by the operator's agreement with it; the product only limits what it sends.
- Prompts are in English; the quality of Arabic suggestions is untested with a real model.
- Limits are database counts: a burst can slightly overshoot.
- As before: the Helm chart, Bicep template and on-premise scripts have not been deployed; rate limits are per process.
