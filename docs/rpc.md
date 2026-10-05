# RPC — the mailcoded JSON-RPC 2.0 surface

**Status:** normative for protocol version **1**. Generated from `src/Mailcoded.Protocol/`
and `src/Mailcoded.Daemon/`. SPEC §5.6 defines the surface; this file is the reference a client
author implements against.

The daemon is `mailcoded-daemon`. It speaks JSON-RPC 2.0 over **stdio** with LSP-style
`Content-Length` framing, so `vscode-jsonrpc` works unmodified.

```bash
mailcoded-daemon --store /path/to/store.db
```

| Flag | Meaning |
|---|---|
| `--store <path>` | explicit path to `store.db`; otherwise the per-OS default root is used |
| `--log-level <off\|error\|warn\|info\|debug\|trace>` | stderr verbosity (default `info`) |
| `--one-shot <method> --params '<json>'` | run one method, print one response, exit |
| `--transcript` | deterministic output for golden-file tests; never use in production |
| `--version`, `--help` | print and exit |

**stdout carries framed JSON-RPC and nothing else.** All logging goes to stderr. A client that
writes anything unframed into the daemon's stdin desynchronizes the stream permanently.

---

## 1. Framing

Every message — request, response, and notification — is one frame:

```
Content-Length: <decimal byte count of the body>\r\n
\r\n
<body: UTF-8 JSON, exactly that many bytes>
```

Rules the daemon actually enforces (`ContentLengthFraming.cs`):

- Header lines are `\r\n`-separated and the header block ends with `\r\n\r\n`.
- The header name is matched **case-insensitively** (`content-length`, `Content-Length`).
- `Content-Length` is required, must be a non-negative decimal integer with no trailing
  characters, and must be **greater than zero**.
- The header block is capped at **8 KiB**; the body is capped at **32 MiB** (`33554432`). An
  oversized `Content-Length` is rejected *before* any buffer is allocated for it.
- Any other header is ignored. There is no `Content-Type` requirement.
- The length is a **byte** count, not a character count. Compute it on the UTF-8 encoding.

On a malformed frame the daemon replies once with error `-32700` (id `null`) and then **stops
reading**: framing is a stream property, and resynchronizing on garbage is worse than closing.
The client should treat that response as fatal and restart the daemon.

### 1.1 Worked example a client can copy

Request bytes written to the daemon's stdin (`␍␊` shown for the CRLFs; the body is 144 bytes):

```
Content-Length: 144␍␊
␍␊
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientName":"my-client","clientVersion":"0.1.0","protocolVersion":1,"interface":"rpc"}}
```

A response frame read back from the daemon's stdout looks like this (body 134 bytes; the real
`capabilities` object is larger — see §3):

```
Content-Length: 134␍␊
␍␊
{"jsonrpc":"2.0","id":1,"result":{"daemonVersion":"0.1.0","protocolVersion":1,"capabilities":{"methods":["initialize"],"send":false}}}
```

A second request on the same pipe, framed the same way (body 92 bytes):

```
Content-Length: 92␍␊
␍␊
{"jsonrpc":"2.0","id":2,"method":"search","params":{"query":"from:acme invoice","limit":20}}
```

Reference writer, in any language: serialize to UTF-8 bytes, write
`"Content-Length: " + bytes.length + "\r\n\r\n"` as ASCII, write the bytes, flush.

### 1.2 Envelope rules

- `jsonrpc` must be exactly `"2.0"`. Anything else is `-32600`.
- `id` may be a number or a string and is **echoed back verbatim**. A request with no `id` is a
  notification: the daemon executes it and sends no response, not even on failure (the failure is
  logged to stderr instead).
- **Batches are not supported.** A JSON array payload fails to parse as a request object and is
  answered with `-32700`.
- Handlers run off the read loop, so **responses may arrive out of order**. Correlate strictly by
  `id`; never assume FIFO. A minutes-long `sync` does not block a concurrent `shutdown`.
- Server-to-client notifications (§5) interleave with responses on the same stream and carry no
  `id`.
- Every property name on the wire is `camelCase`. Null-valued properties are omitted, so a client
  must treat "absent" and "null" identically.
- All timestamps are ISO 8601 UTC with a `Z` suffix and millisecond precision:
  `2026-08-30T12:34:56.000Z`.
- 64-bit unsigned values that exceed JavaScript's safe integer range (MODSEQ) travel as **decimal
  strings**, never as JSON numbers.

---

## 2. Session lifecycle

1. Spawn the daemon with `--store`.
2. Send `initialize` and read the capabilities.
3. Optionally `watch.subscribe` to start receiving notifications (`sync` with `progress: true` sends
   `notify.sync.progress` without it).
4. Call methods.
5. Send `shutdown`, read the response, then close stdin and wait for exit.

`initialize` is expected first and sets the caller identity used for the audit trail and the
safety gates. It is **not** enforced as a precondition: a client that skips it is treated as
`interface: "rpc"`. Send it anyway — capabilities are how you learn what this build will do.

`shutdown` returns `{}` before the daemon drains in-flight handlers and exits, so the response is
never lost. Closing stdin without `shutdown` also terminates the daemon, less gracefully.

---

## 3. Capability negotiation

`initialize` params:

| Field | Type | Notes |
|---|---|---|
| `clientName` | string, **required** | free text, recorded in the audit trail |
| `clientVersion` | string, **required** | free text |
| `protocolVersion` | int | defaults to `1` |
| `interface` | string | `rpc` (default), `cli`, or `mcp`. Any other value → `-32602`. `internal` is the daemon's own retry loop and a client may never claim it. |
| `agentHost` | string | best-effort agent host label for `sync_log.agent_host` |

Result:

```json
{
  "daemonVersion": "0.1.0",
  "protocolVersion": 1,
  "capabilities": {
    "methods": ["initialize", "secret.set", "..."],
    "notifications": ["notify.mail.added", "notify.folder.updated", "notify.sync.error", "notify.sync.progress"],
    "providers": ["imap"],
    "search": true,
    "threading": true,
    "attachments": true,
    "watch": true,
    "send": false,
    "rawSql": false,
    "backup": true,
    "htmlBodies": true,
    "semantic": false,
    "maxSearchLimit": 200,
    "secretBackend": "libsecret"
  }
}
```

**The contract:**

- A **version mismatch is not fatal.** The daemon logs a warning to stderr and answers with the
  version it actually speaks. The client compares `result.protocolVersion` against its own and
  decides: equal → proceed; different → refuse to drive the daemon rather than guessing. Protocol
  version is bumped only on a breaking DTO change.
- **`methods` is the authoritative method list for this build.** Do not call a method that is
  absent; it will answer `-32601`.
- **An absent capability is an absent feature, not a toggle to flip.** There is no way to turn one
  on over the wire. `send`, `rawSql`, `backup` and `htmlBodies` reflect Core-side gates evaluated
  for *this caller's `interface`*, so the same daemon returns different values to a `cli` caller than
  to an `rpc` one.
- `send: false` means `send` will answer **1006**, not that the method is missing. Ask for a
  `send.preview` anyway if you want to show the user why.
- `backup: false` means `store.backup` and `store.backupVerify` will answer **1006**. It is `true`
  for `rpc`, always `false` for `mcp`, and for `cli` only when the daemon was started with
  `MAILCODED_ALLOW_BACKUP=1`.
- `htmlBodies: false` means `message.get` with `format: "html"` or `"raw"` answers **1006**;
  agent interfaces (`cli`, `mcp`) get plaintext only.
- `semantic: false` means no embedding model is installed. `search` still works; it just ranks by
  words alone. Asking for `semantic: true` then answers **1008**, because a search that silently
  ignored the request would be a worse answer than a refusal.
- `watch: false` means this process is not the store owner: it opens no IDLE connection and sends no
  `notify.mail.added` or `notify.folder.updated`. A `sync` it runs with `progress: true` still reports
  `notify.sync.progress`.
- `maxSearchLimit` (200) is a **clamp, not a validation error**: a larger `limit` is silently
  reduced. Page with `nextCursor`.
- `secretBackend` is the name of the active secret backend (`libsecret`, `keychain`, `wincred`,
  `file`). It is a backend name and never a secret.

---

## 4. Methods

`params` must be a JSON object for every method; a missing, `null`, or non-object `params` is
`-32602`. Methods whose params are `{}` still require the empty object. `capabilities.methods` from
`initialize` is the authoritative list for a given build.

