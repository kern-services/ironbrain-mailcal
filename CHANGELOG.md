# Changelog

All notable changes to **Ironbrain MailCal** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.14] — 2026-10-28

### Fixed

- Missing IMAP mailbox no longer surfaces as an unhandled MailKit `FolderNotFoundException`
  (500). `GetExistingMailboxAsync` throws `MailboxNotFoundException` with
  `Mailbox '{name}' was not found.`
- Case-insensitive folder fallback when walking the personal namespace (e.g. `introbox` →
  `Introbox`) for `list_emails` / `get_email` / move / archive / forward / save-draft
  source folders.

### Tests

- `ResolveFolderName`: exact match, different-case resolve, truly missing → null;
  `MailboxNotFoundException` message.

### Versions

- **Ironbrain.Email 0.1.14** / **Ironbrain.Calendar 0.1.14** / tools (**Cli** / **Mcp**) **0.1.14**

## [0.1.13] — 2026-10-28

### Fixed

- Offset-less event start/end strings are interpreted as wall-clock local time in `timeZoneId`
  (default **Europe/Berlin**). `DateTimeOffset.Parse` / JSON previously attached `+00:00` on UTC hosts,
  so `ResolveEventDateTimes` shifted Europe/Berlin times by one hour.
- New `TryParseEventDateTimeOffset` / `ParseEventDateTimeOffset` / `HasExplicitTimeZoneOffset`.
  MailCal MCP `add_appointment` / `update_appointment` use them before resolving local times.
- Explicit offsets and `Z` still convert to the event time zone (absolute instant preserved).

### Versions

- **Ironbrain.Email 0.1.13** / **Ironbrain.Calendar 0.1.13** / tools (**Cli** / **Mcp**) **0.1.13**

## [0.1.12] — 2026-10-21

### Added

- All-day events: `CalendarAddRequest.AllDay` / `CalendarUpdateRequest.AllDay` write
  `DTSTART;VALUE=DATE` / `DTEND;VALUE=DATE` (exclusive end). `CalendarAppointment.AllDay`
  is set when listing; start/end are calendar dates at midnight UTC.
- `CalDavCalendarService.ParseAllDayDate` — accepts `yyyy-MM-dd` only (rejects times).
- MailCal MCP `add_appointment` / `update_appointment`: optional `allDay` bool.

### Fixed

- Add-appointment success message no longer drops the end date on multi-day ranges
  (`yyyy-MM-dd HH:mm – yyyy-MM-dd HH:mm` / all-day inclusive display).

### Versions

- **Ironbrain.Email 0.1.12** / **Ironbrain.Calendar 0.1.12** / tools (**Cli** / **Mcp**) **0.1.12**

## [0.1.11] — 2026-10-21

### Fixed

- `list_emails` exact since/before filter: messages with neither INTERNALDATE nor Envelope.Date no longer
  match when any bound is set (0.1.10 treated missing INTERNALDATE as `DateTimeOffset.MinValue`, which
  incorrectly passed every `before` filter). Prefer Envelope.Date when INTERNALDATE is absent.

### Docs

- Exact since/before filtering uses INTERNALDATE, falling back to Envelope.Date; if both are missing the
  message does **not** match any `since` or `before` bound (`EmailListFilter` / `PassesExactTimeFilter`).

### Tests

- Missing date excluded for since-only, before-only, and both; Envelope.Date fallback;
  `ListTimeFilterFixture_BeforeUtcMidnight_…` rename (was misnamed SinceUtcMidnight).

### Versions

- **Ironbrain.Email 0.1.11** / **Ironbrain.Calendar 0.1.11** / tools (**Cli** / **Mcp**) **0.1.11**

## [0.1.10] — 2026-10-21

### Fixed

- `list_emails` / `ListInboxAsync`: coarse IMAP `SINCE`/`BEFORE` is widened to `SINCE (utcDate − 1)`
  and `BEFORE (utcDate + 2)` so servers that compare calendar days in the message timezone (not UTC)
  do not drop same-day or near-boundary candidates before the exact INTERNALDATE filter runs.
  Example: `before=2026-10-21T10:40+02:00` must not SEARCH `BEFORE 21-Oct` (that dropped 10:27 mail).
  Exact inclusive `since` / exclusive `before` still apply after fetch; pagination uses the refined set.

