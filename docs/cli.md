# MailCal CLI & MCP — bot-host mail + calendar against any IMAP / CalDAV

Standalone tools for **bot machines** (and Cursor MCP clients) to read and write
mail and calendars on **arbitrary self-hosted servers** — Mailcow, SOGo, Dovecot,
generic IMAP/SMTP, CalDAV, or a plain `.ics` feed — not only Gmail or Microsoft 365.

This implements the product direction for email/calendar on **your** infrastructure (self-hosted IMAP/SMTP/CalDAV).

## Projects

| Project | Role |
|---------|------|
| [Ironbrain.Calendar](../Ironbrain.Calendar/) | CalDAV / iCalendar library (`ICalendarService`) |
| [Ironbrain.Email](../Ironbrain.Email/) | MailKit IMAP/SMTP library |
| [Ironbrain.MailCal](../Ironbrain.MailCal/) | Shared multi-account config resolution (CLI + MCP) |
| [Ironbrain.MailCal.Cli](../Ironbrain.MailCal.Cli/) | `ironbrain-mailcal` CLI — scripts, cron, CI, bot hosts that shell out |
| [Ironbrain.MailCal.Mcp](../Ironbrain.MailCal.Mcp/) | `ironbrain-mailcal-mcp` — stdio MCP for Cursor / MCP clients |

