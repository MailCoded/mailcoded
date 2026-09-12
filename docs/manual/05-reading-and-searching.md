# 5. Reading and searching from the command line

[← Contents](./)

Every verb here reads the local store and prints for a human; add `--json` and it prints a document
your scripts can rely on. Nothing in this chapter changes your mail, and only `read` and
`attachments` ever go to the server — to fetch a body or an attachment that has not been downloaded
yet, once.

## `search`

    mailcoded search '<query>' [--limit 50] [--cursor <c>] [--account <id>] [--folder <id>]
                               [--order relevance|date] [--no-snippet]

Full-text over subject, sender, recipients and body, plus structured predicates. Local only.

| Syntax | Matches |
|---|---|
| `invoice` | the word, anywhere; diacritics fold, so `café` also finds `cafe` |
| `"two words"` | the phrase |
| `inv*` | words beginning `inv` |
| `from:acme`, `to:bob@example.com`, `cc:team` | the address or name contains |
| `subject:invoice` | the subject contains |
| `tag:triaged` | carries the local Tag |
| `folder:INBOX`, `in:INBOX` | in that folder |
| `is:unread`, `is:read`, `is:flagged`, `is:unflagged`, `is:draft`, `is:replied` | server Flags; `seen`, `starred` and `answered` are accepted synonyms |
| `has:attachment` | carries an attachment |
| `before:2026-01-31`, `after:2026-01-01`, `since:2026-01-01` | strictly earlier; at or after. ISO dates |
| `-term`, `-tag:spam`, `not term` | negation |

Terms are combined with AND, and if nothing matches every word the search is retried once with OR
rather than answering nothing. You will see a line saying so above the hits, and `relaxed: true` in
the JSON. Ranking does the rest: a message matching every word still comes first. `OR` is not
something you can type; typing it is reported in `errors` and skipped. A query is
capped at 64 terms and 4096 characters. A malformed query never fails: the parser reports what it could
not read and searches with the rest, so look at `errors` in the JSON when a result seems thin.

Chinese, Japanese and Korean text is indexed by trigram, with a slower exact match for one- and
two-character terms.

    $ mailcoded search 'invoice'
          14  2025-01-19T08:00:00.000Z  [unread]  accounts@example.com
              Invoice 4471
              Invoice attached.
    1 hit(s), truncated=false

The first column is the message id the other verbs take. Latin-script text queries come back by
relevance; Chinese, Japanese and Korean text, and queries with no text at all, always come back newest
first. `--order date` asks for newest first explicitly.

Relevance weighs a hit in the subject far above the same word in a body, so a message whose subject
names what you are looking for wins over one that merely repeats the word. A hit in the sender
address counts for more than one in a body, and less than one in a subject.

    $ mailcoded search 'invoice roof'
    ~ nothing matched every word, so these match some of them, best first.
          14  2025-01-19T08:00:00.000Z  [unread]  accounts@example.com
              Invoice 4471
         304  2026-09-06T05:17:41.000Z  [-]  "Freelancer" <noreply@notifications...>
              Andrew, these PHP, HTML, and JavaScript projects and contests might interest you
    2 hit(s), truncated=false

**Paging.** `--limit` is 1 to 200, default 50. When there is more, the human output ends with the
exact command to continue —

    3 hit(s), truncated=true
    next: mailcoded search '<same query>' --cursor k1737374400000.23

— and the JSON carries `truncated` and `next_cursor`. Pass the cursor back verbatim. `truncated: true`
with a **null** cursor means the rest lies beyond what any cursor reaches: narrow the query, or use
`--order date`. Follow the cursor rather than raising `--limit`.

## `read`

    mailcoded read <id> [--no-fetch] [--max-chars 20000] [--skip-chars 0]

One message, as plaintext. There is no HTML mode and no flag that adds one.

    $ mailcoded read 14
    id:      14
    date:    2025-01-19T08:00:00.000Z
    from:    accounts@example.com
    to:      bob@example.org
    subject: Invoice 4471
    flags:   unread
    tags:    unread
    attachments: yes (bytes are not exposed to the CLI)

    Invoice attached.

When the body has not been downloaded yet, `read` connects to the account's IMAP server, fetches it
once, stores it and indexes it. `--no-fetch` stays offline and prints whatever is stored. For a long
message, `--max-chars` (1 to 1000000, default 20000) and `--skip-chars` page through the body.

Anything in a message that could drive your terminal — escape sequences, invisible and
direction-changing characters — is neutralised before it is printed (chapter 12).

## `thread`

    mailcoded thread <id|threadKey> [--limit 200]

Every message in one conversation, oldest first. A number is taken as a local message id and its thread
is resolved for you; anything else is treated as a thread key. `--limit` is 1 to 1000.

## `attachments`

    mailcoded attachments <id> [--no-fetch]
    mailcoded attachments <id> --save <index> [--out <dir>] [--overwrite]

The first form lists them:

    0  invoice.pdf  application/pdf  84 KB

    Save one with: mailcoded attachments 14 --save <index> --out <dir>

The second writes one to disk — into `--out`, or the current directory — under the flattened,
path-safe filename the parser assigned, never a path taken from the message. It refuses to overwrite
an existing file unless you pass `--overwrite`. `read` never hands you attachment bytes; this is the
only verb that does, and it writes them to a file rather than printing them.

## `folders`

    mailcoded folders [--account <id>]

Each folder with its id, the locally stored unread and total counts, and its path; `--json` adds the
role and the last successful sync.

         1      37 unread       37 total  INBOX

## `stats` and `health`

    mailcoded stats
    mailcoded health

`stats` is counters: schema version, database and blob sizes, process memory, the outbox, how many
sends are left in the current hour, and per-folder counts. Nothing in it identifies a message or a
person.

`health` is state: the store path and schema, which secret backend is active, whether the send and SQL
gates are open, each account's connection and authentication state, and a store-wide count of sends
stuck mid-dispatch.

    status:  ok
    store:   v6 at /home/you/.local/share/mailcoded/store.db
    secrets: chain(libsecret,file)
    gates:   send=off sql=off

`health` exits 0 even when something is degraded; with `--json`, branch on its `status` field, which is
`ok` or `degraded`. (`ok` is `true` in every successful document and says nothing about health.)

## Output and exit codes

Every `--json` document starts with `schema_version` and `ok`. Errors go to stderr — with `--json`,
as a JSON object whose `error.code` is the RPC numeric code — and the exit code says what kind went
wrong: 2 you sent bad arguments, 3 nothing by that id, 7 the credential failed, 8 the network did, and
so on. The full table is in chapter 14.
