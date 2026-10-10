# Changelog

All notable changes to **Ironbrain MailCal** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.3] — 2026-10-10

### Added

- `IEmailService.SaveDraftAsync` / `EmailSaveDraftRequest`: IMAP APPEND into Drafts with `\Draft` flag.
- Drafts folder resolution: request/config override → SPECIAL-USE `\Drafts` → `Drafts` / `Entwürfe` (create when missing).
- Draft From uses IMAP account identity (`ImapOptions.Username`), never SMTP send-as.
- Reply drafts: `ReplyToMessageId` + mailbox → In-Reply-To / References, default `Re:` subject, optional quote.
- `EmailContent.To` / `Cc` / `MessageId` on `GetEmailAsync`.
- `ImapOptions.DraftsFolder` per-account override.

### Changed

- Shared lib package version **0.1.3** (`Ironbrain.Email`).

## [0.1.2] — 2026-10-06

### Added

- `ForwardEmailAsync` optional `sourceAccountId` / `sendAccountId` (IMAP source vs SMTP send); legacy `accountId` still sets both.
- Actionable `FolderNotFound` errors include IMAP user/host, folder, and accountId hint.
- Separator-tolerant `GetExistingMailboxAsync` for Forward/Move/Archive/List/Get source folders (tries `/`↔`.` and walks personal namespace; **no** auto-create).

### Changed

- Default forward subject prefix prefers `Fwd:` (still accepts existing `Fw:` / `Fwd:`).
- Host `IUserEmailOptionsProvider` may return SMTP-only or IMAP-only options; MailKit merges with appsettings fallback per side.
- Shared lib package version **0.1.2** (`Ironbrain.Email`).

## [0.1.1] — 2026-10-06

### Added

- `EmailForwardRequest` + `IEmailService.ForwardEmailAsync`: IMAP fetch → classic `Fw:` MIME with optional note preface and original attachments → SMTP send (Sent append when configured).
- `EmailAttachmentInfo` on `EmailContent.Attachments` (file name / content type / size metadata; no binary in get responses).

### Changed

- Shared lib package version **0.1.1** (`Ironbrain.Email` / `Ironbrain.Calendar`).

## [0.1.3] — 2026-10-01

### Added

- Shared-lib reconciliation for platform consumers: `TrashFolder` / `TrashEmailAsync`, SMTP/IMAP `TimeoutMs`, `MailKitSasl` safe-auth preference, optional `mailbox` / `accountId` on list/get/send/move/archive/trash, optional `messageDate` on archive.
- Calendar: optional `accountId` on `ListCalendarsAsync` and on query/add request models; `IUserCalendarOptionsProvider` account overload.
- First NuGet packages for `Ironbrain.Email` / `Ironbrain.Calendar` (`0.1.0`).
- CI publish workflow `.github/workflows/publish-mailcal.yml` (pack tools + libs) via nuget.org Trusted Publishing on this repo.
- Unit tests for SASL preference and SMTP timeout budget.

### Changed

- Pack script also packs `Ironbrain.Email` / `Ironbrain.Calendar` (`LIB_VERSION`, default `0.1.0`).
- Cli/Mcp package version **0.1.3** (includes move/archive from 0.1.2 unreleased work).
- `ListCalendarsAsync` Cli/Mcp call sites use named `cancellationToken` after the new `accountId` parameter.

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
