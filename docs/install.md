# MailCal bot install (unattended Linux / Grok Bot)

Install **`ironbrain-mailcal`** on a bot host, point it at **any** self-hosted IMAP + CalDAV (or `.ics`) server — Mailcow/SOGo, mox, Dovecot, etc. — then smoke-test without a browser.

**Primary feed: [nuget.org](https://www.nuget.org)** (auth-free). Product overview: [ironbrain.de/mailcal](https://www.ironbrain.de/mailcal/).

| Package | Command | When to use |
|---------|---------|-------------|
| `Ironbrain.MailCal.Cli` | `ironbrain-mailcal` | Scripts, cron, CI, bot hosts that shell out (e.g. Grok Bot) |
| `Ironbrain.MailCal.Mcp` | `ironbrain-mailcal-mcp` | Cursor / other MCP clients (stdio); same config + SMTP send gate |

Both share `~/.config/ironbrain/mailcal.json`. Complementary, not competing.

Current package metadata version: **0.1.2**. Published nuget.org version may lag until maintainers republish.

---

## Prerequisites

| Requirement | Notes |
|-------------|--------|
| **.NET 10 SDK** | Tools target `net10.0`. Install: https://dotnet.microsoft.com/download/dotnet/10.0 |
| **Outbound network** | Bot → IMAP/SMTP/CalDAV hosts (and `api.nuget.org` for install). |

Verify SDK:

```bash
dotnet --version   # expect 10.x
```

---

## 1. Install the global tool

### One-liner (recommended — nuget.org)

No GitHub PAT; works with current `dotnet tool install` (no `--username` / `--password`):

```bash
dotnet tool install -g Ironbrain.MailCal.Cli
```

Ensure `~/.dotnet/tools` is on `PATH` (the SDK installer usually adds it):

```bash
export PATH="$PATH:$HOME/.dotnet/tools"
ironbrain-mailcal --help
```

### Update / uninstall

```bash
dotnet tool update -g Ironbrain.MailCal.Cli
dotnet tool uninstall -g Ironbrain.MailCal.Cli
```

### Companion MCP server (optional)

```bash
dotnet tool install -g Ironbrain.MailCal.Mcp

export IRONBRAIN_MAILCAL_CONFIG="$HOME/.config/ironbrain/mailcal.json"
ironbrain-mailcal-mcp   # stdio MCP; keep secrets in the same config file
```

### Advanced: GitHub Packages (optional secondary feed)

Prefer nuget.org. Use GitHub Packages only if you need a pre-release or org-private mirror. Requires a PAT with **`read:packages`** (and SSO for `kern-services` if required). Newer `dotnet tool install` may not accept `--username` / `--password`; a named NuGet source is more reliable:

```bash
export GITHUB_TOKEN=ghp_xxxxxxxx   # read:packages; do not commit

dotnet nuget add source "https://nuget.pkg.github.com/kern-services/index.json" \
  --name github-kern-services \
  --username kern-services \
  --password "$GITHUB_TOKEN" \
  --store-password-in-clear-text

dotnet tool install -g Ironbrain.MailCal.Cli --source github-kern-services
```

See also `nuget.config.mailcal.example`.

### Local pack (dev / offline)

From a clone of this repo:

```bash
./scripts/pack-mailcal.sh
dotnet tool install --global --add-source ./artifacts/nuget Ironbrain.MailCal.Cli
```

### Maintainer publish (CI — Trusted Publishing)

When a publish workflow is present in this repo (or until maintainers migrate publish CI here), packages can be published via tag / `workflow_dispatch`. **nuget.org** uses [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) (OIDC → short-lived API key). Do **not** add a long-lived `NUGET_API_KEY` repo secret.

**One-time nuget.org setup** (once Trusted Publishing targets this public repo):

1. Sign in to [nuget.org](https://www.nuget.org) as the account/org that will own the packages (e.g. `kern-services` or the individual profile that owns them).
2. Username → **Trusted Publishing** → add or update a policy:
   - **Repository Owner:** `kern-services`
   - **Repository:** `ironbrain-mailcal`
   - **Workflow File:** `publish-mailcal.yml` — **filename only** (no `.github/workflows/` path; must match the real workflow file name once added)
   - **Environment:** leave empty unless the job sets `environment:`
3. Policy ownership: choose the NuGet user or organization that should own `Ironbrain.MailCal.*`.
4. Private repos: the policy may be **temporarily active for 7 days** until the first successful publish locks GitHub owner/repo IDs; re-open the window in the UI if it expires unused.

**GitHub repo setup**

| Setting | Purpose |
|---------|---------|
| Variable or secret **`NUGET_USER`** | nuget.org **profile username** (not email). Prefer repository **variable** `NUGET_USER`; `secrets.NUGET_USER` also works. |
| Secret **`GITHUB_TOKEN`** | Provided automatically; used for optional GitHub Packages push (`packages: write`). |

Workflow job permissions should include `id-token: write` (OIDC) and `packages: write` (GitHub Packages). Prefer calling `NuGet/login@v1` immediately before `dotnet nuget push` to nuget.org (temp keys last ~1 hour).

This extract ships **CI build/test only** (`.github/workflows/ci.yml`). Add a publish workflow before relying on Trusted Publishing from this repo.

Optional secondary feed (maintainers with `GITHUB_TOKEN`):

```bash
VERSION=0.1.2 GITHUB_TOKEN=… OWNER=kern-services ./scripts/pack-mailcal.sh --push-github
```
---

## 2. Config file (no secrets in git)

Default path:

```text
~/.config/ironbrain/mailcal.json
```

Override:

```bash
export IRONBRAIN_MAILCAL_CONFIG=/etc/ironbrain/mailcal.json
# or: ironbrain-mailcal --config /path/to/mailcal.json …
```

```bash
mkdir -p ~/.config/ironbrain
chmod 700 ~/.config/ironbrain
# create mailcal.json (examples below), then:
chmod 600 ~/.config/ironbrain/mailcal.json
```

### Schema (multi-account)

| Key | Purpose |
|-----|---------|
| `defaultAccount` | Name used when `--account` / `IRONBRAIN_MAILCAL_ACCOUNT` omitted |
| `accounts.<name>.Email.Smtp` | SMTP host/port/TLS/credentials/from; set **`Enabled`: false** to make the account send-read-only (default `true`) |
| `accounts.<name>.Email.Imap` | IMAP host/port/TLS/credentials/mailbox/SentFolder |
| `accounts.<name>.Calendar` | Optional CalDAV/`.ics` (`SourceUrl` may be empty) |

There is **no** Gmail/MS365 OAuth path — username/password (or app password) + host/port/TLS only.

### Migration from legacy single-account

Old shape still works (implicit account name `default`):

```json
{ "Email": { "Imap": {…}, "Smtp": {…} }, "Calendar": {…} }
```

Wrap it like this:

```json
{
  "defaultAccount": "assistant",
  "accounts": {
    "assistant": {
      "Email": { "Imap": {…}, "Smtp": {…} },
      "Calendar": {…}
    }
  }
}
```

Remove root `Email`/`Calendar` after migrating so env overlays stay predictable.

### Env overrides (optional secrets)

```bash
export IRONBRAIN_MAILCAL_ACCOUNT=assistant   # same as --account
export Email__Imap__Password='…'             # applied to the selected account
export Email__Smtp__Password='…'
export Calendar__Password='…'
# Per-account (multi only):
# export Accounts__assistant__Email__Imap__Password='…'
```

Also: `IRONBRAIN_MAILCAL_CONFIG` for the config file path.

---

## 3. Examples

### A) Multi-account Mailcow + SOGo

```json
{
  "defaultAccount": "assistant",
  "accounts": {
    "assistant": {
      "Email": {
        "Smtp": {
          "Enabled": true,
          "Host": "mail.example.com",
          "Port": 587,
          "UseStartTls": true,
          "UseSsl": false,
          "Username": "bot@example.com",
          "Password": "REPLACE_ME",
          "FromAddress": "bot@example.com",
          "FromName": "Assistant Bot"
        },
        "Imap": {
          "Host": "mail.example.com",
          "Port": 993,
          "UseSsl": true,
          "Username": "bot@example.com",
          "Password": "REPLACE_ME",
          "Mailbox": "INBOX",
          "SentFolder": "Sent"
        }
      },
      "Calendar": {
        "SourceUrl": "https://mail.example.com/SOGo/dav/bot@example.com/Calendar/personal/",
        "HomeUrl": "https://mail.example.com/SOGo/dav/bot@example.com/Calendar/",
        "Username": "bot@example.com",
        "Password": "REPLACE_ME",
        "PreferIcsFeed": false,
        "DefaultWriteCalendar": "personal",
        "IncludeSharedByDefault": true
      }
    },
    "privat": {
      "Email": {
        "Smtp": {
          "Enabled": false,
          "Host": "mail.example.com",
          "Port": 587,
          "UseStartTls": true,
          "UseSsl": false,
          "Username": "me@example.com",
          "Password": "REPLACE_ME",
          "FromAddress": "me@example.com",
          "FromName": "Private"
        },
        "Imap": {
          "Host": "mail.example.com",
          "Port": 993,
          "UseSsl": true,
          "Username": "me@example.com",
          "Password": "REPLACE_ME",
          "Mailbox": "INBOX",
          "SentFolder": "Sent"
        }
      },
      "Calendar": {
        "SourceUrl": "",
        "Username": "",
        "Password": "",
        "PreferIcsFeed": false
      }
    }
  }
}
```

`HomeUrl` (or a `SourceUrl` whose parent is the calendar home, e.g. `…/Calendar/personal/` → `…/Calendar/`) enables **multi-calendar discovery**. Optional explicit `Calendars: [{ "name", "url" }]` skips PROPFIND. `DefaultWriteCalendar` is the default target for `cal add` when `-c` is omitted. Shared calendars are included by default (`IncludeSharedByDefault`); use `--exclude-shared` / MCP `includeShared=false` to omit them.

**Send gate:** set `Email.Smtp.Enabled` to `false` on personal accounts so `mail send` / `mail reply` / MCP `send_email` fail clearly for that account (no silent fallback). Leave `true` (or omit — default) on the bot/assistant account that may send.

### B) Generic IMAP + CalDAV (e.g. host like `mail.example.com`)

```json
{
  "defaultAccount": "work",
  "accounts": {
    "work": {
      "Email": {
        "Smtp": {
          "Host": "mail.example.com",
          "Port": 587,
          "UseStartTls": true,
          "UseSsl": false,
          "Username": "bot@example.com",
          "Password": "REPLACE_ME",
          "FromAddress": "bot@example.com",
          "FromName": "Grok Bot"
        },
        "Imap": {
          "Host": "mail.example.com",
          "Port": 993,
          "UseSsl": true,
          "Username": "bot@example.com",
          "Password": "REPLACE_ME",
          "Mailbox": "INBOX",
          "SentFolder": "Sent"
        }
      },
      "Calendar": {
        "SourceUrl": "https://mail.example.com/dav/bot@example.com/calendar/",
        "Username": "bot@example.com",
        "Password": "REPLACE_ME",
        "PreferIcsFeed": false
      }
    }
  }
}
```

Adjust hostnames/ports to match the server. If you only have a published `.ics` URL (read-only), set `SourceUrl` to that URL and `"PreferIcsFeed": true`. `cal add` requires CalDAV write access.

Repo template: `Ironbrain.MailCal.Cli/appsettings.example.json`.

---

## 4. Smoke-test sequence (unattended)

```bash
export PATH="$PATH:$HOME/.dotnet/tools"

# Config discovery
ironbrain-mailcal config path
# expect: /home/<bot>/.config/ironbrain/mailcal.json

ironbrain-mailcal account list
ironbrain-mailcal account show assistant   # passwords redacted

# Mail (default account, or pass --account / -a)
ironbrain-mailcal mail list --limit 5
ironbrain-mailcal --account privat mail list --limit 5
ironbrain-mailcal mail get '<imap-unique-id-from-list>'

# Calendar (multi-calendar: discover, then list all or filter)
ironbrain-mailcal cal calendars
ironbrain-mailcal cal list --date today --range day
ironbrain-mailcal cal list --date today -c Family,personal
ironbrain-mailcal cal list --date today --range week
```

Optional write checks (only if the account may send / create events):

```bash
ironbrain-mailcal mail send --to you@example.com --subject "mailcal smoke" --body "ok"
ironbrain-mailcal cal add -c personal \
  --summary "mailcal smoke" \
  --start "2026-09-26T10:00:00+02:00" \
  --end "2026-09-26T10:15:00+02:00" \
  --tz Europe/Berlin
```

Exit code `0` and JSON/list output = success. Connection/auth errors print to stderr with non-zero exit.

---

## 5. Grok Bot / systemd sketch

Minimal unit (after tool + config exist):

```ini
[Unit]
Description=Ironbrain MailCal availability (optional oneshot smoke)
After=network-online.target

[Service]
Type=oneshot
User=bot
Environment=PATH=/home/bot/.dotnet/tools:/usr/bin
Environment=IRONBRAIN_MAILCAL_CONFIG=/home/bot/.config/ironbrain/mailcal.json
ExecStart=/home/bot/.dotnet/tools/ironbrain-mailcal mail list --limit 1
```

For MCP, run `ironbrain-mailcal-mcp` under your agent host’s MCP config with the same `IRONBRAIN_MAILCAL_CONFIG`.

---

## Related

- Design / command reference: [cli.md](cli.md)
- Platform email/calendar context: [#](#)
