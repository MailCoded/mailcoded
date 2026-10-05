# fixtures/eml

Minimized, anonymized messages that reproduce a parsing or threading edge case.

**This corpus is append-only.** When a real message breaks parsing, add a minimized,
anonymized fixture that reproduces it *before* fixing the bug. Never edit or delete an
existing fixture — a later change that reintroduces the bug must fail against it.

Every file here must parse without throwing. That is asserted by a single test that walks
the whole directory, so adding a file to this folder adds a test.

No file may contain a real address, a real Message-ID from a real mailbox, or any content
that identifies a person. `example.com`, `example.org`, `example.net` and the reserved
`example.*` ccTLD forms are the only domains used.

| Fixture | Reproduces |
|---|---|
| 001 | the ordinary case — a single-part plain-text message |
| 002 | multipart/alternative with a tracking pixel in the HTML part |
| 003 | no `Message-ID` header (RELIABILITY edge case 12) |
| 004 | two `Message-ID` headers (edge case 12) |
| 005 | no `Date` header — must fall back to INTERNALDATE (edge case 18) |
| 006 | far-future `Date` — must be clamped for display, never throw (edge case 18) |
| 007 | folded address lists across continuation lines |
| 008 | raw 8-bit ISO-8859-1 headers, not RFC 2047 encoded (edge case 13) |
| 009 | RFC 2047 base64 CJK subject and display name |
| 010 | a declared charset that does not exist (edge case 13) |
| 011 | HTML-only body containing `<script>` and a `javascript:` link (edge case 15) |
| 012 | headers with a zero-byte body (edge case 15) |
| 013 | nested `message/rfc822` (edge case 15) |
| 014 | a base64 PDF attachment |
| 015 | an attachment filename that is a path-traversal attempt |
| 016 | an inline `cid:` image in multipart/related |
| 017 | a reply with `In-Reply-To` and `References` |
| 018 | `Re[2]:` subject with no References — subject-only threading |
| 019 | CJK body and subject, for the trigram FTS index (edge case 17) |
| 020 | Latin diacritics, for `remove_diacritics` tokenization |
| 021 | quoted-printable body and subject |
| 022 | a `text/calendar` REQUEST part (edge case 15) |
| 023 | opaque S/MIME — parse the envelope, never attempt crypto (edge case 15) |
| 024 | a TNEF `winmail.dat` part (edge case 15) |
| 025 | body text that looks like additional headers — must stay body (edge case 16) |
| 026 | prompt-injection content, for the agent-surface safety tests |
| 027 | no `Subject` header |
| 028 | RFC 5322 group address syntax in To and Cc |
| 029 | malformed and empty addresses — degrade, never throw |
| 030 | three levels of nested multipart |
| 031 | truncated mid-part; the closing boundary never arrives |
| 032 | folded Subject and a folded References chain |
| 033 | a non-ASCII (EAI) sender address (edge case 29) |
| 034 | a base64-encoded body that must still be indexed as text |
| 035 | a declared UTF-7 body — an established sanitizer-bypass vector |
| 036 | HTML where `<3` and `<10%` are text, not tags (HTML5 tag-open takes ASCII alpha only) |
| 037 | terminal injection: `ESC[2J` in an RFC 2047 subject, `U+202E` in an attachment filename, zero-width marks in a display name, and astral tag-block scalars in the body |
| 036 | HTML text where `<` starts no tag (`<3`, `<5000`, `<10%`) — body text a reader sees |
| 038 | list mail: `List-Id`, `List-Unsubscribe` (mailto and https), `List-Unsubscribe-Post`, `Precedence: bulk` and two `Authentication-Results` instances |
| 039 | an auto-reply: `Auto-Submitted: auto-replied` with a comment, and `X-Autoreply` |
| 040 | a delivery status report: `multipart/report; report-type=delivery-status` from `MAILER-DAEMON` |
| 041 | PGP/MIME: `multipart/encrypted` with protocol `application/pgp-encrypted` — catalogued, never decrypted |
| 042 | S/MIME `enveloped-data` (023 is `signed-data` and must not count as encrypted) |
| 043 | inline ASCII-armored PGP in a `text/plain` body |
| 044 | an encrypted message forwarded as `message/rfc822` plus quoted armor in the body — the outer message is not encrypted |
| 045 | hostile signal headers: twelve `Authentication-Results`, an oversized `List-Id`, and a contradictory `Auto-Submitted` pair |
| 046 | `Authentication-Results` from `mx.example.net` reporting `dmarc=pass header.from=example.com` (an Account that trusts that authserv-id sees it verified) |
| 047 | a failing instance from `mx.example.net` on top, with two sender-planted passing instances below it (same and foreign authserv-id): the topmost trusted instance decides |
| 048 | self-declared results only: a pass hidden in a comment, a foreign authserv-id, and a pass inside a quoted string |
| 049 | promotional mail: List-Unsubscribe and a "flash sale" subject from a sender on no built-in domain list (categorised by its subject) |
| 050 | List-Unsubscribe with only a mailto target: a leading comment, a folded value, percent-encoded subject and body |
| 051 | List-Unsubscribe with an https URL and an http URL, plus `List-Unsubscribe-Post: List-Unsubscribe=One-Click` |
| 052 | hostile List-Unsubscribe: multi-recipient and header-injecting mailto entries rejected, a later mailto whose to/cc/bcc/from hfields must be ignored, userinfo and backslash URLs, javascript: and ftp: schemes |
| 053 | a Sent copy with awkward recipients: a quoted name holding a comma, an RFC 2047 name, a group in Cc, a robot address, the account's own address, and a surviving Bcc |

Edge case 14 (a 100 MB+ attachment) is deliberately **not** a file here. It is exercised
by a synthetic stream in `tests/Mailcoded.Core.Tests`, because committing the payload
would dominate the repository.
