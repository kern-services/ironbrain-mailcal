# Smoke checklist

Run against a real staging config (never commit it). Passwords must never appear in output.

```bash
export PATH="$PATH:$HOME/.dotnet/tools"
export IRONBRAIN_MAILCAL_CONFIG="$HOME/.config/ironbrain/mailcal.json"

# Config discovery
ironbrain-mailcal config path
ironbrain-mailcal account list
ironbrain-mailcal account show assistant   # passwords redacted

# IMAP
ironbrain-mailcal -a assistant mail list --limit 5
ironbrain-mailcal -a assistant mail get '<imap-unique-id-from-list>'

# CalDAV discover / list
ironbrain-mailcal -a family-cal cal calendars
ironbrain-mailcal -a family-cal cal list --date today --range day
ironbrain-mailcal -a family-cal cal list --date today -c Family,personal
```

## SMTP (only when Enabled)

Only for accounts with `"Email": { "Smtp": { "Enabled": true } }`:

```bash
ironbrain-mailcal -a assistant mail send \
  --to you@example.com \
  --subject "mailcal smoke" \
  --body "ok"
```

Expect a clear error (non-zero exit) when `Enabled` is `false` — no silent fallback to another account.

## Optional calendar write

```bash
ironbrain-mailcal -a family-cal cal add -c personal \
  --summary "mailcal smoke" \
  --start "2026-09-26T10:00:00+02:00" \
  --end "2026-09-26T10:15:00+02:00" \
  --tz Europe/Berlin
```

Exit code `0` and JSON/list output = success.
