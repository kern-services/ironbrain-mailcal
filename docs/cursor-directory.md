# cursor.directory submission checklist

Track review/status here (or open a GitHub issue and link it).

## Ready to submit

- [x] Public GitHub repo live at https://github.com/kern-services/ironbrain-mailcal
- [x] MIT LICENSE
- [x] Root README with product idea, CLI vs MCP, install via nuget.org
- [x] `.cursor-plugin/plugin.json` + root `plugin.json`
- [x] `mcp.json` wires `ironbrain-mailcal-mcp`
- [x] Install path documented: `dotnet tool install -g Ironbrain.MailCal.Mcp`
- [x] Sample config placeholders only
- [ ] Submitted at https://cursor.directory/plugins/new (paste repo URL)
- [ ] Listing review / status noted

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
