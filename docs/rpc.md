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
3. Optionally `watch.subscribe` to start receiving notifications.
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
    "notifications": ["notify.mail.added", "notify.folder.updated", "notify.sync.error"],
    "providers": ["imap"],
    "search": true,
    "threading": true,
    "attachments": true,
    "watch": true,
    "send": false,
    "rawSql": false,
    "htmlBodies": true,
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
  on over the wire. `send`, `rawSql` and `htmlBodies` reflect Core-side gates evaluated for *this
  caller's `interface`*, so the same daemon returns different values to a `cli` caller than to an
  `rpc` one.
- `send: false` means `send` will answer **1006**, not that the method is missing. Ask for a
  `send.preview` anyway if you want to show the user why.
- `htmlBodies: false` means `message.get` with `format: "html"` or `"raw"` answers **1006**;
  agent interfaces (`cli`, `mcp`) get plaintext only.
- `watch: false` means this process is not the store owner and will emit no notifications.
- `maxSearchLimit` (200) is a **clamp, not a validation error**: a larger `limit` is silently
  reduced. Page with `nextCursor`.
- `secretBackend` is the name of the active secret backend (`libsecret`, `keychain`, `wincred`,
  `file`). It is a backend name and never a secret.

---

## 4. Methods

Eighteen methods. `params` must be a JSON object for every one of them; a missing, `null`, or
non-object `params` is `-32602`. Methods whose params are `{}` still require the empty object.

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
  "secretRef": "mailcoded:me@example.com"
}
```
→ `{ "accountId": 1 }`

`provider` ∈ `imap | graph | jmap | gmail`; only `imap` is implemented in v0.1 and anything else
answers **1008**. `security` ∈ `none | sslOnConnect | startTls | startTlsWhenAvailable`.
`auth.kind` ∈ `password | oauth2`. `secretRef` is required and must already exist — passing a raw
credential here is impossible by design.

### `account.list`
`{}` → `{ "accounts": [AccountDto] }`

`AccountDto` carries `secretRef` (an opaque handle) and `quirks` (latched server-quirk names for
diagnostics). It never carries a credential.

### `folder.list`
`{ "accountId": 1 }` → `{ "folders": [FolderDto] }`

`FolderDto`: `{ id, accountId, name, role?, unread, total }`. `name` uses `/` as the hierarchy
delimiter. `role` ∈ `inbox | sent | drafts | trash | archive | junk | all`, or absent when the
folder has no special use. Counts are the locally stored ones — they reflect the last sync, not
the server this instant.

Unknown `accountId` → **1002**.

### `sync`
`{ "accountId": 1, "folderId": 12 }` → `{ "added": 0, "updated": 0, "expunged": 0, "durationMs": 0 }`

Omit `folderId` to sync the whole account. Opens a network connection. Idempotent: re-running
after an interruption replays safely. A `folderId` that belongs to another account → **1002**.

### `search`
```json
{ "query": "from:acme invoice", "limit": 50, "cursor": null,
  "accountId": null, "folderId": null, "order": "relevance", "includeSnippet": true }
```
→ `{ "hits": [EnvelopeDto], "nextCursor": "…", "truncated": false }`

Local only; never touches the network. Query syntax: bare words (full text over subject, sender,
recipients, body), `"quoted phrases"`, `from:`, `to:`, `cc:`, `subject:`, `tag:`, `folder:`,
`is:unread|flagged|draft|replied`, `has:attachment`, `before:YYYY-MM-DD`, `after:YYYY-MM-DD`, and
`-` negation.

- `limit` defaults to **50** and is clamped to **200**. `limit: 0` or absent means the default.
- `order` ∈ `relevance | date`; relevance is the default for text queries.
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
  true. It is an excerpt of attacker-controlled mail text.

**Search covers subject and body text only. Attachment *contents* are not indexed.**

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
  "attachments": [AttachmentDto], "bodyFetched": true, "parseWarnings": [] }
```

`format` ∈ `text | html | raw` (default `text`).

- `text` → `bodyText` only.
- `html` → `bodyHtml` plus the plaintext extraction in `bodyText`.
- `raw` → `raw`, the base64-encoded RFC 822 bytes.

When `bodyFetched` is false on the envelope and `fetchIfMissing` is true (the default), the
daemon connects to IMAP, fetches the body once, stores and indexes it. Pass `fetchIfMissing:
false` to stay offline.

`parseWarnings` are stable slugs (`missing-date`, …). They never echo mail content.

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
             "inReplyTo": null, "references": [], "replyToMessageId": 4213 } }
