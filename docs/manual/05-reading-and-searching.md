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
| `tag:triaged` | carries the local Tag; `tag:auto.news` finds a built-in category (chapter 6) |
| `folder:INBOX`, `in:INBOX` | in that folder; a folder is matched by its path, its last path segment, or its role (`in:trash`, `in:junk`) |
| `in:anywhere` | every folder, Trash and Junk included (see below) |
| `is:unread`, `is:read`, `is:flagged`, `is:unflagged`, `is:draft`, `is:replied` | server Flags; `seen`, `starred` and `answered` are accepted synonyms |
| `is:automated` | machine-generated: list or bulk mail, an auto-reply, a delivery or read report, a `noreply`-style sender |
| `is:encrypted` | PGP/MIME, an S/MIME envelope, or an inline PGP block; mailcoded cannot read the body |
| `is:verified` | a receiving server you trust reported DMARC `pass` for the From domain (see below); this is not "safe" |
| `has:attachment` | carries an attachment |
| `before:2026-01-31`, `after:2026-01-01`, `since:2026-01-01` | strictly earlier; at or after. ISO dates |
| `-term`, `-tag:spam`, `not term` | negation |

**Trash and Junk are left out by default.** A search skips messages whose folder the server marks as
Trash or Junk, in every account, so a discarded newsletter does not crowd out the mail you meant. Any
of these puts them back:

- name a folder in the query: `invoice in:trash`, `folder:Spam`. A folder you name is searched whatever
  its role, and `in:trash` and `in:junk` match by role, so they cover every account's Trash or Junk
  folder;
- `in:anywhere` searches every folder, for when you do not know where a message went;
- `--folder <id>` limits the search to one folder, Trash and Junk included.

A minus does not count as naming a folder: `invoice -in:inbox` still leaves Trash and Junk out. The
word `anywhere` is reserved for this, so a folder literally called `anywhere` is reached with
`--folder` or by its full path. Reading a message, showing a thread, listing folders and `stats` are
not affected, and a search for a word that only a discarded message contains answers nothing until
you add `in:anywhere` or `in:trash`.

`is:automated` and `is:encrypted` are worked out from message headers and structure when mail is
synced, and refined when a body is fetched (inline PGP armor can only be seen then). They match only a
**known** value, and so do their negations: `-is:automated` finds mail known not to be automated, and
mail whose status is not known yet matches neither form. Mail that was already in your store before
these operators existed stays unknown until you open it with `read` or its body is fetched, so
`-is:automated` will not list it at first. A sender writes these headers, so treat both as a sorting
aid, never as a statement that a message is safe.

In `--json` output, `search`, `read` and `thread` carry `automated` and `encrypted` as `true`, `false`
or `null` (unknown).

**`is:verified`** means exactly one thing: a mail server you told mailcoded to trust wrote an
`Authentication-Results` header saying DMARC passed for the domain in the From line. It is about the
sender's domain, not the message. A verified message can still be a scam from a domain the attacker
owns, and it can still carry a malicious link or attachment, so it is never "safe" and never "trusted
content".

- Nothing is verified until an `authserv-id` is trusted. Gmail accounts trust `mx.google.com` out of
  the box; for any other server run `mailcoded account trust` (chapter 8), because a header the sender
  wrote themselves can claim anything.
- Only DMARC counts. A DKIM or SPF pass on its own does not make a message verified.
- `-is:verified` is broader than the other negations: it lists everything without a recorded pass,
  including mail that failed, mail the server said nothing usable about, and mail mailcoded never
  evaluated. It never means "forged".
- `read`, `search` and `thread` show a `dmarc` field: `pass`, `fail`, `none`, `unknown`, or `null`
  when mailcoded did not evaluate the message (no trusted id, or it predates the feature). Imported
  `.eml` files are always `unknown`.

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

**Searching by meaning — unfinished, and there is nothing to install yet.** The `--meaning` flag
exists and the machinery behind it runs, but **no model is shipped or pinned**, so on any checkout
today it has nothing to rank with:

    $ mailcoded search 'the leaking roof thing' --meaning
    ~ no model is installed, or nothing has been embedded yet; these match on words alone.

