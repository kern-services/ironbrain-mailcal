# cursor.directory submission checklist

Track review/status here (or open a GitHub issue and link it).

**Tracking issue:** https://github.com/kern-services/ironbrain-mailcal/issues/1

## Ready to submit

- [x] Public GitHub repo live at https://github.com/kern-services/ironbrain-mailcal
- [x] MIT LICENSE
- [x] Root README with product idea, CLI vs MCP, install via nuget.org
- [x] `.cursor-plugin/plugin.json` + root `plugin.json`
- [x] `mcp.json` wires `ironbrain-mailcal-mcp`
- [x] Install path documented: `dotnet tool install -g Ironbrain.MailCal.Mcp`
- [x] Sample config placeholders only
- [x] Submitted at https://cursor.directory/plugins/new (2026-09-27)
- [ ] Listing review / status noted (publicly live / searchable)

### Submission status (not publicly live yet)

- **Listing URL:** https://cursor.directory/plugins/ironbrain-mailcal
- **Status:** unpublished / pending security review (not publicly searchable yet)
- **Auto-detected MCP:** `ironbrain-mailcal-mcp`
- **Submitted:** 2026-09-27

## Submit steps

1. Ensure nuget tools install cleanly:
   ```bash
   dotnet tool install -g Ironbrain.MailCal.Cli
   dotnet tool install -g Ironbrain.MailCal.Mcp
   ```
2. Open https://cursor.directory/plugins/new
3. Sign in, paste `https://github.com/kern-services/ironbrain-mailcal`
4. Confirm auto-detected MCP / plugin components
5. Record submission date and review status in an issue titled `cursor.directory listing`