```
→
```json
{ "draftId": 17,
  "preview": { "from": "me@example.com", "to": ["a@b.com"], "cc": [], "bcc": [],
               "subject": "Re: invoice", "bodyPreview": "Thanks — looking now.",
               "bodyTruncated": false, "sizeBytes": 812,
               "messageId": "…", "requiresSmtpUtf8": false, "warnings": [] },
  "confirmToken": "…",
  "confirmTokenExpiresUtc": "2026-08-30T12:39:56.000Z" }
```

Phase one of the two-phase send. Builds the message, stores it in the outbox as a draft, and mints
a **single-use token bound to that draft**. Plaintext bodies only; HTML compose and attachment
upload are out of scope for v0.1.

`preview.messageId` is the RFC 5322 Message-ID assigned at outbox creation, without angle
brackets; it is immutable and is the idempotency key for the send. `warnings` are stable slugs
(`smtputf8-unsupported`, `exceeds-server-size-limit`, …).

At least one recipient across `to`/`cc`/`bcc` is required. `replyToMessageId` makes the daemon
derive `In-Reply-To`/`References` for you; `inReplyTo` is the raw form (no angle brackets).

**Show the preview to a human. The preview is the supervision.** Do not mint a token and consume
it in the same automated step.

Never log `confirmToken`.

### `send`
`{ "accountId": 1, "draftId": 17, "confirmToken": "…" }` →
`{ "messageId": "…", "state": "sent", "outboxId": 17, "smtpResponse": "250 2.0.0 OK" }`

Phase two. `state` ∈ `queued | sending | sent | failed`. A row is `queued` from the moment
`send.preview` builds it, but only a consumed confirm token makes it eligible to be sent: an
abandoned preview stays queued forever and is never dispatched. Every attempt — allowed or denied —
writes a `sync_log` row.

- A missing, blank, expired, or already-consumed token → **1003** (never `-32602`).
- A closed Core gate → **1006**. The agent-surface gates are `MAILCODED_SEND=1`, every recipient
  matching `MAILCODED_APPROVED_RECIPIENTS`, and ≤5 sends per rolling hour.
- `smtpResponse` is sanitized and length-capped for the audit record.

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
draft the retry loop will never dispatch, however long it sits in `queued`.

## 5. Notifications

Server → client, no `id`, no response. They arrive only after `watch.subscribe`.

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

Counts or state changed; refresh the badge.

### `notify.sync.error`
```json
{ "accountId": 1, "folderId": 12, "code": 1001, "message": "…",
  "category": "network", "requiresUserAction": false, "retryAfterMs": 5000,
  "atUtc": "2026-08-30T12:34:56.000Z" }
```

A background sync or watch failed. `code` is **the same stable numeric code the equivalent request
error would carry**, so one handler covers both paths. Carries no mail content and no credential.
`requiresUserAction: true` means backoff alone will not recover.

---

## 6. Errors

```json
{ "jsonrpc": "2.0", "id": 2,
  "error": { "code": 1001, "message": "The connection was reset.",
             "data": { "category": "network", "retryAfterMs": 5000,
                       "requiresUserAction": false, "accountId": 1, "folderId": 12 } } }