### `initialize`
See §3.

### `secret.set`
`{ "ref": string, "value": string }` → `{}`

The only inbound DTO that carries a credential. The value goes straight to the OS keyring (or the
encrypted-file vault) under `ref`; it is never logged, echoed, persisted in the database or in
config, and never appears in an error message. Register the secret **before** `account.add`.

### `account.add`
```json
{
  "email": "me@example.com",
  "displayName": "Me",
  "provider": "imap",
  "imap": { "host": "imap.example.com", "port": 993, "security": "sslOnConnect",
            "username": "me@example.com", "watchFolders": ["INBOX"] },
  "smtp": { "host": "smtp.example.com", "port": 587, "security": "startTls",
            "username": "me@example.com" },
  "auth": { "kind": "password" },
  "secretRef": "mailcoded:me@example.com",
  "verify": true
}
```
→ `{ "accountId": 1, "verified": true, "folders": 12 }`

`verify` (default `false`) opens one IMAP session with the credential under `secretRef` and lists
folders **before** anything is written. On failure the call answers **1000** or **1001** and no
account is created, so a mistyped app password leaves nothing to clean up — which matters because
there is no method that removes an account. It costs one network round trip; omit it when the
settings are already known to work.

The credential stays under its `secretRef` after a failure. It is not deleted, because a ref may be
shared with an account that is working; the next `secret.set` with the same ref overwrites it.

`provider` ∈ `imap | graph | jmap | gmail`; only `imap` is implemented in v0.1 and anything else
answers **1008**. `security` ∈ `none | sslOnConnect | startTls | startTlsWhenAvailable`.
`auth.kind` ∈ `password | oauth2`. `secretRef` is required and must already exist — passing a raw
credential here is impossible by design.

### `account.list`
`{}` → `{ "accounts": [AccountDto] }`

`AccountDto` carries `secretRef` (an opaque handle) and `quirks` (latched server-quirk names for
diagnostics). It never carries a credential. It also lists what the account may send as and with:
`identities` (`[{ address, displayName?, replyTo? }]`, besides `email`, which is always allowed),
`signatures` (`[{ name, text }]`, plain text without the `-- ` separator line) and
`defaultSignatureNew` / `defaultSignatureReply`, the names of the signatures a new message and a
reply start with (absent when none is set). There is no method that changes them: a human does that
with `mailcoded account identity` and `mailcoded account signature`, and the daemon reads the
current values on every call. An older daemon sends none of these fields; treat them as absent.

`saveDraftsToServer` is true when the account also appends each draft to its server's Drafts
Folder (see `send.preview`). It is off unless the user turned it on with
`mailcoded account server-drafts`; no RPC method changes it.

### `folder.list`
`{ "accountId": 1 }` → `{ "folders": [FolderDto] }`

`FolderDto`: `{ id, accountId, name, role?, unread, total, delimiter?, leaf?, depth, parentId? }`.
`role` ∈ `inbox | sent | drafts | trash | archive | junk | all`, or absent when the
folder has no special use. Counts are the locally stored ones — they reflect the last sync, not
the server this instant.

**Hierarchy.** `name` always puts `/` between levels, whatever delimiter the server uses (a Courier
or Cyrus-style server that lists `INBOX.Work.Q1` arrives as `INBOX/Work/Q1`). `delimiter` is the
server's own character, absent when the server has no hierarchy (a flat namespace, where `/` inside
`name` is part of the name and `depth` is 0). `leaf` is the last level of `name`; `depth` is the
number of levels above it; `parentId` is the nearest ancestor that is itself a folder, absent at
the top and when no ancestor is a folder. A parent the server never listed as a selectable
folder (a `\Noselect` or `\NonExistent` entry, or one only implied by a child's name) is not a
folder here: it has no id and no row, but its level counts in `depth` and its name is a prefix of
its children's `name`. A client that wants a node for it builds one from the prefix.
`leaf`, `depth` and `parentId` are additive: a daemon from before they existed omits `leaf`, and a
client falls back to splitting `name` on `/`.

**Order.** The array is a tree, depth first. Siblings are ordered special-use folders first
(inbox, drafts, sent, trash, junk, archive, all), then by name ignoring case, then by case. A
folder is followed by its descendants, so a client can indent by `depth` without sorting. At most
32 levels are recognised: deeper names keep the rest of the name in their last level.

A folder's delimiter is learned from the server's LIST and stored with it; folders stored before
that was recorded read as `/` until the next folder list refreshes them. A server name that contains
a literal `/` under another delimiter cannot be mapped to `name` and is not listed.

Unknown `accountId` → **1002**.

### `correspondent.suggest`
`{ "accountId": 1, "prefix": "ad"?, "limit": 10? }` → `{ "correspondents": [CorrespondentDto] }`

People the account's user has written to, best first, for completing a recipient field.
`CorrespondentDto`: `{ address, displayName?, count, lastUsedUtc }`. `address` is the whole address,
lower-cased. `displayName` is the name on the most recent message that carried one; it is untrusted
text, so render it as plain text. `count` is how many outgoing messages named the address in To or Cc.

`prefix` matches case-insensitively when it begins the address, begins the display name, or begins
any word of the name; absent or empty lists the best overall. Order is an exact address match first,
then frequency weighted by recency (a recent use counts for more, and the weight decays with a
90-day half-life toward a floor, so a long-standing Correspondent still outranks a one-off). `limit` is
`1..100`, default 10; outside that range → **-32602**. Unknown `accountId` → **1002**.

Learned from outgoing mail only, so received mail never adds anyone: every message mailcoded sends
(counted once, when the outbox row is marked sent, however many retries or crash reconciliations it
took) and every message that appears in a Sent-role folder whose From is the account address or one of
its identities. Bcc is not counted. The account's own addresses, robot senders (`noreply`,
`mailer-daemon`, ...) and messages to more than 25 distinct To and Cc addresses are skipped. Sent mail
that was already stored when the daemon first opened a store with this feature is counted once, at
that open. `account forget` removes the account's list with the account.

### `sync`
`{ "accountId": 1, "folderId": 12, "progress": true }` → `{ "added": 0, "updated": 0, "expunged": 0, "durationMs": 0 }`

Omit `folderId` to sync the whole account. Opens a network connection. Idempotent: re-running
after an interruption replays safely. A `folderId` that belongs to another account → **1002**.

`progress` is optional and defaults to `false`. With `progress: true` the daemon sends
`notify.sync.progress` while the sync runs: every event precedes the response, and every indicator
it opened gets a `final` event on success and on failure. A request that fails before the sync
starts (unknown account or folder, no connection) sends none. An older daemon ignores the field, so
check `capabilities.notifications` for `notify.sync.progress` before relying on it.

### `search`
```json
{ "query": "from:acme invoice", "limit": 50, "cursor": null,
  "accountId": null, "folderId": null, "order": "relevance", "includeSnippet": true,
  "semantic": null }
```
→ `{ "hits": [EnvelopeDto], "nextCursor": "…", "truncated": false, "relaxed": false,
   "semantic": false }`

Local only; never touches the network. Query syntax: bare words (full text over subject, sender,
recipients, body), `"quoted phrases"`, `from:`, `to:`, `cc:`, `subject:`, `tag:`, `folder:`/`in:`,
`is:unread|flagged|draft|replied`, `is:automated`, `is:encrypted`, `is:verified`, `has:attachment`,
`attachment:`, `before:YYYY-MM-DD`, `after:YYYY-MM-DD`, and `-` negation.

**Attachment text.** `attachment:word`, `attachment:"two words"` and `attachment:pre*` search the text
of attachments, and **only** that: bare words never match attachment text, and `attachment:` never
matches a body. The text comes from text-like attachments (`text/plain`, `text/csv`,
`text/tab-separated-values`, `text/markdown`, `text/calendar`, `application/json`, `text/html`, or
`application/octet-stream` named `.txt`, `.csv`, `.tsv`, `.md`, `.markdown`, `.log`, `.json` or
`.ics`) and Word `.docx` files (the body, footnotes and endnotes; not headers, footers, comments or
tracked deletions). PDF, spreadsheets, presentations, images, legacy `.doc` and archives are not
read. Text is read when a body is fetched or imported and, for bodies fetched earlier, by a
background pass in the daemon (and for up to five seconds at the end of each `mailcoded sync`), so an
older attachment becomes searchable some time after the upgrade. Each `attachment:` term is its own
condition and may be satisfied by a different attachment; the same attachment is only required
inside one quoted phrase. A message qualifies when any attachment holds the term, `-attachment:` when none does. A query
with `attachment:` and bare words ranks by the bare words; with `attachment:` alone, results page by
date. CJK follows the body rules (three or more characters by trigram, one or two by `LIKE`, which scans
every stored attachment text). The semantic ranking is not fused into a query that uses `attachment:`.