Both packages share `~/.config/ironbrain/mailcal.json` and the SMTP send gate. Complementary, not competing. Product page: [ironbrain.de/mailcal](https://www.ironbrain.de/mailcal/).

## Install on a bot host

**Primary (unattended):** install from **nuget.org** (auth-free) — see **[install.md](install.md)** for Mailcow/`mail.example.com` config, env secrets, and smoke tests.

```bash
dotnet tool install -g Ironbrain.MailCal.Cli
# optional MCP companion:
dotnet tool install -g Ironbrain.MailCal.Mcp
```

Requires **.NET 10 SDK**. Package version **0.1.2**. GitHub Packages is an optional secondary feed (see [install.md](install.md)).

**From source** (dev):

```bash
./scripts/pack-mailcal.sh
dotnet tool install --global --add-source ./artifacts/nuget Ironbrain.MailCal.Cli
# or: dotnet run --project Ironbrain.MailCal.Cli -- --help
```
## Configuration (secrets stay local)

**Do not commit credentials.** Config is loaded from, in order:

1. `--config /path/to/mailcal.json`
2. `$IRONBRAIN_MAILCAL_CONFIG`
3. `~/.config/ironbrain/mailcal.json`
4. `./appsettings.json` (gitignored when named as in `.gitignore`)

### Multi-account (preferred)

```json
{
  "defaultAccount": "assistant",
  "accounts": {
    "assistant": {
      "Email": { "Smtp": { "...": "..." }, "Imap": { "...": "..." } },
      "Calendar": { "SourceUrl": "https://…/SOGo/dav/…/Calendar/personal/", "Username": "…", "Password": "REPLACE_ME" }
    },
    "privat": {
      "Email": { "Smtp": { "...": "..." }, "Imap": { "...": "..." } },
      "Calendar": { "SourceUrl": "", "Username": "", "Password": "" }
    }
  }
}
```

Select an account with `--account` / `-a`, or set `IRONBRAIN_MAILCAL_ACCOUNT`. If omitted: use `defaultAccount`, else the only account, else error when multiple accounts exist.

**SMTP send gate:** `Email.Smtp.Enabled` (default `true`). Set `false` on personal/read-only mailboxes; `mail send`, `mail reply`, and MCP `send_email` then fail with an explicit error for that account and do **not** fall back to another account.

### Legacy single-account (still supported)

A root `{ "Email": …, "Calendar": … }` file is treated as one implicit account named **`default`**. Prefer migrating to `accounts` (see [install.md](install.md#migration-from-legacy-single-account)).

### Environment overrides

| Variable | Effect |
|----------|--------|
| `IRONBRAIN_MAILCAL_CONFIG` | Path to config file |
| `IRONBRAIN_MAILCAL_ACCOUNT` | Default account name (same as `--account`) |
| `Email__Imap__Password`, `Email__Smtp__Password`, `Calendar__Password`, … | Applied to the **selected** account (handy for secrets). With multi-account JSON, remove leftover root `Email`/`Calendar` objects so they are not merged from file. |
| `Accounts__assistant__Email__Imap__Password` | Overrides one field on a named account (MS config nesting) |
| `IRONBRAIN_` prefix | Same keys with prefix stripped by the loader |

Copy the example and edit:

```bash
mkdir -p ~/.config/ironbrain
cp Ironbrain.MailCal.Cli/appsettings.example.json ~/.config/ironbrain/mailcal.json
chmod 600 ~/.config/ironbrain/mailcal.json
```

### Mailcow-oriented example (multi-account)

Mailcow typically exposes IMAP/SMTP on the mail hostname and calendars via **SOGo** CalDAV. Full template: `Ironbrain.MailCal.Cli/appsettings.example.json`.

- **CalDAV**: Prefer `Calendar:HomeUrl` = `…/SOGo/dav/user@domain/Calendar/` for multi-calendar discovery. Legacy `SourceUrl` pointing at a single collection (e.g. `…/Calendar/personal/`) still works — the parent `…/Calendar/` is inferred for discovery.
- Optional `Calendar:Calendars: [{ "name": "Family", "url": "…" }]` skips PROPFIND and uses that explicit list.
- `Calendar:DefaultWriteCalendar` (display name or collection id) is used by `cal add` when `-c` is omitted.
- `Calendar:IncludeSharedByDefault` (default `true`) controls whether shared/delegated collections are included when listing; override per call with `--include-shared` / `--exclude-shared`.
- For a **read-only `.ics` / webcal feed**, set `SourceUrl` to the feed URL (or `PreferIcsFeed: true`). Writes (`cal add`) require CalDAV.

## CLI commands

Global options: `--config <path>`, `--verbose`, `--account` / `-a <name>`.

### Accounts

```bash
ironbrain-mailcal account list
ironbrain-mailcal account show assistant
```

### Mail (IMAP / SMTP)

```bash
ironbrain-mailcal --account assistant mail list [--limit 20] [--offset 0] [--mailbox INBOX]
ironbrain-mailcal -a privat mail get <imap-unique-id>
ironbrain-mailcal mail send --to a@b.com --subject "Hi" --body "Hello"
ironbrain-mailcal mail send --to a@b.com --subject "Hi" --body-file ./msg.txt
ironbrain-mailcal mail reply <imap-unique-id> --body "Thanks"
```

`send` / `reply` use SMTP only when `Email.Smtp.Enabled` is true for the selected account (default). When `Enabled` is `false`, they exit with an error naming that account.

`send` also appends to the IMAP Sent folder when `Imap:SentFolder` is set (same behaviour as `Ironbrain.Email`).

### Calendar (CalDAV / iCal, multi-calendar)

```bash
# Discover collections under the calendar home (displayname, href/id, shared)
ironbrain-mailcal -a family-cal cal calendars
ironbrain-mailcal -a family-cal cal calendars --exclude-shared

# List appointments — default: ALL discovered calendars, merged & sorted by start
ironbrain-mailcal -a family-cal cal list --date today
ironbrain-mailcal -a family-cal cal list --date today --range week

# Filter by display name and/or collection path segment (repeatable or comma-separated)
ironbrain-mailcal -a family-cal cal list -c Family,personal
ironbrain-mailcal -a family-cal cal list --calendar personal --calendar Family

# Single-collection override (skips multi discovery)
ironbrain-mailcal cal list --url 'https://mail.example.com/SOGo/dav/u/Calendar/personal/' --date today

# Writes require an explicit calendar (or DefaultWriteCalendar / --url) — never all calendars
ironbrain-mailcal -a family-cal cal add -c personal \
  --summary "Follow-up" \
  --start "2026-09-26T10:00+02:00" \
  --end "2026-09-26T10:30+02:00" \
  --tz Europe/Berlin
```

Each appointment JSON includes `calendarName`, `calendarId`, and `calendarHref` when known. Mirror `.ics` / `.xml` hrefs are excluded from discovery. Shared/delegated SOGo collections (e.g. `alice_D_smith_A_example_D_com_personal` under the subscriber’s `…/Calendar/` home) are discovered via PROPFIND on the calendar home **with a trailing slash** and a non-empty `User-Agent` (`Ironbrain.MailCal/0.1` — SOGo omits delegated collections when UA is empty), and marked `isShared: true` when `DAV:owner` is another principal.

### Config helper

```bash
ironbrain-mailcal config path
```

## MCP server (optional companion)

Thin stdio MCP wrapper over the same libraries — for Cursor / agent hosts that speak MCP (not a replacement for the CLI):

```bash
dotnet tool install -g Ironbrain.MailCal.Mcp
export IRONBRAIN_MAILCAL_CONFIG=$HOME/.config/ironbrain/mailcal.json
ironbrain-mailcal-mcp
# or from source: dotnet run --project Ironbrain.MailCal.Mcp
```

Example Cursor MCP config snippet:

```json
{
  "mcpServers": {
    "ironbrain-mailcal": {
      "command": "ironbrain-mailcal-mcp",
      "env": {
        "IRONBRAIN_MAILCAL_CONFIG": "/home/bot/.config/ironbrain/mailcal.json"
      }
    }
  }
}
```

Tools: `list_accounts`, `list_calendars`, `list_emails`, `get_email`, `send_email`, `get_appointments_for_day`, `get_appointments_for_week`, `add_appointment` — each mail/calendar tool accepts optional `account`. Appointment tools accept optional `calendars` (comma-separated filters) and `includeShared`; `add_appointment` accepts `calendar` (name/id) and never writes to all calendars.

## MVP gaps (intentional)

- No CalDAV **update/delete** of existing events yet (add + list only); `Href` is reserved on the model for a later edit path.
- Mail **reply** does not set `In-Reply-To` / `References` headers yet (subject/`Re:` + original From only).
- No CardDAV / contacts tools in this package.
- MCP package version may track the official `ModelContextProtocol` NuGet line; pin a version that builds in your environment if needed.

## See also

- [install.md](install.md) — bot-host install, config templates, smoke tests
- [smoke-checklist.md](smoke-checklist.md) — quick verification against a real staging config
- Product page: [ironbrain.de/mailcal](https://www.ironbrain.de/mailcal/)
