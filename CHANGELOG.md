# Changelog

All notable changes to **Ironbrain MailCal** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Shared-lib reconciliation for platform consumers: `TrashFolder` / `TrashEmailAsync`, SMTP/IMAP `TimeoutMs`, `MailKitSasl` safe-auth preference, optional `mailbox` / `accountId` on list/get/send/move/archive/trash, optional `messageDate` on archive.
- Calendar: optional `accountId` on `ListCalendarsAsync` and on query/add request models; `IUserCalendarOptionsProvider` account overload.
- `Ironbrain.Email` / `Ironbrain.Calendar` are packable NuGet libraries (`IsPackable=true`, version `0.1.0`).
- CI publish workflow `.github/workflows/publish-mailcal.yml` (pack tools + libs). nuget.org Trusted Publishing must be retargeted from private `ironbrain` to this repo before live publish.
- Unit tests for SASL preference and SMTP timeout budget.

### Changed

- Pack script also packs `Ironbrain.Email` / `Ironbrain.Calendar` (`LIB_VERSION`, default `0.1.0`).
- `ListCalendarsAsync` Cli/Mcp call sites use named `cancellationToken` after the new `accountId` parameter.

### Added (prior)

- IMAP `mail move <uid> --to <mailbox>` and `mail archive <uid>` (CLI + MCP `move_email` / `archive_email`).
- Per-account `Email:Imap:ArchiveFolder` with placeholders: `{CurrentYear}` (UTC now), `{YYYY}` / `{MM}` / `{YY}` / `{DD}` (message Date, else UTC now).
- Prefer IMAP UID MOVE; fallback COPY + `\Deleted` + EXPUNGE; create missing archive hierarchy when allowed.

### Changed (prior)

- Default `Email:Imap:ArchiveFolder` when unset or empty is `Archive/{CurrentYear}` so `mail archive` works without config (package version remains 0.1.2).

## [0.1.2] — 2026-09-26

### Changed

- Public extract to `kern-services/ironbrain-mailcal` (MIT).
- Package `RepositoryUrl` now points at this public repository.
- Sample config and test fixtures scrubbed of personal calendar names and private hostnames.
- Package READMEs link to this public repo's `docs/` (not the private monorepo).
- Public docs no longer reference private platform internals (Supabase / API / Kernel).

## [0.1.1] — 2026-09

### Added

- Initial nuget.org release of `Ironbrain.MailCal.Cli` and `Ironbrain.MailCal.Mcp`.
- Multi-account config (`accounts` / `-a`), SMTP send gate (`Email.Smtp.Enabled`).
- CalDAV multi-calendar discovery (SOGo-friendly), IMAP list/get, SMTP send/reply.

[0.1.2]: https://github.com/kern-services/ironbrain-mailcal/releases/tag/v0.1.2
[0.1.1]: https://www.nuget.org/packages/Ironbrain.MailCal.Cli/0.1.1