```

`error.data` is present only when the daemon has something machine-readable to say; treat every
field in it as optional. `data.detail` is extra context that is safe to display.

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
| `-32602` | InvalidParams | Params missing, wrong type, or failed validation (also raised for a bad `interface`, a blank `threadKey`, an unparseable address). | Client bug. Do not retry unchanged. Surface the message to a developer, not to an end user. |
| `-32603` | InternalError | An unhandled daemon failure, or a cancelled request. The message never carries mail content. | Retry once at most. If it repeats, capture the daemon's stderr (which has the stack trace) and file a bug. |
| `1000` | Auth | Authentication to the mail server failed, or the stored credential is gone. | **Stop and ask the human.** Never retry in a loop — repeated failures lock accounts. Prompt for a new app password, write it with `secret.set`, then retry once. Surface the account in the UI as needing attention. |
| `1001` | Network | Transient network or TLS failure. | Retry with **exponential backoff and jitter**, honouring `data.retryAfterMs` when present. Keep the UI in a "reconnecting" state rather than showing an error dialog. Timeouts are reconnect triggers, not fatal errors. |
| `1002` | NotFound | The account, folder, message, attachment, thread, or draft does not exist. | Do not retry. The local id is stale — a sync expunged or moved it. Refresh the enclosing list and drop the stale id from your cache. |
| `1003` | ConfirmRequired | `send` was called without a valid one-time token, or the token expired or was already consumed. | Do not retry the same token. Call `send.preview` again, **show the fresh preview to a human**, and send with the new token. A loop that auto-mints and auto-consumes tokens defeats the only supervision in the design. |
| `1004` | StoreCorrupt | The local store failed an integrity check (also raised when the store returns something the schema forbids). | Do not retry. Stop writing. The recovery path is a re-sync from the server: the server is the source of truth for mail, and only local Tags and the outbox are at risk. Tell the user before destroying anything. |
| `1005` | RateLimited | A rate limit or busy-writer backpressure rejected the call — either a Core policy limit (e.g. ≤5 agent sends/hour) or SQLITE_BUSY-class backpressure. | Wait `data.retryAfterMs` (fall back to exponential backoff when absent), then retry. Do not parallelize harder; the writer is single-threaded by design. |
| `1006` | Forbidden | A Core safety gate refused: send disabled, recipient not allowlisted, raw SQL off, HTML body requested by an agent interface. | Do not retry — no amount of retrying opens a gate, and there is no RPC that opens one. Tell the human exactly which gate is closed and which environment variable or setting they would have to change themselves. |
| `1007` | StoreFull | The disk or the database is full (SQLITE_FULL). Sync pauses rather than risking corruption. | Do not retry. Surface a "disk full" state to the user with the store path. Resume only after free space is confirmed. |
| `1008` | Unsupported | The server or this build lacks a capability the call requires — e.g. SMTPUTF8 for an EAI recipient, or a provider kind this build cannot speak. | Do not retry. Degrade the feature permanently for this account. For a send, edit the recipients or the message; for a provider, this build cannot serve it. |

### 6.2 How exceptions map to codes

`RpcErrorMapper` is the one place this happens (ARCHITECTURE §12.5):

| Source | → code |
|---|---|
| unknown method | `-32601` |
| missing/expired confirm token | `1003` (`requiresUserAction: true`) |
| policy denial, rate-limited | `1005` (with `retryAfterMs` when known) |
| any other policy denial | `1006` (`requiresUserAction: true`) |
| provider: auth | `1000` · network or protocol → `1001` · notFound → `1002` · busy → `1005` · full → `1007` · unsupported or permanent → `1008` |
| store: notFound | `1002` · full → `1007` · busy → `1005` · auth → `1000` · network → `1001` · unsupported → `1008` · anything else → `1004` |
| secret store | `1000` (`requiresUserAction: true`) |
| JSON, argument, format, overflow, not-supported | `-32602` |
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
| `9` | store corrupt or full | `1004`, `1007` |
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
  "hasAttachments": true, "size": 20481, "bodyFetched": false, "snippet": null }
```

- `id`, `accountId`, `folderId` are **local row ids**, not server UIDs, and are only meaningful
  against this store.
- `messageId` is the RFC 5322 Message-ID **without angle brackets**, absent when the message had
  none.
- `flags` are server IMAP flags: `unread | flagged | answered | draft | deleted | recent`.
  Unknown names from a server are ignored, not rejected.
- `tags` are local notmuch-style Tags. The two vocabularies are distinct and never merged.
- `subject`, `from`, `to`, `cc`, `snippet` are decoded but **untrusted attacker input**.
- `bodyFetched: false` means `message.get` will hit the network unless you pass
  `fetchIfMissing: false`.

### `AttachmentDto`
`{ "index": 0, "filename": "invoice.pdf", "mime": "application/pdf", "size": 20481,
   "isInline": false, "contentId": null }`

Described, not transferred; bytes come from `attachment.get`.

### `AccountDto`
`{ id, email, displayName?, provider, auth, imap?, smtp?, secretRef?, quirks[] }` — never a
credential.

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
- [ ] Do not look for a delete method. There isn't one.

## 9. A worked example

`src/Mailcoded.Tui` is this document, executed. It is a terminal client that spawns
`mailcoded-daemon` and speaks the framing above, and it is the only shipped client that does —
`mailcoded` (CLI) and `mailcoded-mcp` run in process against the engine.

It references `Mailcoded.Protocol` and nothing else: not the engine, not MailKit, not SQLite. An
architecture test enforces that, and the size difference is the evidence — 5.6 MB against the CLI's
15.3 MB, both Native AOT on linux-x64. Whatever a third-party client needs is therefore in
`Mailcoded.Protocol`, because the TUI compiles without anything else.

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
methods     18
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