`scripts/install.sh --with-model` is where a model would come from, and it currently refuses:
the URL and checksum it would verify are empty, so it prints *"no model is pinned in this checkout"*
and installs nothing. Until a licence has been read and a digest recorded, that is the whole story —
there is no download to accept, and nothing to delete afterwards.

What the flag does when a model *is* present: it embeds your query, ranks every embedded message by
how close it sits to that query, and fuses that ranking with the ordinary word search, which stays
primary — a message both stages found beats one only a single stage found. The JSON reply then
carries `semantic: true`. **Whether that ordering is any better than plain search is unmeasured.**
The pipeline has only ever run against a test model with random weights, which proves the plumbing
and says nothing about relevance, so treat the feature as unfinished rather than as a way to find
things. `mailcoded search 'roof'` is what to use meanwhile.

Three things to know if you do put a model in place:

- **It fills the page.** Every embedded message is a candidate, so a word search that found one
  message can come back with fifty. There is no relevance threshold, because choosing one needs a
  real model to calibrate against. Read down until the results stop making sense.
- A fused page has **no cursor**. The two rankings interleave, so a later full-text page would repeat
  what fusion promoted. Page with `--order date` if you need to walk a large result set.
- Only messages whose **body has been fetched** have a vector, and they are embedded in the
  background by the daemon. Nothing is findable by meaning until a daemon has run.

It is a flag rather than the default because it costs one model load per command, and `mailcoded` is
a command an agent runs in a loop. A long-lived daemon loads the model once at startup instead, so a
client that talks to the daemon would not pay that per search.

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

With `--json`, a message whose sender offers a way to unsubscribe carries an `unsubscribe` object
(`null` when it offers none); chapter 6 explains it and the `unsubscribe` verb.

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

Each folder with its id, the locally stored unread and total counts, and its name, as a tree:
special-use folders first, then the rest by name, children indented under their parent. A parent the
server never listed as a folder shows as a bare line with no id and no counts.

         1      37 unread       37 total  INBOX
         4       2 unread       90 total  Work
                                            Clients
         7       0 unread       12 total      Acme

`--json` keeps a flat array in the same order and adds, for each folder, `leaf` (its last level),
`depth` (levels above it), `parent_id` (its nearest ancestor that is a folder, else `null`),
`delimiter` (the server's own, `null` for a flat namespace), the role and the last successful sync.
`name` always uses `/` between levels, whatever the server uses.

## `correspondents`

    mailcoded correspondents [prefix] [--account <id>] [--limit <n>]

The people you have written to, best first, for completing a recipient. A prefix matches the start of
an address, of a display name, or of any word in a name, in any case. With no prefix you get the best
overall. `--limit` is 1 to 100 and defaults to 10.

    ada@example.org        12x  2026-01-09  Ada Example
    bob@example.net         3x  2025-11-30

The list is learned from outgoing mail only. Two things feed it: every message mailcoded sends, and
every message that appears in a Sent folder and was written by you (its From is your account address
or one of your identities, chapter 8). Mail you receive adds no one, however much of it there is. To
and Cc count; Bcc does not, so a message sent to a hidden list teaches nothing. Your own addresses,
robot senders such as `noreply` and `mailer-daemon`, and any message to more than 25 people are
left out. A message mailcoded sent counts once however many times it was retried, and its copy
in Sent is not counted again. Mail you sent from another client under an address that is not your
account address counts only once you add that address as an identity.

Order is how often and how recently: a person you wrote to last week outranks one you wrote to once
long ago, but a long-standing correspondent is not pushed out by a stray recent name. A name is the
one on the most recent message that carried one, and is untrusted text. `--json` gives `address`,
`display_name`, `uses` and `last_used_utc`. Sent mail that was already stored when this feature first
appeared is counted once, the next time the store opens. `account forget` removes the list with the
account. An agent can run this verb; the MCP server has no tool for it, and an agent that only needs
to find a recipient can search the Sent folder (`in:sent`).

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
