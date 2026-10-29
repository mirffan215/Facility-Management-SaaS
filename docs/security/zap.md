# OWASP ZAP baseline scan

A passive scan of the running stack: the production web image in front of the API, spidered as a signed-out visitor. It looks for missing headers, cookie flags, information leaks and similar; it does not attack, and it cannot see anything behind sign-in. It complements, and does not replace, the independent penetration test described in the [brief](pentest-brief.md).

Run it:
- In GitHub: Actions, "ZAP baseline", Run workflow (also runs weekly). The report is the `zap-report` artifact.
- Locally, with Docker and access to `ghcr.io`: `ZAP_BASELINE=1 scripts/e2e-full.sh`. The report is written to `$E2E_LOGS/zap` (`/tmp/fms-e2e/zap` by default).

Findings do not fail the run. Read them, fix what is real, and write down what is not (a false positive, or accepted with a reason) below so the next reader need not repeat the work.

**Status: the workflow and script mode were written in Phase 11 but have not been run.** The environment they were written in could not download the scanner image. The first run may need small changes (for example the scan's target address or time limit).

| Date | Finding | Decision |
|---|---|---|
| | none recorded yet | |