### Docs

- Missing INTERNALDATE is treated as `DateTimeOffset.MinValue` in the exact-time filter: excluded by any
  `since` bound, included by any practical `before` bound (`EmailListFilter` / `PassesExactTimeFilter`).
  Superseded by **0.1.11**.

### Tests

- Search-window widening (`SINCE date−1` / `BEFORE date+2`); `before=…T10:40+02:00` searches past 21-Oct;
  fixture pipelines combining widened SEARCH + exact-time filter; missing INTERNALDATE handling.

### Versions

- **Ironbrain.Email 0.1.10** / **Ironbrain.Calendar 0.1.10** / tools (**Cli** / **Mcp**) **0.1.10**
  ([#18](https://github.com/kern-services/ironbrain-mailcal/pull/18)).

## [0.1.9] — 2026-10-21

### Fixed

- `list_emails` / `ListInboxAsync`: after IMAP calendar-day `SINCE`/`BEFORE`, refine matches by
  INTERNALDATE so ISO-8601 timestamps work (e.g. `since=2026-10-21T10:40+02:00` excludes 10:27).
  Inclusive `since` / exclusive `before`; pagination `totalCount` / `nextOffset` use the refined set.

### Tests

- `PassesExactTimeFilter` for same-day earlier exclusion and exclusive `before` bound.

### Versions

- **Ironbrain.Email 0.1.9** / **Ironbrain.Calendar 0.1.9** / tools (**Cli** / **Mcp**) **0.1.9**

## [0.1.8] — 2026-10-21

### Changed

- `ApplyReplyHeaders` (SaveDraft replies): prior `References` tokens are copied **byte-for-byte**
  (including Exchange `[id@microsoft.com]` — MimeKit#912). `NormalizeMessageId` strips only outer
  RFC `<…>`, never `[]`, so wire headers are written as `In-Reply-To: <[id]>` / `References: <[id]>`.
  Append-if-missing still uses **case-insensitive** Message-Id equality so a differently-cased
  duplicate is not added twice. `get_email` `MessageId` / Models docs unchanged (MimeKit tokens
  without outer `<>`).

### Tests

- Write/reparse Exchange `<[id]>` / bare `[id]`; prior bracketed References byte-for-byte;
  case-insensitive duplicate suppression; long References folding (≤998, order kept);
  MimeKit drops bare `[id]` References without `<>`.

### Versions

- **Ironbrain.Email 0.1.8** / **Ironbrain.Calendar 0.1.8** / tools (**Cli** / **Mcp**) **0.1.8**
  ([#16](https://github.com/kern-services/ironbrain-mailcal/pull/16)).

## [0.1.7] — 2026-10-21

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

- **Ironbrain.Email 0.1.7** / **Ironbrain.Calendar 0.1.7** / tools (**Cli** / **Mcp**) **0.1.7**
  ([#13](https://github.com/kern-services/ironbrain-mailcal/pull/13)).

## [0.1.6] — 2026-10-20

### Fixed

- `SaveDraft` with `quoteOriginal`: text-only replies produce a real HTML part (`PlainTextToHtml`) before `<blockquote type="cite">` instead of a quote-only / empty reply HTML body.
- Quoted HTML uses only the original `<body>` inner content (strips doctype / html / head / meta / charset declarations; also strips meta/base/link/style chrome inside body).
- HTML-only originals: text quote via MimeKit `HtmlTokenizer` (Html → Plain; skips style/script/title/head) so `>` lines are not empty.

### Follow-up (not in this release)

- Sanitize quoted HTML (`script` / `iframe` / `object` / `embed` / `form` / `base` / `on*`) via MimeKit `HtmlToHtml` filter.

### Versions

- **Ironbrain.Email 0.1.6** / **Ironbrain.Calendar 0.1.6** / tools **0.1.6** (single `lib_version` for Trusted Publishing). [#13](https://github.com/kern-services/ironbrain-mailcal/pull/13) renumbered to **0.1.7**.

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
