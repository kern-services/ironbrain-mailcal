# Ironbrain.MailCal.Mcp (`ironbrain-mailcal-mcp`)

**stdio MCP server** exposing IMAP/SMTP + CalDAV tools for **Cursor** and other MCP clients. Same config file and SMTP send gate as the CLI.

Product: [ironbrain.de/mailcal](https://www.ironbrain.de/mailcal/)

## CLI vs MCP

| Package | Command | When to use |
|---------|---------|-------------|
| [Ironbrain.MailCal.Cli](https://www.nuget.org/packages/Ironbrain.MailCal.Cli) | `ironbrain-mailcal` | Scripts, cron, CI, bot hosts that shell out (e.g. Grok Bot) |
| **Ironbrain.MailCal.Mcp** (this package) | `ironbrain-mailcal-mcp` | Cursor / MCP clients over stdio |

Both share `~/.config/ironbrain/mailcal.json`. Complementary, not competing — install one or both.

## Install (primary: nuget.org)

Auth-free; no GitHub PAT required:

```bash
dotnet tool install -g Ironbrain.MailCal.Mcp
```

Requires the **.NET 10** SDK (or runtime + tool host). Ensure `~/.dotnet/tools` is on `PATH`.

```bash
export IRONBRAIN_MAILCAL_CONFIG="$HOME/.config/ironbrain/mailcal.json"
ironbrain-mailcal-mcp
```

## Cursor MCP snippet

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

Tools include `list_accounts`, `list_emails`, `get_email`, `send_email`, `list_calendars`, `get_appointments_for_day`, `get_appointments_for_week`, `add_appointment` (optional `account` on each).

Full install and config: [docs/install.md](https://github.com/kern-services/ironbrain-mailcal/blob/main/docs/install.md). Design reference: [docs/cli.md](https://github.com/kern-services/ironbrain-mailcal/blob/main/docs/cli.md).

## Advanced: GitHub Packages

Optional secondary feed (requires a PAT with `read:packages`). Prefer nuget.org for normal installs.
