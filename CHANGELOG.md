# Changelog

All notable changes to **Ironbrain MailCal** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- `EmailSendRequest` optional `Cc` / `Bcc`; `SendEmailAsync` wires them through MimeKit `AddAddresses` (same path as SaveDraft).

### Fixed

- Recurring occurrence `End` no longer collapses to `Start` when Ical.Net leaves `Period.EndTime` null.
  `ParseOccurrences` resolves end as occurrence period end → occurrence duration →
  `occurrenceStart + (master DTEND − DTSTART)` / master `DURATION` — never the master's absolute `DTEND`.
  Fixes zero-duration Freigabe snapshots (`beforeStart` == `beforeEnd`) and `list_appointments` for later RRULE instances.
- `list_emails` `since`/`before`: IMAP `SINCE`/`BEFORE` use the filter's UTC calendar day with no ±1 day widening
  (`since` inclusive, `before` exclusive — matches CLI/MCP docs).
- Bulk move/archive `MovedCount` / `Ids` report UIDPLUS-mapped source UIDs actually moved (not the requested count).

### Versions

- **Ironbrain.Email 0.1.7** / **Ironbrain.Calendar 0.1.7** (rebased on main after [#15](https://github.com/kern-services/ironbrain-mailcal/pull/15) libs **0.1.6**).

## [0.1.6] — 2026-10-20

### Fixed

- `SaveDraft` with `quoteOriginal`: text-only replies produce a real HTML part (`PlainTextToHtml`) before `<blockquote type="cite">` instead of a quote-only / empty reply HTML body.
- Quoted HTML uses only the original `<body>` inner content (strips doctype / html / head / meta / charset declarations; also strips meta/base/link/style chrome inside body).
- HTML-only originals: text quote via MimeKit `HtmlTokenizer` (Html → Plain; skips style/script/title/head) so `>` lines are not empty.

### Follow-up (not in this release)

- Sanitize quoted HTML (`script` / `iframe` / `object` / `embed` / `form` / `base` / `on*`) via MimeKit `HtmlToHtml` filter.

### Versions

- **Ironbrain.Email 0.1.6** / **Ironbrain.Calendar 0.1.6** / tools **0.1.6** (single `lib_version` for Trusted Publishing). Open [#13](https://github.com/kern-services/ironbrain-mailcal/pull/13) renumbers to **0.1.7**.

## [0.1.5] — 2026-10-20

### Added

- `list_emails` / `mail list`: server-side IMAP filters (`unread`, `since`/`before`, `fromContains`, `subjectContains`, folder via mailbox) returning `EmailListResult` with `totalCount`, `offset`, `limit`, `nextOffset`.
- `get_email`: exposes `InReplyTo` and `References` for threading verification; missing UID → `EmailNotFoundException` (hosts map to HTTP 404).
- Bulk IMAP move/archive: `MoveEmailsAsync` / `ArchiveEmailsAsync` (one IMAP session; archive groups by expanded folder). MCP/CLI accept comma-separated `uids`.
- `AddAppointment`: recurrence via `RecurrenceRule` (RRULE) or simple `RecurrenceFrequency` (`daily`/`weekly`) + `RecurrenceCount` / `RecurrenceUntil`.
- Default timezone **Europe/Berlin** when `timeZoneId` is omitted on add/update — never drops offsets into floating local time (`ResolveEventDateTimes`).

### Versions

- **Ironbrain.Email 0.1.4** / **Ironbrain.Calendar 0.1.4**. Tools **0.1.5**.

## [0.1.4] — 2026-10-20

### Added

- `ICalendarService.UpdateAppointmentAsync` / `DeleteAppointmentAsync` keyed by UID + calendar (CalDAV PUT/DELETE with If-Match/ETag; SEQUENCE bump on update).
- Recurring scopes: omit `recurrenceId` for whole series; set it for one occurrence (update → RECURRENCE-ID override; delete → EXDATE / cancel override).
- List results populate `Href`, `ETag`, `RecurrenceId`, `IsRecurring` so clients can target a single occurrence.
- CLI: `cal update` / `cal delete`; MCP: `update_appointment` / `delete_appointment`.

### Versions

- Libraries published together as **Ironbrain.Email 0.1.3** / **Ironbrain.Calendar 0.1.3** (single `lib_version`; the 0.1.2 Calendar bridge build predates the review fixes). Tools 0.1.4.

### Fixed (review)

- Update/delete no longer fall back to a non-matching resource when the CalDAV UID text-match (substring) returns no exact UID match.
- Occurrence RECURRENCE-ID / EXDATE for TZID masters converts the UTC occurrence start to the master zone's wall-clock time.

### Changed

- Shared lib package version **0.1.2** (`Ironbrain.Calendar`); tools **0.1.4**.

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

## [0.1.0] — 2026-10-01

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