**Which Folders a search covers.** By default a search leaves out every Message whose Folder has
`role` `trash` or `junk`, in every Account. Three things lift that, and any one is enough:
a **positive** `folder:`/`in:` predicate (`in:trash`, `folder:Spam`, `in:inbox`, whether it names the
Folder by path, leaf name or role), the literal `in:anywhere` (or `folder:anywhere`), which searches
every Folder, or a request that carries `folderId`. A negated predicate (`-in:inbox`) only rules a
Folder out and does not lift the default; `-in:anywhere` is reported in the parse errors and ignored.
`anywhere` is reserved, so a Folder literally named `anywhere` is reached by `folderId` or by its full
path. The rule applies to every route: text, CJK, short-CJK `LIKE`, metadata-only, the relaxed retry
and the semantic ranking. It is a property of `search` only: `thread.get`, `message.get`,
`folder.list` and `stats` still see everything, and a thread view shows its Messages wherever they
sit. The reply carries no marker that Messages were left out, so a client that offers a Trash or Junk
view should send `folderId` for it.

`tag:auto.social`, `tag:auto.news`, `tag:auto.marketing` and `tag:auto.notifications` find the built-in
category Tags (see `tags.set`); there is no separate operator.

`is:automated` and `is:encrypted` match only a **known** value. A message whose signal is unknown
(`automated` / `encrypted` absent on its `EnvelopeDto`) matches neither the positive nor the negated
form, so `-is:automated` means "known not to be automated", never "not known to be".

`is:verified` matches a recorded DMARC `pass` from a receiving server the Account trusts (see
`EnvelopeDto.dmarc`). Unlike the two signals above, its negation is deliberately broad:
`-is:verified` matches every message with no recorded pass, including `fail`, `none`, `unknown` and
mail that was never evaluated. "Not verified" claims nothing about the sender; it only says mailcoded
has no trusted pass to show.

- `limit` defaults to **50** and is clamped to **200**. `limit: 0` or absent means the default.
- `order` ∈ `relevance | date`; relevance is the default for text queries. Relevance is BM25 with
  per-column weights — subject 20, sender 3, recipient 1, body 1 — so a term in a subject outranks
  the same term in a body.
- Terms are combined with AND. When that matches **nothing**, and the query has two or more terms,
  and no `cursor` was given, the daemon retries once with the same terms joined by OR. `relaxed` is
  then true; a client that shows the hits should tell the user they are partial matches.
  A cursor never widens, so page two keeps asking page one's question.
- `semantic` asks for meaning as well as words. `null` (the default) fuses a vector ranking into the
  results when a model is installed and changes nothing when one is not; `true` requires a model and
  answers **1008** without one; `false` asks for words only. Check `capabilities.semantic` first.
- `semantic: true` in the **reply** says the two rankings were fused. Such a page has
  `nextCursor: null` on purpose — the rankings interleave, so a later lexical page would repeat what
  fusion promoted. Fusion happens only on the first page of a `relevance` search.
- A semantic search **fills the page**: every embedded message is a candidate, so the reply tends to
  carry `limit` hits regardless of how many are genuinely relevant. There is no similarity floor yet
  — choosing one needs a pinned model to calibrate against. Treat rank order as meaningful and the
  tail as weak.
- Vectors cover messages whose **body has been fetched**, and are built in the background. A message
  synced a moment ago may be findable by word before it is findable by meaning.
- `cursor` is an **opaque keyset cursor** — pass `nextCursor` back verbatim, never construct or
  decode one. Paging is keyset seek; there is no offset parameter and there never will be.
