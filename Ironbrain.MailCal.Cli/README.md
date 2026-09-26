# Ironbrain.MailCal.Cli (`ironbrain-mailcal`)

**CLI** for IMAP/SMTP mail and CalDAV/iCalendar against arbitrary self-hosted servers (Mailcow, SOGo, mox, Dovecot, …). Use this when a host **shells out** — scripts, cron, CI, or bot runners such as Grok Bot.

Product: [ironbrain.de/mailcal](https://www.ironbrain.de/mailcal/)

## CLI vs MCP

| Package | Command | When to use |
|---------|---------|-------------|
| **Ironbrain.MailCal.Cli** (this package) | `ironbrain-mailcal` | Scripts, cron, CI, bot hosts that invoke a process |
| [Ironbrain.MailCal.Mcp](https://www.nuget.org/packages/Ironbrain.MailCal.Mcp) | `ironbrain-mailcal-mcp` | Cursor and other **MCP** clients over stdio |

Both tools share `~/.config/ironbrain/mailcal.json` (and the same SMTP send gate). They are complementary, not competing — install one or both.

## Install (primary: nuget.org)

Auth-free; no GitHub PAT required:

```bash
dotnet tool install -g Ironbrain.MailCal.Cli
```

Requires the **.NET 10** SDK (or runtime + tool host). Ensure `~/.dotnet/tools` is on `PATH`.

```bash
ironbrain-mailcal --help
```

## Config

```bash
mkdir -p ~/.config/ironbrain
# create ~/.config/ironbrain/mailcal.json (multi-account Email + Calendar)
chmod 600 ~/.config/ironbrain/mailcal.json
```

```bash
ironbrain-mailcal account list
ironbrain-mailcal --account assistant mail list
ironbrain-mailcal cal list --date today
```

Full install, Mailcow/mox examples, and smoke tests: [docs/install.md](https://github.com/kern-services/ironbrain-mailcal/blob/main/docs/install.md). Design reference: [docs/cli.md](https://github.com/kern-services/ironbrain-mailcal/blob/main/docs/cli.md).

## Advanced: GitHub Packages

Optional secondary feed (requires a PAT with `read:packages`). Prefer nuget.org for normal installs.
