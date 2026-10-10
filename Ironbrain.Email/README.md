# Ironbrain.Email

IMAP/SMTP client library (MailKit) shared by **Ironbrain MailCal** CLI/MCP and platform hosts.

- List / get / send mail (`get` returns From, To, Cc, Message-ID)
- Save draft (IMAP APPEND with `\Draft`; From = IMAP identity)
- Move, archive (`Archive/{CurrentYear}` default), and trash
- Optional per-user / per-account options via `IUserEmailOptionsProvider`
- Configurable SMTP/IMAP timeouts and safe SASL mechanism preference

Repository: [kern-services/ironbrain-mailcal](https://github.com/kern-services/ironbrain-mailcal)

```bash
dotnet add package Ironbrain.Email
```
