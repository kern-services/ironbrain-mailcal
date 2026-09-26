# Ironbrain MailCal

**Self-hosted IMAP / SMTP / CalDAV for bots** — not Gmail or Outlook cloud APIs.

Product page: [ironbrain.de/mailcal](https://www.ironbrain.de/mailcal/)

Ironbrain MailCal lets agent hosts, cron jobs, and Cursor talk to **your** mail and calendar servers (Mailcow, SOGo, Dovecot, mox, generic IMAP/SMTP/CalDAV). Credentials stay on the bot host in a local JSON file. Brand pattern for future adapters: **Ironbrain \<Thing\>**.

## CLI vs MCP

| Package | Command | When to use |
|---------|---------|-------------|
| [Ironbrain.MailCal.Cli](https://www.nuget.org/packages/Ironbrain.MailCal.Cli) | `ironbrain-mailcal` | Scripts, cron, CI, bot hosts that shell out |
| [Ironbrain.MailCal.Mcp](https://www.nuget.org/packages/Ironbrain.MailCal.Mcp) | `ironbrain-mailcal-mcp` | Cursor and other **MCP** clients over stdio |

Both share `~/.config/ironbrain/mailcal.json` and the same SMTP send gate. Complementary, not competing — install one or both.

## Install (nuget.org)

Requires **.NET 10** SDK (or runtime + tool host). Ensure `~/.dotnet/tools` is on `PATH`.

```bash
dotnet tool install -g Ironbrain.MailCal.Cli
dotnet tool install -g Ironbrain.MailCal.Mcp   # optional MCP companion
```

```bash
mkdir -p ~/.config/ironbrain
cp samples/mailcal.example.json ~/.config/ironbrain/mailcal.json
chmod 600 ~/.config/ironbrain/mailcal.json
# edit placeholders: mail.example.com, bot@example.com, REPLACE_ME
```

```bash
ironbrain-mailcal account list
ironbrain-mailcal -a assistant mail list --limit 5
ironbrain-mailcal -a family-cal cal calendars
```

## Multi-account (`-a`)

Prefer an `accounts` map with `defaultAccount`. Select with `--account` / `-a` or `IRONBRAIN_MAILCAL_ACCOUNT`. Legacy single-account `{ "Email": …, "Calendar": … }` still works as account `default`.

## SMTP send gate

`Email.Smtp.Enabled` (default `true`). Set `false` on personal/read-only mailboxes. Then `mail send`, `mail reply`, and MCP `send_email` fail with an explicit error for that account and do **not** fall back to another account.

## Multi-calendar / SOGo

Prefer `Calendar:HomeUrl` = `…/SOGo/dav/user@domain/Calendar/` for discovery. Optional explicit `Calendar:Calendars` list. `DefaultWriteCalendar` is used by `cal add` when `-c` is omitted. Shared/delegated SOGo collections are discovered via PROPFIND (non-empty User-Agent required). See [docs/cli.md](docs/cli.md).

## Cursor community plugin

This repo is the Cursor community plugin scaffold:

- `.cursor-plugin/plugin.json` — Cursor plugin manifest
- `plugin.json` — Agent Plugins (open standard) manifest
- `mcp.json` — wires `ironbrain-mailcal-mcp`

**Prerequisite:** install the MCP .NET tool once:

```bash
dotnet tool install -g Ironbrain.MailCal.Mcp
export IRONBRAIN_MAILCAL_CONFIG="$HOME/.config/ironbrain/mailcal.json"
```

Then point Cursor at this repo / plugin, or merge the `mcp.json` snippet into your MCP settings. Submission checklist: [docs/cursor-directory.md](docs/cursor-directory.md).

## Build from source

```bash
dotnet build Ironbrain.MailCal.slnx
dotnet test Ironbrain.MailCal.slnx --filter "Category!=Live"
./scripts/pack-mailcal.sh   # optional local nupkgs
```

## Smoke checklist

See [docs/smoke-checklist.md](docs/smoke-checklist.md) — IMAP list/get, CalDAV discover/list; SMTP only when `Enabled`.

## Projects

| Project | Role |
|---------|------|
| `Ironbrain.Email` | MailKit IMAP/SMTP |
| `Ironbrain.Calendar` | CalDAV / iCalendar |
| `Ironbrain.MailCal` | Multi-account config resolution |
| `Ironbrain.MailCal.Cli` | `ironbrain-mailcal` global tool |
| `Ironbrain.MailCal.Mcp` | `ironbrain-mailcal-mcp` stdio MCP server |
| `*.Tests` | Unit tests |

## Security

- Sample config uses placeholders only (`mail.example.com`, `bot@example.com`, `REPLACE_ME`).
- Never commit real passwords, tokens, or private hostnames.
- Passwords are never logged or printed (`account show` redacts them).

## License

[MIT](LICENSE)

---

## Deutsch (Kurz)

**Ironbrain MailCal** ist CLI + MCP für **selbst gehostete** IMAP/SMTP/CalDAV-Server (kein Gmail/Outlook-API). Installation über nuget.org (`Ironbrain.MailCal.Cli` / `Ironbrain.MailCal.Mcp`), gemeinsame Config unter `~/.config/ironbrain/mailcal.json`, SMTP-Senden nur wenn `Email.Smtp.Enabled` true ist. Produktseite: [ironbrain.de/mailcal](https://www.ironbrain.de/mailcal/).
