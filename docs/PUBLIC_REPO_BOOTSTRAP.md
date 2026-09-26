# Bootstrap public repository

## Blocker (2026-09-26)

Creating `kern-services/ironbrain-mailcal` via the Cloud Agent GitHub token failed:

```text
gh repo create kern-services/ironbrain-mailcal --public ...
HTTP 403: Resource not accessible by integration
(https://api.github.com/orgs/kern-services/repos)
```

The authenticated integration can push to `kern-services/ironbrain` but cannot create new org repositories.

## Org admin: create empty public repo once

```bash
gh repo create kern-services/ironbrain-mailcal \
  --public \
  --description "Ironbrain MailCal — self-hosted IMAP/SMTP/CalDAV for bots (CLI + MCP)" \
  --license mit
```

Or via GitHub UI: New repository under `kern-services`, name `ironbrain-mailcal`, public, MIT, **no** README (empty).

## Push this extract

From a checkout of `kern-services/ironbrain` on branch `cursor/mailcal-p0-community-5abb`:

```bash
EXTRACT=community/ironbrain-mailcal
TMP=$(mktemp -d)
cp -a "$EXTRACT"/. "$TMP/"
cd "$TMP"
git init
git checkout -b main
git add -A
git commit -m "feat: initial Ironbrain MailCal public extract (0.1.2)"
git remote add origin https://github.com/kern-services/ironbrain-mailcal.git
git push -u origin main
```

Then open a PR from a feature branch on the new repo if desired, or treat `main` as the squash-ready landing.

## After public repo exists

1. Re-run Cloud Agent or push updates from this extract folder.
2. Submit https://cursor.directory/plugins/new with the public repo URL.
3. Optionally republish nuget with `RepositoryUrl` pointing here (already set to 0.1.2 in this extract).
