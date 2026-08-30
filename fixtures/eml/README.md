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

Edge case 14 (a 100 MB+ attachment) is deliberately **not** a file here. It is exercised
by a synthetic stream in `tests/Mailcoded.Core.Tests`, because committing the payload
would dominate the repository.
