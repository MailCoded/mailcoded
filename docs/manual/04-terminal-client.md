# 4. The terminal client

[← Contents](./)

    mailcoded tui

A full-screen client for the terminal: every account in one sidebar, a message list, a preview, a
reader, triage keys, and compose with the two-phase send. Press `?` inside it for the key list, and
watch the bar above the status line — it always shows what works *right now*, and its entries are
clickable.

Unlike every other verb, `tui` does not run in the CLI's process. It starts `mailcoded-daemon` and
talks to it over JSON-RPC, exactly as a third-party client would. That is deliberate: it is what makes
the TUI the worked example behind [docs/rpc.md](https://github.com/MailCoded/mailcoded/blob/main/docs/rpc.md). It never asks the daemon for HTML — a
terminal has no sandbox — so what you read is the plaintext part of each message.

## Starting it

| | |
|---|---|
| `mailcoded tui` | the normal way; finds `mailcoded-tui` and `mailcoded-daemon` beside the CLI, then on PATH |
| `mailcoded tui --data-dir <dir>`, `--db <path>` | another store |
| `mailcoded-tui [--store <path>]` | the binary directly; `MAILCODED_DAEMON=<path>` picks the daemon executable |
| `mailcoded-tui --check` | connect, print the daemon version, method count and account count, exit; no screen |

It needs a real terminal: with stdin or stdout redirected, `mailcoded tui` refuses rather than hanging.
Exit codes: 0 normally; 2 when the console cannot render ANSI (on Windows, use Windows Terminal);
3 when the daemon went away, or `--check` failed; 4 when the daemon refused the connection.

With no account configured it opens and says so: `No account is configured. Run 'mailcoded setup'
first.` Accounts are added with the CLI (chapter 2); the TUI shows whatever exists.

## The screen

    mailcoded   you@example.com   (2 accounts)                                   header
    - you@example.com    12/340 |*!@ sender      subject               date | From:    ...
        Inbox            12/300 | *  alice       Quarterly numbers    09:14 | Subject: ...
        Drafts                2 |    bob         Re: invoice          Sep 04 | ----------
        Sent                 38 |  !@ accounts   Invoice 4471         Sep 02 | body ...
    + other@example.org  3/1200 |                                           |
    j/k move  enter read  p preview  c compose  / search  ...                key bar
    300 in Inbox  n for more                                                status line

- **Header** — the account that owns the selected folder, and how many accounts there are.
- **Sidebar** — every account and, under each, its folders: the special-use ones first, in the
  familiar order (Inbox, Drafts, Sent, Trash or Deleted, Junk, Archive, All), then the rest
  alphabetically, nested on `/`. `-` marks an expanded row, `+` a collapsed one. Counts are
  `unread/total`; a folder with nothing unread shows only its total, and an account row totals its
  folders. Roles come from the server's special-use flags, so the order holds for a Trash folder
  called "Papierkorb".
- **Message list** — three marker cells (`*` unread, `!` flagged, `@` has attachments), the sender,
  the subject, and a date that is `HH:mm` for today and `Mon dd` otherwise. Unread rows are bright and
  read rows dim. It loads 100 at a time; the status line says `n for more` when there are more.
- **Preview** — on a terminal at least 100 columns wide, the right-hand pane shows the selected
  message's From, Subject and Date, its attachments, and its body. It is on by default; `p` toggles
  it. A body that has
  not been downloaded is fetched from the server once you have rested on the row for about a third
  of a second, so scrolling quickly through a list does not fetch every row it passes.
- **Key bar** — the bindings that apply now. It changes when you open a message, start a search,
  compose, or reach the confirmation screen. Click an entry to press it.
- **Status line** — what just happened, or a hint. Prompts (search, tags, discard) appear here too.

It opens on the Inbox of the first account.

## Moving around

| Key | |
|---|---|
| `j` / `↓`, `k` / `↑` | next, previous |
| `g`, `G`, `Home`, `End` | first, last |
| `space`, `PgDn`, `PgUp` | a page |
| `ctrl-d`, `ctrl-u` | half a page |
| `n` | load the next 100 messages |
| `tab`, `h` / `←`, `l` / `→` | between the sidebar and the list |
| `h` / `l` in the sidebar | collapse, expand — or jump to the parent, into the first child |
| `enter` | open the folder; read the message |
| `ctrl-l` | redraw |
| `q` | back out of the reader; from the list, quit. `ctrl-c` quits from anywhere |

Escape undoes the innermost thing first: a running operation, then the open message, then a search.

## Reading

`enter` on a message opens the reader and marks it read — a Flag change, pushed to the server. The
headers come first (Subject, From, To, Cc, Date, Tags, one `Attach:` line per attachment with its
index, name, type and size, and any parse warnings), then the body, wrapped at up to 100 columns.

| Key | |
|---|---|
| `j` / `k`, `space`, `ctrl-d` / `ctrl-u`, `g` / `G` | scroll |
| `r`, `R` | reply, reply to all |
| `u`, `f` | toggle unread, flagged |
| `t` | edit tags |
| `a`, `m` | archive, move |
| `T` | show the whole conversation |
| `s` then a digit | save that attachment |
| `q` or `esc` | back to the list |

A body that has not been downloaded is fetched when you open the message. If a message genuinely has
no plaintext part, the reader says `(this message has no plaintext part)`; it does not fall back to
HTML.

## Searching

`/` opens a prompt on the status line. The query language is the one `mailcoded search` uses
(chapter 5): words, `"phrases"`, `from:`, `to:`, `subject:`, `tag:`, `is:unread`, `has:attachment`,
`before:` and `after:` dates, `-` to negate. A search covers the **whole account** the selected folder
belongs to and comes back by relevance; `esc` clears it and returns you to the folder.

## Triage

Every key here acts on the open message, or else on the highlighted row.

| Key | | |
|---|---|---|
| `u` | toggle unread | Flags are pushed to the server in the same call |
| `f` | toggle flagged | |
| `t` | edit tags | a prompt, space-separated: `triaged followup -inbox` adds two and removes one; a leading `+` is optional |
| `a` | archive | to the folder the server marks as Archive; if the account has none, you are told to use `m` |
| `m` | move | the sidebar becomes a picker — `j`/`k` to the destination, `enter` to move, `esc` to cancel. Within the same account only |
| `T` | thread | replaces the list with the conversation, oldest first; `esc` goes back |
| `r` | sync this folder (from the list) | the status line reports `+added ~updated -removed in N ms` |
| `s` then `0`–`9` | save an attachment | into `~/Downloads`, under the safe name the daemon assigned; never overwrites — a second `invoice.pdf` is saved as `invoice (2).pdf` |
| `A` | test this account's connection | the result appears in the status line |
| `S` | daemon status | version, uptime, memory; per account its connection, auth, whether it is being watched, outbox counts and last error |
| `o` | outbox | queued, sending, sent and failed sends, with the SMTP reply where there is one |

A Tag name is lower-cased, at most 128 characters, printable ASCII, with no whitespace and none of
`" ' ( ) * : ; , \ /`.

## Composing and sending

`c` starts a blank message. `r` and `R` in the reader start a reply with the recipients, a `Re:`
subject, the quoted original and the threading headers already filled in. The composer has To, Cc,
Bcc, Subject and a body.

| Key | |
|---|---|
| `tab`, `shift-tab` | next, previous field |
| `enter` | a new line in the body; the next field elsewhere |
| arrows, `home`, `end`, `backspace` | edit |
| `ctrl-s` | preview the send |
| `esc`, `ctrl-c` | discard — it asks `discard the draft? y/n:` first |

Addresses are comma-separated. Attachments cannot be added in this release.

`ctrl-s` sends the draft to the daemon for a **preview** and opens the confirmation screen:

    SEND THIS MESSAGE?

    From:      you@example.com
    To:        bob@example.org
    Cc:        team@example.org
    Subject:   Re: invoice
    Size:      1204 bytes

    Thanks - looking now.
    ...

    This confirmation expires in 597s.

    press Y to send, anything else to go back

Every recipient is listed, in a colour meant to make you read it. **Only a capital `Y` sends.** Any
other key returns you to the composer with the draft intact: `Not sent. The draft is still here.`

The preview comes with a one-time confirmation token. The TUI holds it in memory and never displays
it; it is valid for ten minutes and bound to exactly the bytes you previewed. If it expires, or the
send is refused, you are put back in the composer and told why — `That confirmation is spent or
expired. Preview again.`, or the server's own message, with `Run 'mailcoded account reauth'.` added
when the problem is authentication. After a successful send the status line reports the result.

The TUI talks to the daemon as an editor-style client, not as an agent, so the agent-only send
controls — `MAILCODED_SEND`, the recipient allowlist, the hourly budget (chapter 7) — do not apply to
it. The confirmation screen is the control.

## Live updates

At startup the TUI asks the daemon to watch every account's folders. New mail arrives as
`3 new in INBOX - r to refresh` — the folder named by its server path — and a folder whose counts
changed is updated in the sidebar.
Watching uses IMAP IDLE, so the daemon keeps one connection open per watched folder.

Only one daemon can hold a store's live connections at a time (chapter 9). If another mailcoded is
already watching this store — a second TUI window, say — this one still works but tells you, once:
`Live updates belong to another mailcoded window; press r to refresh here.`

An account that cannot sync — no stored credential, or a server that refuses — is reported in red on
the status line as `sync: <reason>`.

## Slow operations

Anything that goes to the server runs in the background, and the status line says what it is doing:
`opening`, `syncing Inbox`, `previewing`. If it takes long, the line changes to `Still syncing
Inbox. Press esc to give up on it.` — `esc` cancels it, and the screen stays usable throughout.

## Mouse

On Linux and macOS the mouse works: click a folder or a message to select it, double-click a message
to open it, click an account row to fold or unfold it, scroll with the wheel (three rows a notch), and click any
entry in the key bar. Your terminal's own text selection needs **shift** held down while mouse
reporting is on. The client turns the mouse on only when it could put the terminal into raw mode,
which it does through `stty`; on Windows it is keyboard-only.

## Not in the TUI yet

Adding, re-authenticating and removing accounts, importing `.eml` files, and raw SQL are command-line
only for now (chapters 2, 8, 10). There is no HTML view, by design.