- `truncated` on `search` is the **strong** meaning: with a non-null `nextCursor` it just says
  another page exists — keep paging, nothing was lost. With `nextCursor: null` it says matches were
  **dropped that no further call can reach** (the relevance offset cap); narrow the query or switch
  to `order: "date"`, whose cursor reaches any depth and therefore never drops a hit. See
  [§7.1](#71-the-two-meanings-of-truncated) — this is not the same `truncated` that paged listings
  return.
- **A malformed query is not an error.** The parser reports what it could not read (to stderr at
  debug level) and searches with the rest.
- `snippet` on an `EnvelopeDto` is present only on search hits and only when `includeSnippet` is
  true. **An omitted `includeSnippet` is false on the wire**; the CLI and the MCP adapter default it
  to true (see [§7.2](#72-surface-divergences)), so send `true` explicitly to get snippets. It is an excerpt of attacker-controlled mail text.

- `attachmentMatch` on an `EnvelopeDto` is present only on search hits whose query had a positive
  `attachment:` term. It names the first attachment (lowest
  `index`) that holds the term — `index` is the one `attachment.get` takes, `filename` is the
  sanitized save-as name, `mime` the declared type — and `snippet` is a short excerpt of it, present only when `includeSnippet` is true. The
  excerpt is **attacker-controlled attachment text**, exactly as untrusted as `snippet`. Only the
  first `attachment:` term decides which attachment is named.

**Search covers subject, sender, recipients and body text, and, through `attachment:`, the text of
text-like attachments and `.docx` files. It does not read PDFs, spreadsheets, presentations or
images.** Extraction is bounded and treats every attachment as hostile: a `.docx` is a zip read with
the .NET base class library only; at most 4,096 entries are declared, 16 attachments are read per message, each
XML part is read through a 16 MiB counter whatever the zip claims, a part that declares more than a
200:1 inflation ratio is refused unread, any DOCTYPE or ENTITY declaration refuses the part (no
entity is ever expanded, nothing is ever fetched), at most 256,000 characters are kept per
attachment and 512,000 per message, and a message gets five seconds. A file that exceeds a bound is
skipped or cut, never fatal, and shows only as a count in the `warnings` field of the body-fetch
audit entry.

### `thread.get`
`{ "threadKey": "…", "limit": 200 }` → `{ "messages": [EnvelopeDto], "truncated": false }`

Every message sharing a thread key, oldest first. An empty or blank `threadKey` → `-32602`.

- There is **no cursor**. `truncated: true` means `messages.length` reached the limit that was
  *applied* and the rest of the conversation was not returned — raise `limit` and call again.
- `limit` absent, `0`, or negative applies the default of **500**. A `limit` above **2000** is
  clamped to 2000, the store's own page cap: ask for 5000 and you get at most 2000 messages with
  `truncated: true`.
- `truncated` is computed against the limit actually applied, never against the number you sent. A
  700-message thread requested with no `limit` returns 500 messages and `truncated: true`.
- The CLI and the MCP adapter clamp differently again (see [§7.2](#72-surface-divergences)).

### `message.get`
`{ "messageId": 4213, "format": "text", "fetchIfMissing": true }`
→
```json
{ "envelope": EnvelopeDto, "bodyText": "…", "bodyHtml": null, "raw": null,
  "attachments": [AttachmentDto], "bodyFetched": true, "parseWarnings": [],
  "unsubscribe": UnsubscribeDto }
```

`format` ∈ `text | html | raw` (default `text`).

- `text` → `bodyText` only.
- `html` → `bodyHtml` plus the plaintext extraction in `bodyText`.
- `raw` → `raw`, the base64-encoded RFC 822 bytes.

When `bodyFetched` is false on the envelope and `fetchIfMissing` is true, the daemon connects to
IMAP, fetches the body once, stores and indexes it. **An omitted `fetchIfMissing` is false on the
wire** (the CLI and the MCP adapter default it to true, see [§7.2](#72-surface-divergences)), so a
client that wants the fetch must send `true`.

`parseWarnings` are stable slugs (`missing-date`, …). They never echo mail content.

`unsubscribe` is what the sender's `List-Unsubscribe` (RFC 2369) and `List-Unsubscribe-Post`
(RFC 8058) headers offer, and is **absent** when nothing usable is known:

```json
{ "mailto": { "address": "leave@example.org", "subject": "unsubscribe", "body": "…" },
  "web": ["https://lists.example.org/u/abc"], "oneClick": true }
```

- It is sender-controlled input, never a trust signal. `mailto` carries one address and at most the
  `subject` and `body` hfields of the URI (RFC 6068); `to`, `cc`, `bcc`, `from` and every other hfield
  are dropped, and a URI that is malformed, over its length cap or carries a control character is
  rejected whole. A client that acts on it composes an ordinary draft with `send.preview`, so the
  two-phase send and every gate apply; the daemon never sends it by itself.
- `web` holds at most four `http`/`https` URLs, ASCII only, with no userinfo, for a human to open. **The
  daemon never fetches or opens them**, and a client must not fetch them on a user's behalf without
  that user's decision.
- `oneClick` only reports that the sender advertises RFC 8058 one-click POST (an `https` URL plus
  `List-Unsubscribe-Post: List-Unsubscribe=One-Click`). **mailcoded refuses to perform it, by
  design:** the URL is chosen by whoever sent the message, and a POST to it tells that party the
  address is live. It is also outside the network rule, which allows mail protocols only.
- It appears on `message.get` only, not on search hits or `thread.get` envelopes.

A message whose raw bytes exceed a structural parse bound — the boundary-delimiter count that caps
a MIME part-count bomb — is **refused**, with `-32602` and category `protocol`, rather than returned
as a parsed message with an empty body. The refusal message carries counts only, never mail content.
The envelope stays readable and `bodyFetched` stays false, so nothing empty is recorded as the body.

> ### `bodyHtml` is returned RAW. Sanitizing it is the client's job.
>
> The daemon does **not** sanitize `bodyHtml`, and it never will: sanitization belongs where the
> rendering context is known. What you get back is attacker-controlled HTML, verbatim, including
> `<script>`, `<iframe>`, event handlers, remote `<img>` trackers, and CSS that can exfiltrate.
>
> A client that renders it must, at minimum:
> - render only inside a sandboxed context under a strict CSP with **no remote origins**
>   (`default-src 'none'; style-src <cspSource> 'unsafe-inline'; script-src 'nonce-…'; img-src data:;`);
> - pass it through DOMPurify (or an equivalent) *inside* that context first, forbidding
>   `script iframe object embed form link meta base`, all `on*` attributes, and every URL scheme
>   except `https` and `mailto`;
> - block remote images by default and require a per-message opt-in to load them;
> - never `eval` it, never interpolate it into a shell command, a terminal, a file path, a SQL
>   string, or an LLM prompt.
>
> `bodyText` is subject to the same "untrusted input" rule — it is merely not *executable*.
> Agent interfaces are given plaintext only, which is why `htmlBodies` is false for them.

### `attachment.get`
`{ "messageId": 4213, "index": 0 }` →
`{ "filename": "invoice.pdf", "mime": "application/pdf", "base64": "…", "size": 20481 }`

`index` is the `index` from the `attachments` array of `message.get`, stable within one message.
`size` is the **decoded** length. `filename` is already sanitized for use as a save-as name —
which does not make it safe to hand to a shell. Fetches from the server if the body is not local.

A negative index → `-32602`. An index with no attachment behind it, or a message with no stored
body, → **1002**. An attachment larger than the configured cap → **1007**.

### `tags.set`
`{ "messageId": 4213, "add": ["triaged"], "remove": ["inbox"] }` → `{ "tags": ["…"] }`

Applies a local **Tag** delta and returns the message's complete tag set afterwards, sorted
ordinal. Tags that mirror server **Flags** (`unread`, `flagged`, `replied`, `draft`) are projected
onto IMAP in the same call — and because the server wins on flags, a connection failure fails the
whole call (**1001**) rather than leaving a change the next sync would silently revert. Writes a
`sync_log` row.

Tag and Flag are distinct vocabularies. Neither is ever called a "label".

**Category Tags.** When a message row is first created (sync, bulk ingest or `import-eml`),
mailcoded may add exactly one of `auto.social`, `auto.news`, `auto.marketing`,
`auto.notifications`, chosen by fixed built-in rules over the From address, `List-Id`, the
automated signal and the subject. The prefix `auto.` is reserved and local-only: such Tags are never
pushed as IMAP keywords, a server keyword with that prefix is ignored, and removing one here pushes
nothing. They are assigned once and never re-added after a removal or a later sync; a message with
no obvious category has none, and mail stored before this existed is not backfilled. A re-enumerated
folder (UIDVALIDITY change) or a reused UID is a new Message and is categorised again. The inputs are
sender-controlled, so a category is a sorting aid only. No RPC method configures the rules.

### `message.move`
`{ "messageId": 4213, "toFolderId": 7 }` → `{}`

Not part of the agent surface: a caller that identified as `cli` or `mcp` gets **1006**.

**There is no delete, expunge, trash or purge method.** Not gated — absent. Moving to a folder
whose `role` is `trash` is as close as the protocol gets, and that is a normal move.

### `send.preview`
```json
{ "accountId": 1,
  "draft": { "from": null, "to": ["a@b.com"], "cc": [], "bcc": [],
             "subject": "Re: invoice", "bodyText": "Thanks — looking now.\n",
             "inReplyTo": null, "references": [], "replyToMessageId": 4213,
             "quotedText": null, "signature": null, "useDefaultSignature": null } }
```
→
```json
{ "draftId": 17,
  "preview": { "from": "me@example.com", "to": ["a@b.com"], "cc": [], "bcc": [],
               "subject": "Re: invoice", "bodyPreview": "Thanks — looking now.",
               "bodyTruncated": false, "sizeBytes": 812,
               "messageId": "…", "requiresSmtpUtf8": false, "warnings": [] },
  "confirmToken": "…",
  "confirmTokenExpiresUtc": "2026-08-30T12:39:56.000Z",
  "serverDraft": "off" }
```

Phase one of the two-phase send. Builds the message, stores it in the outbox as a draft, and mints
a **single-use token bound to that draft**. Plaintext bodies only; HTML compose and attachment
upload are out of scope for v0.1.

`preview.messageId` is the RFC 5322 Message-ID assigned at outbox creation, without angle
brackets; it is immutable and is the idempotency key for the send. `warnings` are stable slugs
(`smtputf8-unsupported`, `exceeds-server-size-limit`, …).

At least one recipient across `to`/`cc`/`bcc` is required. `replyToMessageId` makes the daemon
derive `In-Reply-To`/`References` for you; `inReplyTo` is the raw form (no angle brackets).

**Who the message is from.** `from` must be the account's own address or one of its `identities`,
compared without regard to case; the message goes out with the configured spelling, that
identity's display name (else the account's) and its `replyTo` as a `Reply-To` header. Any other
address answers **-32602** with `error.data.reason = "sender_not_allowed"` (and nothing is queued).
Omitted, `from` is the account address, or, when the draft is a reply (`replyToMessageId` or
`inReplyTo` names a message this account holds), the first address in that message's `To` then `Cc`
that the account owns, so a reply to mail sent to an alias comes from the alias. The check is made
in Core for every caller; no client can skip it.

**Signatures.** With none of the three fields set, `bodyText` is built exactly as sent. `signature`
names one of the account's signatures; `useDefaultSignature: true` asks for the account's default
for a new message or for a reply (a reply is a draft with `inReplyTo` or `replyToMessageId`).
`signature` wins when both are given; an unknown name answers **-32602**. The signature goes after
a line holding `-- ` (RFC 3676), after your text; send the quoted original of a reply in `quotedText`
rather than inside `bodyText` and it goes below the signature. `useDefaultSignature` adds nothing to
a `bodyText` that already holds its own `-- ` line. `preview.bodyPreview` shows the final body,
signature included, and `bodyTruncated` refers to that final body. A client that composes a whole
body itself, as an editor does, can instead insert the text from `account.list` and leave all three
fields unset.

**Server copy of the draft.** On an account that has `saveDraftsToServer` set, the draft is also
appended to the server's Drafts-role Folder, flagged `\Draft` and `\Seen`, so the user's other mail
clients show it. `serverDraft` says what happened: `off` (the account does not do this), `saved`, or
`failed`; with `failed`, `serverDraftReason` is `no-drafts-folder`, `not-connected` or
`append-failed`. A failure never fails the call: the draft exists locally and can be sent. The local
draft stays the only thing `send` ever transmits; the server copy is never read back. The appended
bytes are the ones that will be sent, so they carry no `Bcc` header and blind recipients live only
in the local outbox. Each preview makes its own server copy, and an abandoned one stays in Drafts
(mailcoded removes nothing from a server).

**Show the preview to a human. The preview is the supervision.** Do not mint a token and consume
it in the same automated step.

Never log `confirmToken`.

### `send`
`{ "accountId": 1, "draftId": 17, "confirmToken": "…" }` →
`{ "messageId": "…", "state": "sent", "outboxId": 17, "smtpResponse": "250 2.0.0 OK",
  "appendedToSent": false, "serverDraft": "moved" }`

Phase two. `state` ∈ `queued | sending | sent | failed`. A row is `queued` from the moment
`send.preview` builds it, but only a consumed confirm token makes it eligible to be sent: an
abandoned preview stays queued forever and is never dispatched. Every attempt — allowed or denied —
writes a `sync_log` row.

- A missing, blank, expired, or already-consumed token → **1003** (never `-32602`).
- A closed Core gate → **1006**. The agent-surface gates are `MAILCODED_SEND=1`, every recipient
  matching `MAILCODED_APPROVED_RECIPIENTS`, and ≤5 sends per rolling hour.
- `smtpResponse` is sanitized and length-capped for the audit record.
- `serverDraft` ∈ `none | saved | moving | moved | left` reports the server copy made by
  `send.preview` (`none`: there is none). After a send is accepted, the draft is **moved** into the
  Sent Folder with one `UID MOVE` (RFC 6851) and its `\Draft` flag cleared when, and only when,
  the server advertises MOVE, the account keeps a copy in Sent, the Folder's UIDVALIDITY is still
  the one recorded and the draft is still at its recorded UID. Then `appendedToSent` is false: the
  moved draft is the Sent copy, byte for byte what was sent. In every other case the draft is
  **left** in Drafts for the user to file, `serverDraft` is `left` and `serverDraftReason` says why:
  `no-move-capability`, `sent-copy-not-wanted`, `server-saves-sent` (Gmail), `no-sent-folder`,
  `no-drafts-folder`, `not-connected`, `uid-unknown`, `uidvalidity-changed`, `draft-gone` (another
  client replaced it), `move-refused`, `move-unconfirmed` (the connection dropped mid-move, so no
  second copy is made) or `interrupted` (a crash between the send and the move). Except for
  `move-unconfirmed`, `appendedToSent` then follows the usual rule. No outcome deletes, flags
  `\Deleted` or expunges anything, and a move is never retried.

### `watch.subscribe`
`{ "accountId": 1, "folderIds": [12, 13] }` → `{}`

Starts IMAP IDLE and begins delivering the notifications in §5 on the same stdout stream. An empty
`folderIds` means the account's configured watch set (INBOX when none is configured). Answers
`{}` even when `capabilities.watch` is false, but no notification will ever arrive.

### `stats`
`{}` → `{ "stats": StatsDto }`

Process and store counters: `daemonVersion`, `protocolVersion`, `uptimeMs`, working set, GC heap /
committed / total-allocated bytes, gen0/1/2 collection counts, thread and handle counts,
`openConnections`, `schemaVersion`, database / WAL / blob-directory sizes, `tableCounts`, and a
`folders` array of per-folder sync positions
(`folderId, accountId, name, highestModSeq, uidNext, unread, total, lastSyncUtc`).

`highestModSeq` is a **decimal string** (`"0"` when the server never reported one). Nothing in
`stats` identifies a message or a person.

### `health`
`{}` → `{ "health": HealthDto }`

```json
{ "status": "ok", "storeOk": true, "storeDetail": null,
  "accounts": [ { "accountId": 1, "email": "me@example.com", "connection": "connected",
                  "auth": "ok", "watching": true, "consecutiveFailures": 0,
                  "lastError": null, "lastSyncUtc": "2026-08-30T12:34:56.000Z",
                  "outboxQueued": 0, "outboxFailed": 0 } ],
  "warnings": [] }
```

`outboxQueued` counts messages a human confirmed and the daemon has still to send. A `send.preview`
that was never confirmed is a draft, not pending work, so it is excluded — otherwise the number
would never fall.

`status` ∈ `ok | degraded | error`. `connection` ∈ `connected | connecting | disconnected | error`.
`auth` ∈ `ok | auth-required | unknown`. `warnings` are stable slugs, safe to log:
`outbox-stuck-sending`, `auth-required`, `secondary-instance`, `watch-disabled-not-store-owner`.

`health` succeeds even when the daemon is unhealthy — branch on `status`, not on the presence of
an error. `lastError` is redacted: never a credential, never mail content.

### `store.backup`
`{ "directory": "/abs/path" }` → `{ "backup": BackupDto, "secretsIncluded": false }`

Writes a verifiable copy of the store into `directory` and returns what its `manifest.json`
records. The directory holds `store.db` (a consistent snapshot taken with `VACUUM INTO` while sync
and the writer keep running), `blobs/<xx>/<sha256>.blob` for every external blob file the snapshot
points at, and `manifest.json` (format version, mailcoded version, schema version, creation time,
sha256 and size of the database, row counts per table, and the hash and size of every blob file).
`BackupDto` carries the same facts minus the per-blob list: `directory`, `formatVersion`,
`mailcodedVersion`, `schemaVersion`, `createdUtc`, `databaseBytes`, `databaseSha256`,
`externalBlobCount`, `externalBlobBytes`, `tableCounts`.

- `directory` must be an **absolute** path (a relative one is `-32602`: the daemon's working
  directory is not something a client can know). It must not exist, or be an empty folder, and its
  parent must exist.
- Refused with `-32602`: a path inside (or containing) the live store, a symbolic link, a non-empty
  folder, a file, any folder that sits inside a workspace (an ancestor below the user's home that
  holds `.git`, `.vscode`, `.obsidian`, `.idea` or a `*.code-workspace` file), and on Windows the
  device, extended-length and alternate-data-stream forms. The mail store must never live inside a
  workspace folder, and a backup is a full copy of it.
- **Secrets are never copied.** `secretsIncluded` is always `false`; after a restore every account
  must be signed in again (`account.test` then `secret.set`, or `mailcoded account reauth`).
- A backup is a second copy of every message and attachment in plaintext. It is gated: allowed
  for `interface: rpc`, refused with **1006** for `mcp` always and for `cli` unless the daemon runs
  with `MAILCODED_ALLOW_BACKUP=1`. A connection that never called `initialize` is treated as `mcp`.
  Every call, allowed or refused, writes `sync_log` rows (`backup_started`, `backup_completed`,
  `backup_failed`, `backup_denied`).
- If an external blob file the snapshot refers to is missing, or does not match its hash, nothing is
  left behind and the call fails with `1004`: a backup that cannot be verified is not written. Blob
  files written after the snapshot are not needed and not copied. At most one backup runs per
  daemon at a time (`1005` for a second).
- Files are created with owner-only permissions on Unix.

### `store.backupVerify`
`{ "directory": "/abs/path" }` → `StoreBackupVerifyResult`

Opens the copy read-only and immutable (it adds no file to the directory), runs
`PRAGMA integrity_check`, recomputes the sha256 and size of `store.db` and every blob file, and
compares them, the schema version, the per-table row counts and the set of blob files with the
manifest. **A mismatch is a result, not an error:** `ok: false` with every problem found, so a
client can show them all.

```json
{ "ok": false, "directory": "/abs/path", "formatVersion": 1, "schemaVersion": 11,
  "createdUtc": "2026-10-04T01:02:03.456Z", "integrity": "ok",
  "blobFilesChecked": 3, "bytesChecked": 8412345,
  "problemCount": 2, "problemsTruncated": false,
  "problems": [ { "kind": "blob_hash", "subject": "blobs/ab/ab12....blob",
                  "message": "The file hashes to ..., not to the name it is filed under." },
                { "kind": "unexpected_file", "subject": "notes.txt",
                  "message": "The manifest does not account for this file." } ] }
```

`problems` is capped at 200 (`problemsTruncated`). `kind` is a stable slug: `directory_missing`,
`manifest_missing`, `manifest_invalid`, `unsupported_format`, `database_missing`,
`database_size`, `database_hash`, `database_unreadable`, `integrity`, `schema_version`,
`schema_newer`, `table_missing`, `table_unlisted`, `row_count`, `blob_missing`, `blob_size`,
`blob_hash`, `blob_unlisted`, `blob_unreferenced`, `unexpected_file`, `link_refused`,
`too_many_files`. Links inside a backup are never followed. `integrity` is `ok`, the
integrity_check messages, or `not run`.

The manifest is **not signed**. It detects corruption and edits that left it behind; someone who can
rewrite the whole directory can rewrite the manifest too. Same gate and the same audit rows
(`backup_verified`, `backup_verify_failed`) as `store.backup`.

**Restoring is not a method.** Stop the daemon and every other mailcoded process, keep the old
store directory aside, copy `store.db` and the `blobs` directory from the backup into the store
directory, and start again. Do not copy `manifest.json` in. The `ext_path` column inside the
database holds the path the blob had on the machine that made it; a store that cannot find a file
there looks for it under its own `blobs/<xx>/<sha256>.blob`, so a restore to a different location
works.

### `shutdown`
`{}` → `{}`

Answers first, then drains in-flight handlers and exits.

---

### `account.test`

`{ "accountId": 1 }` → `{ "imap": "ok", "smtp": "auth", "folders": 8, "smtpDetail": "..." }`

Each leg is `ok | auth | network | unsupported`. Nothing is sent and nothing is written: it exists to
tell a stored credential that still works from one that does not. `unsupported` on `smtp` means the
account has no SMTP configuration. Details are short and redacted, never a credential.

### `outbox.list`

`{ "accountId": 1?, "state": "queued"?, "limit": 200? }` → `{ "entries": [ OutboxEntryDto ] }`

No raw bytes and no body. `confirmed` is false until a confirm token was consumed; such a row is a
draft the retry loop will never dispatch, however long it sits in `queued`. `serverDraft` and
`serverDraftReason` take the values documented under `send`; both are absent when the draft was
never saved to the server.

### `provider.detect`

`{ "email": "me@gmail.com" }` → `{ "preset": ProviderPresetDto }`

A table lookup: no network, nothing stored. `preset.imap` and `preset.smtp` are `ImapConfigDto` and
`SmtpConfigDto` with `username` pre-filled, so they can be handed to `account.add` once the human has
confirmed them.

`credential` ∈ `password | appPassword | oauthOnly`. When it is `appPassword`, show `advice` and offer
`credentialUrl` **before** asking for anything — the account password will simply be refused.
`isGuess: true` means the hosts were derived from the domain (`imap.<domain>` / `smtp.<domain>`) and
must be presented as editable. `discouraged`, when present, is a warning to display, not a reason to
block the attempt.

Open `credentialUrl` only through the platform's external opener; never render it as HTML.

An address with no domain part → **-32602**.

## 5. Notifications

Server → client, no `id`, no response. `notify.mail.added`, `notify.folder.updated` and
`notify.sync.error` arrive only after `watch.subscribe`. `notify.sync.progress` arrives for the syncs
a subscription triggers and for a `sync` request that sets `progress: true`. Ignore notification
methods you do not recognise.

### `notify.mail.added`
```json
{ "accountId": 1, "folderId": 12, "folderName": "INBOX", "count": 3,
  "messages": [EnvelopeDto] }
```

`count` is how many arrived; `messages` is capped at **20** envelopes so an IDLE storm cannot flood
the client. When `count > messages.length` the batch was coalesced — refresh the list with
`search` rather than trusting the array to be complete.

### `notify.folder.updated`
```json
{ "accountId": 1, "folder": FolderDto }
```

Counts or state changed; refresh the badge. `folder` carries the same hierarchy fields as in
`folder.list`.

### `notify.sync.error`
```json
{ "accountId": 1, "folderId": 12, "code": 1001, "message": "…",
  "category": "network", "requiresUserAction": false, "retryAfterMs": 5000,
  "atUtc": "2026-08-30T12:34:56.000Z" }
```

A background sync or watch failed. `code` is **the same stable numeric code the equivalent request
error would carry**, so one handler covers both paths. Carries no mail content and no credential.
`requiresUserAction: true` means backoff alone will not recover.

### `notify.sync.progress`
```json
{ "accountId": 1, "folderId": 12, "folderName": "INBOX", "phase": "envelopes",
  "done": 12000, "total": 48000, "final": false, "atUtc": "2026-08-30T12:34:56.000Z" }
{ "accountId": 1, "phase": "folders", "done": 12, "total": 12, "final": true,
  "atUtc": "2026-08-30T12:35:40.000Z" }
```

How far a running sync has got. Counts only: no subject, no address, no body. `folderName` follows
the same convention as `notify.mail.added`; `folderId` and `folderName` are absent for `folders`.

| `phase` | Meaning | `done` | `total` |
|---|---|---|---|
| `folders` | the walk across the account's Folders | Folders finished in this sync | Folders in this sync |
| `envelopes` | Envelopes fetched into one Folder | Envelopes the store now holds for the Folder | the server's message count, never below `done` |
| `flags` | Flags re-read for one Folder | flag changes applied in this sync | the number of known messages on a full diff, otherwise absent |

- An **indicator** is one `accountId`, `folderId` and `phase`. Every event is a snapshot of that
  indicator, not a delta.
- `total` is absent when it is not known in advance, and is never less than `done`.
- `done` for `envelopes` can go **down**: it is what the Folder holds, and the server may have
  removed messages. A resumed backfill of a large Folder starts above zero, and a call that stops at
  its window limit ends with a `final` whose `done` is below `total`. That is the honest state, not
  an error.
- `final: true` is the last event for that indicator in that sync: clear it. Every indicator that
  received a non-final event receives exactly one `final`, including when the sync failed or was
  cancelled; the outcome travels in the response or in `notify.sync.error`. A short sync may send no
  event at all.
- At most one burst per Account per 250 ms, measured on a monotonic clock. A burst carries the latest
  snapshot of each open indicator of one sync. A `final` is never delayed.
- Ordering: for a `sync` request, all of its progress precedes its response. For a watch-triggered
  sync, its progress precedes that pass's `notify.mail.added` and `notify.folder.updated`, and a failed
  pass closes its indicators before `notify.sync.error`.
- Two syncs of the same Folder can overlap and share an indicator, so you may see it close and
  reopen.
- Drop an Account's indicators after 60 s without any progress for it, in case a `final` was lost to
  a full client queue.
- Ignore a `phase` you do not recognise.
- `--one-shot` never sends notifications.

---

## 6. Errors

```json
{ "jsonrpc": "2.0", "id": 2,
  "error": { "code": 1001, "message": "The connection was reset.",
             "data": { "category": "network", "retryAfterMs": 5000,
                       "requiresUserAction": false, "accountId": 1, "folderId": 12 } } }
```

`error.data` is present only when the daemon has something machine-readable to say; treat every
field in it as optional. `data.detail` is extra context that is safe to display. `data.reason` is a
stable slug for a refusal a client can branch on without reading prose; today the only one is
`sender_not_allowed` (-32602: a draft's `from` is neither the account address nor one of its
identities).

`message` is short, stable prose, control characters stripped, capped at 240 characters. **No
error message ever carries a credential or mail content** — do not try to parse mail data out of
one.

`data.category` — the adapter failure class a client can branch on without reading prose:
`network`, `protocol`, `auth`, `busy`, `full`, `notFound`, `unsupported`, `permanent`.

### 6.1 The complete code table

Codes are a wire contract: never renumbered, never reused. `RpcErrorCode` in
`src/Mailcoded.Protocol/RpcErrorCode.cs` is the source of truth. **Adding a code means adding
a row here in the same PR.**

| Code | Name | Meaning | What the client should DO |
|---:|---|---|---|
| `-32700` | ParseError | Malformed JSON, or a malformed frame. | **Fatal for the stream.** The pipe is desynchronized and the daemon stops reading. Kill the process and respawn; do not retry on the same pipe. Fix the framing bug. |
| `-32600` | InvalidRequest | Valid JSON, not a valid JSON-RPC request object (missing `method`, `jsonrpc` ≠ `"2.0"`, a batch array). | Client bug. Do not retry. Fix the request shape. |
| `-32601` | MethodNotFound | No such method on this protocol version. | Client bug or a version mismatch. Do not retry. Check `capabilities.methods` from `initialize` and degrade the feature. |
| `-32602` | InvalidParams | Params missing, wrong type, or failed validation (also raised for a bad `interface`, a blank `threadKey`, an unparseable address, an unknown signature name, and a draft `from` the account does not own, which also carries `data.reason: "sender_not_allowed"`). | Client bug. Do not retry unchanged. Surface the message to a developer, not to an end user. |
| `-32603` | InternalError | An unhandled daemon failure, or a cancelled request. The message never carries mail content. | Retry once at most. If it repeats, capture the daemon's stderr (which has the stack trace) and file a bug. |
| `1000` | Auth | Authentication to the mail server failed, or the stored credential is gone. | **Stop and ask the human.** Never retry in a loop — repeated failures lock accounts. Prompt for a new app password, write it with `secret.set`, then retry once. Surface the account in the UI as needing attention. |
| `1001` | Network | Transient network or TLS failure. | Retry with **exponential backoff and jitter**, honouring `data.retryAfterMs` when present. Keep the UI in a "reconnecting" state rather than showing an error dialog. Timeouts are reconnect triggers, not fatal errors. |
| `1002` | NotFound | The account, folder, message, attachment, thread, or draft does not exist. | Do not retry. The local id is stale — a sync expunged or moved it. Refresh the enclosing list and drop the stale id from your cache. |
| `1003` | ConfirmRequired | `send` was called without a valid one-time token, or the token expired or was already consumed. | Do not retry the same token. Call `send.preview` again, **show the fresh preview to a human**, and send with the new token. A loop that auto-mints and auto-consumes tokens defeats the only supervision in the design. |
| `1004` | StoreCorrupt | The local store failed an integrity check (also raised when the store returns something the schema forbids). | Do not retry. Stop writing. The recovery path is a re-sync from the server: the server is the source of truth for mail, and only local Tags and the outbox are at risk. Tell the user before destroying anything. |
| `1005` | RateLimited | A rate limit or busy-writer backpressure rejected the call — either a Core policy limit (e.g. ≤5 agent sends/hour) or SQLITE_BUSY-class backpressure. | Wait `data.retryAfterMs` (fall back to exponential backoff when absent), then retry. Do not parallelize harder; the writer is single-threaded by design. |
| `1006` | Forbidden | A Core safety gate refused: send disabled, recipient not allowlisted, raw SQL off, HTML body requested by an agent interface, `store.backup` or `store.backupVerify` from a caller the backup gate does not admit. | Do not retry — no amount of retrying opens a gate, and there is no RPC that opens one. Tell the human exactly which gate is closed and which environment variable or setting they would have to change themselves. |
| `1007` | StoreFull | The disk or the database is full (SQLITE_FULL). Sync pauses rather than risking corruption. | Do not retry. Surface a "disk full" state to the user with the store path. Resume only after free space is confirmed. |
| `1008` | Unsupported | The server or this build lacks a capability the call requires — e.g. SMTPUTF8 for an EAI recipient, or a provider kind this build cannot speak. | Do not retry. Degrade the feature permanently for this account. For a send, edit the recipients or the message; for a provider, this build cannot serve it. |

### 6.2 How exceptions map to codes

`RpcErrorMapper` is the one place this happens (ARCHITECTURE §12.5):

| Source | → code |
|---|---|
| unknown method | `-32601` |
| missing/expired confirm token | `1003` (`requiresUserAction: true`) |
| policy denial, rate-limited | `1005` (with `retryAfterMs` when known) |
| any other policy denial (including `store.backup` for an agent caller) | `1006` (`requiresUserAction: true`) |
| provider: auth | `1000` · network or protocol → `1001` · notFound → `1002` · busy → `1005` · full → `1007` · unsupported or permanent → `1008` |
| store: notFound | `1002` · full → `1007` · busy → `1005` · auth → `1000` · network → `1001` · unsupported → `1008` · anything else → `1004` |
| secret store | `1000` (`requiresUserAction: true`) |
| JSON, argument, format, overflow, not-supported | `-32602` (a `from` the account does not own adds `data.reason: "sender_not_allowed"`) |
| cancellation | `-32603`, `category: "busy"` |
| anything else | `-32603` with a generic message; the stack trace goes to stderr only |

### 6.3 The exit-code mapping the CLI uses

`mailcoded` translates the same codes into process exit codes, which is the fastest way for a
shell agent to branch:

| Exit | Meaning | RPC code |
|---:|---|---|
| `0` | success | — |
| `1` | internal error | `-32603` |
| `2` | validation | `-32602` |
| `3` | not found | `1002` |
| `4` | forbidden | `1006` |
| `5` | rate limited | `1005` |
| `6` | confirm required | `1003` |
| `7` | auth | `1000` |
| `8` | network | `1001` |
| `9` | store corrupt or full; also `backup-verify` finding a difference (not an error object: a report with `ok: false`) | `1004`, `1007` |
| `10` | unsupported | `1008` |
| `130` | cancelled | `-32603`, slug `cancelled` |

---

## 7. Shared DTOs and cross-surface semantics

### `EnvelopeDto`
```json
{ "id": 4213, "accountId": 1, "folderId": 12, "threadKey": "…", "messageId": "…",
  "subject": "…", "from": "…", "to": "…", "cc": "…",
  "date": "2026-08-30T12:34:56.000Z",
  "flags": ["unread"], "tags": ["unread", "triaged"],
  "hasAttachments": true, "size": 20481, "automated": false, "encrypted": false,
  "dmarc": "pass", "bodyFetched": false, "snippet": null, "attachmentMatch": null }
```

- `id`, `accountId`, `folderId` are **local row ids**, not server UIDs, and are only meaningful
  against this store.
- `messageId` is the RFC 5322 Message-ID **without angle brackets**, absent when the message had
  none.
- `flags` are server IMAP flags: `unread | flagged | answered | draft | deleted | recent`.
  Unknown names from a server are ignored, not rejected.
- `tags` are local notmuch-style Tags, including any `auto.*` category Tag (see `tags.set`). The two
  vocabularies are distinct and never merged.
- `subject`, `from`, `to`, `cc`, `snippet` are decoded but **untrusted attacker input**.
- `automated` is true when headers or MIME structure mark the message as machine-generated:
  `List-Id` or `List-Unsubscribe` present, `Precedence` of `bulk`, `list` or `junk`,
  `Auto-Submitted` other than `no` (RFC 3834), `X-Autoreply`, `X-Autorespond` or
  `X-Failed-Recipients` present, a `multipart/report` of type `delivery-status`,
  `disposition-notification` or `feedback-report`, or a `noreply`-style sender local part. The sender
  controls every one of these, so the field sorts mail and never vouches for it.
- `encrypted` is true for PGP/MIME (`multipart/encrypted`), an S/MIME `enveloped-data` or
  `authEnveloped-data` message, or an inline `-----BEGIN PGP MESSAGE-----` block in a plain-text
  body. mailcoded never decrypts. From a message whose body is not fetched yet, `false` means no
  PGP/MIME or S/MIME envelope was seen; inline armor appears only once the body is fetched.
- `automated` and `encrypted` are **absent when unknown** — mail stored before these signals existed,
  until its body is fetched or read again, and mail from a server that did not return the headers.
  Absent is not false. They are present on `search`, `message.get` and `thread.get` envelopes and
  absent on plain folder listings, which are served from a covering index that does not carry them.
- `dmarc` is what a receiving server the Account trusts reported for DMARC on the From domain, read
  from `Authentication-Results` (RFC 8601): `pass | fail | none | unknown`. Only `pass` means
  **verified**: a trusted server says the From domain was not forged. It does not mean the message is
  safe, that the sender is who the display name says, or that the content is harmless; a verified
  sender can still send a malicious message, and mailcoded makes no claim about content.
  - An Account trusts an `authserv-id` (the name a server writes at the start of its
    `Authentication-Results` header, for example `mx.example.net`) only when the human configured it
    (`mailcoded account trust`), or for Gmail's built-in `mx.google.com`. A header naming any other id
    is the sender's own and is ignored. With no trusted id, nothing is evaluated and the field is absent.
  - The topmost trusted `Authentication-Results` instance decides, and it must carry a `dmarc` result
    whose `header.from` names the message's one From domain. `dkim=pass` or `spf=pass` alone never
    verify. `unknown` means a trusted id was configured but no usable result was found (no such
    header, unreadable, contradictory, or for another domain).
  - Absent means not evaluated: no trusted id, headers not fetched, or the message predates the
    signal. Files loaded with `import-eml` are always `unknown`, because the file proves nothing
    about who wrote its headers.
  - Changing an Account's trusted ids clears its recorded results, except those of `import-eml` files,
    which stay `unknown`; the rest are recomputed when a message with a cached body is read or when
    mail is synced again.
- `bodyFetched: false` means `message.get` will hit the network only if you pass
  `fetchIfMissing: true`; omitted, it does not.
- `attachmentMatch` is `{ "index", "filename", "mime", "snippet" }`; see `search`. It never appears
  outside search results.

### `AttachmentDto`
`{ "index": 0, "filename": "invoice.pdf", "mime": "application/pdf", "size": 20481,
   "isInline": false, "contentId": null }`

Described, not transferred; bytes come from `attachment.get`.

### `AccountDto`
`{ id, email, displayName?, provider, auth, imap?, smtp?, secretRef?, quirks[], identities[],
signatures[], defaultSignatureNew?, defaultSignatureReply?, saveDraftsToServer }` — never a credential.

### 7.1 The two meanings of `truncated`

`truncated` is one field name carrying two different guarantees. Reading the weak one as the strong
one makes a client stop paging and silently miss mail.

| Shape | Where | What `truncated: true` means |
|---|---|---|
| **Paged listing** (`StorePage<T>`: envelope lists, thread lists, sync log) | keyset-cursored | *Another page exists.* It is **always exactly `nextCursor != null`**. Nothing was dropped; every matching row is still reachable. **Keep paging.** |
| **Search result** (`StoreSearchResult`, the `search` method) | relevance order only | With a non-null `nextCursor`: another page exists, as above. With `nextCursor: null`: matches were **dropped and are unreachable** by any further call — the relevance offset cap was hit. Narrow the query or use `order: "date"`. |
| **Unpaged cap** (`thread.get`, raw SQL `query`) | no cursor at all | The result hit a fixed row cap. Raise `limit` / `maxRows`, or narrow the request. |

The only case that means "results were lost" is `truncated: true` **with a null cursor**. Any
`truncated: true` accompanied by a cursor is an instruction to fetch the next page, not a warning.

### 7.2 Surface divergences

The CLI, the MCP adapter, and this RPC surface do not enforce identical limits, and their help text
has drifted from what they enforce. Recorded here rather than smoothed over; the enforced column is
read from the code.

**Thread page size**

| Surface | Enforced | Advertised |
|---|---|---|
| CLI `mailcoded thread --limit` | `1..1000`, default **200** | `mailcoded help thread` says 1..1000, default 200 — correct |
| MCP `thread` tool | `1..500`, default **200** | its JSON Schema says `maximum: 500` (correct) but *"Defaults to 500"* — **wrong**, the adapter passes 200 |
| RPC `thread.get` | `1..2000`, default **500** | — |

Three defaults (200, 200, 500) and three maxima (1000, 500, 2000) for the same operation. A client
that moves between surfaces must pass `limit` explicitly rather than rely on any default.

**Draft body size**

| Surface | Enforced |
|---|---|
| CLI `draft --body-file` | rejects a file over **1 MiB** (1,048,576 bytes) — `mailcoded help draft` says "at most 1 MiB", correct |
| CLI `draft --body-stdin` | rejects over **1,048,576 characters** — a *character* count, so multi-byte text is cut at a smaller byte size than `--body-file` |
| MCP `draft` / `send_preview` | **no body-size limit at all.** `body` is an inline JSON string; the only ceiling is the MCP host's own frame size |
| RPC `send.preview` | no body-size limit of its own; bounded only by the 32 MiB frame cap in §1 |

So "at most 1 MiB" is a CLI rule, not a mailcoded rule. Do not document it as a property of the
engine, and do not assume the MCP surface will reject an oversized body — it will not.

**Store backup**

| Surface | Offered | Gate |
|---|---|---|
| CLI `mailcoded backup`, `backup-verify` | yes | off unless `MAILCODED_ALLOW_BACKUP=1` (exit 4, refusal audited) |
| MCP | **no tool at all**, and the policy refuses an `mcp` caller even with the variable set | — |
| RPC `store.backup`, `store.backupVerify` | yes | open to `interface: rpc`; `cli` needs the variable; `mcp` never |

The reason is in `docs/AGENT-INTERFACE.md`: agents never get a file handle to the database or the
blob store, and agents get plaintext only, and a backup copy is both. The RPC is the human clients'
surface, like `message.move`; an agent that can run a shell can start its own daemon and speak this
protocol, so a deployment that must stop that needs an OS-level sandbox, not a gate.

**Snippets and the body fetch**

| Surface | `includeSnippet` omitted | `fetchIfMissing` omitted |
|---|---|---|
| CLI `search`, `read` | snippets on (`--no-snippet` turns them off) | body fetched when missing (`read --no-fetch` stays local) |
| MCP `search`, `read` | `include_snippet` defaults to true | body fetched when missing |
| RPC `search`, `message.get` | **false**: no `snippet` on any hit | **false**: no network, `bodyFetched: false` |

The wire never applies a C# initializer to a missing field, so an RPC client that wants either
behaviour sends `true`. The generated TypeScript types show both as optional booleans with no default.

**Correspondent suggestions**

| Surface | Offered |
|---|---|
| CLI `mailcoded correspondents [prefix]` | yes, `--limit 1..100`, default 10 |
| MCP | **no tool.** An agent that needs a recipient searches Sent mail (`search 'in:sent ...'`); a ready-made list of the user's correspondents is not something to hand out by default |
| RPC `correspondent.suggest` | yes, `limit` `1..100`, default 10 |

---

## 8. Client checklist

- [ ] Frame on **bytes**, not characters; cap reads at 32 MiB.
- [ ] Correlate responses by `id`; expect out-of-order delivery.
- [ ] Treat `-32700` as fatal and respawn.
- [ ] Read `capabilities` and hide features that are absent.
- [ ] Compare `protocolVersion`; refuse rather than guess on a mismatch.
- [ ] Always pass a `limit` to `search` and page with `nextCursor`.
- [ ] Sanitize `bodyHtml` yourself, in a sandbox, under a CSP with no remote origins.
- [ ] Never log `confirmToken`, and never mint-and-consume one without a human in between.
- [ ] Handle `1000` by asking the user, `1001`/`1005` with backoff, and `1002` by refreshing.
- [ ] Ignore notification methods and `phase` values you do not recognise; clear a progress indicator
      on its `final`.
- [ ] Do not look for a delete method. There isn't one.

## 9. A worked example

`src/Mailcoded.Tui` is this document, executed. It is a terminal client that spawns
`mailcoded-daemon` and speaks the framing above, and it is the only shipped client that does —
`mailcoded` (CLI) and `mailcoded-mcp` run in process against the engine.

It references `Mailcoded.Protocol` and nothing else: not the engine, not MailKit, not SQLite. An
architecture test enforces that, and the size difference is the evidence:
`mailcoded-tui` is 5.90 MiB where `mailcoded` is 15.53 MiB, both Native AOT, measured 2026-09-12.
Whatever a third-party client needs is therefore in `Mailcoded.Protocol`, because the TUI compiles
without anything else.

The reusable half lives in `src/Mailcoded.Protocol/Client/`:

| File | What it solves |
|---|---|
| `FrameCodec.cs` | `Content-Length` framing on bytes, with the 32 MiB and 8 KiB caps |
| `DaemonConnection.cs` | spawn, `id` correlation, notification fan-out, the fatal latch |
| `DaemonLaunch.cs` | finding `mailcoded-daemon`: `MAILCODED_DAEMON`, then a sibling, then PATH |
| `MailcodedClient.cs` | `initialize` with a version check, capability gating, typed calls |
| `RpcException.cs` | the numeric codes of §6, with `IsTransient` and `IsGate` |

`mailcoded tui` is a launcher on the CLI that execs this binary; it does not link it, because the
CLI references the engine and the TUI must not. `mailcoded-tui --check` performs the whole handshake
headlessly and prints what it negotiated, which
is both a diagnostic and the CI smoke test:

```
$ mailcoded-tui --check
daemon      0.1.0
methods     21
maxSearch   200
accounts    1
ok
```

Two things the TUI does that this document asks of every client, and that are easy to get wrong:

- **It never requests `format: "html"`.** An `rpc` caller is entitled to `bodyHtml`, and the daemon
  will hand over raw attacker-controlled markup. A terminal has no sandbox and no CSP, so the client
  asks for `text` and nothing else.
- **It holds `confirmToken` in a private field on the app object.** Its view models have no member
  that can carry one, so no screen can render it and no status line can leak it.
