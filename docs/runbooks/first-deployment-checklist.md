# First deployment checklist (Helm and Azure)

The Compose bundle has been installed, upgraded and rolled back for real ([`scripts/rehearse-onprem.sh`](../../scripts/rehearse-onprem.sh)). **The Helm chart and the Azure template have only ever been rendered and compiled, never deployed.** Use this list the first time, on a non-production cluster or resource group, and write down what differs. Fix the chart or template, not the checklist.

## Before
- [ ] Images for the version are in your registry (the release workflow pushes them; check the signatures if you require them).
- [ ] A production license signing key is in the build (`scripts/check-release-keys.sh` passes) and a license file exists for this installation.
- [ ] An identity provider client is registered (public client, PKCE) with the redirect address of the site; the administrator's subject id is known.
- [ ] Encryption keys for contact details exist in the secret store (see the deployment guide).

## Helm
- [ ] `helm lint` and `helm template` pass with your values (CI does this with sample values).
- [ ] `helm install` completes; the **migrator Job** runs first and succeeds (look at its logs: pending migrations applied, application login created).
- [ ] Pods are not root, have a read-only root file system and no added capabilities (`kubectl get pod -o yaml`).
- [ ] The **web** pod starts: its rendered nginx configuration folder is writable by its own user. *This is exactly what failed in the Compose rehearsal.*
- [ ] `/health/ready` answers through the ingress; `/api/v1/system/info` shows the version.
- [ ] Sign in; the administrator exists; **Administration → License** shows the license.
- [ ] Uploaded files survive a pod restart (the storage volume or blob store is wired).
- [ ] With more than one API replica, `Security__RateLimit__Shared=true`.
- [ ] `helm upgrade` to the next version: the migrator Job runs, the old pods drain, nothing is lost. Take a database backup first: there is no automatic rollback of the database.
- [ ] Network policy (if enabled) still allows the web pod to reach the API and the API to reach the database and the identity provider.

## Azure (Bicep)
- [ ] The template deploys into an empty resource group without edits other than parameters.
- [ ] The migrator job runs and succeeds before traffic moves; the application login can sign in to Azure SQL (the contained-user path has never been exercised).
- [ ] Secrets come from Key Vault references, not plain settings; the managed identity can read them.
- [ ] Blob storage works with the managed identity (upload and download a lease document).
- [ ] The container apps scale to zero and back without losing sessions or leaving the database unreachable.
- [ ] Diagnostic logs reach Log Analytics; the tenant id appears on request logs.

## After, for either
- [ ] Run the browser journey against the deployment if you can (`web/e2e`), or at least: create a request, assign it, complete it, see the audit trail.
- [ ] Record the date, version and any change you had to make in the release notes.
