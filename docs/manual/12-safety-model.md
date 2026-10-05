# 12. The safety model, in plain terms

[← Contents](./)

The design documents state these as invariants. This is what they mean for you.

## Nothing here deletes mail

There is no delete, expunge, trash or purge anywhere — not in the CLI, the TUI, the MCP server or the
wire protocol — and no setting that adds one. `account forget` removes an account's *local copy* and
nothing on the server. If you want a message gone, move it to your provider's Trash and let the server
do it. A tool that cannot delete cannot be tricked into deleting, and a fresh sync from the server is
always a way back.

## Your mail is treated as hostile input

Every subject, sender and body arrived from a stranger. So:

- **The terminal is protected.** Before anything from a message reaches your screen, control
  characters, escape sequences, invisible characters and the Unicode direction-override characters
  that can make text read backwards are removed or replaced with `�`. If you see that character in a
  subject, the message tried to put something there that would have driven your terminal. One of the
  bundled fixtures does exactly that, and `read` shows it as harmless text:

      subject: [2J [HCleared your screen

  The escape byte that would have cleared your screen is gone; what remains is inert.
- **No HTML in a terminal.** The TUI and the CLI show the plaintext part only. There is no flag for
  HTML, and the TUI never asks the daemon for it, because a terminal has no sandbox to render it in.
  Remote images, tracking pixels and the like never load.
- **Filenames are flattened.** An attachment is saved under a name the parser made safe, never a path
  the message supplied, so a message that calls its attachment `../../.bashrc` produces a harmlessly
  named file in the directory you chose.
- **Message text never becomes a command, a query or a path.** Database access is parameterised
  throughout, and nothing from a message is spliced into SQL or a shell.

## Credentials

Passwords and sign-in grants live in the OS keyring — or in the encrypted file vault where there is no
keyring — and nowhere else: not in the database, not in the account configuration, not in logs, not in
RPC responses, not in error messages. Passwords are typed at a prompt and never taken as arguments, so
they cannot end up in your shell history. For a password account, `setup` verifies the login before
storing anything, and a failed attempt stores nothing; a Microsoft sign-in stores its grant as soon as
the browser step completes, before the mailbox is checked (chapter 2).

## Who a message can claim to be from

A draft's From must be your account address or an identity you added (chapter 8), whichever surface
drafts it: Core refuses anything else (exit 2, `sender_not_allowed`) before a message is queued, and
writes an audit row. An agent can use an identity but cannot add one: `account identity add` needs a
terminal confirmation, or `MAILCODED_ALLOW_IDENTITY_CHANGE=1` with `--yes`, and the MCP server has no
tool for it. Your mail server still decides whether it accepts the address. Signatures are not gated;
their text is in every send preview, and they change neither the sender nor the recipients.

## Drafts on the server

With `account server-drafts on` (chapter 8), `draft`, `reply` and the MCP `send_preview` also put the
draft in the account's own Drafts folder on your mail server. That needs no send unlock, because it is not
sending and not exfiltration: the message lands only in your own account, flagged as a draft, and nothing
leaves it until you send it. What the unlock protects is turning the setting on, since an agent that could
do that would put its drafts in front of every mail program you use. Each server copy is made from the
bytes that would be sent, so it has no Bcc header. After a send the copy is moved into Sent with one MOVE
when the server supports it, and left where it is otherwise. mailcoded never deletes, flags for deletion
or expunges mail on a server, and a move is tried once.

## The people you write to

`mailcoded correspondents` and the terminal client's `ctrl-n` offer the people you have written to. The
list is built from outgoing mail only, so received mail, which anyone can send you, can never put an
address on it. It lives in `store.db` beside the mail it was learned from, goes with the account on
`account forget`, and is covered by a backup. The CLI will print it to an agent that runs it, but it
holds nothing an agent cannot already read by searching Sent mail, and the MCP server has no tool for
it.

## A backup is a copy of everything

`mailcoded backup` (chapter 3) writes a plaintext copy of the whole store and its attachments, which is
exactly what the agent rules keep an agent away from. So it follows the raw-SQL pattern: off for an
agent unless you set `MAILCODED_ALLOW_BACKUP=1`, absent from MCP, audited whether allowed or refused,
and never written inside the live store or a workspace folder. It never contains a credential. Like
every gate here it stops an agent that plays by the rules; an agent that can set its own environment or
start its own daemon can pass it, so where that matters, run the agent in an OS sandbox.

## "Verified" is not "safe"

`is:verified` (chapter 5) means a mail server you chose to trust reported that the sender's domain
passed DMARC. That tells you the From address was not forged, and nothing about whether the message
is honest or harmless: a scammer passes DMARC for a domain they own, and a hijacked account passes
too. Treat verified mail exactly as hostile as any other. Mailcoded only believes an
`Authentication-Results` header written by a server on your trust list (`account trust`, chapter 8),
because a sender can type that header themselves; with nothing trusted, nothing is verified.

## Encrypted mail stays unread

Mailcoded does not decrypt PGP or S/MIME mail and has no setting that makes it. Encrypted messages are
marked (`is:encrypted`, chapter 5) and their bodies stay ciphertext, so an agent working for you sees
the marker and nothing more. This is a scope choice and a safety one: text decrypted for you must not
be something an agent can read or the store can keep. If decryption is ever added, it will be for you
at a person's keyboard only, never the CLI or the MCP server, and the plaintext will not be saved.

## Sending needs a human

Every send — TUI, CLI or MCP — is two-phase: a preview that lists every recipient and mints a one-time
token, then a send that consumes it. The token is single-use, bound to the exact bytes previewed,
expires in ten minutes, and is never logged or shown by the TUI. The command line is additionally
treated as an agent surface: `MAILCODED_SEND=1`, a recipient allowlist and a budget of five sends an
hour apply to it (chapter 7). Every attempt is audited, allowed or not.

## What it talks to

Your IMAP and SMTP servers. For a Microsoft sign-in, Microsoft's login endpoint. Nothing else: no
telemetry, no update check, no analytics. That includes links inside mail: mailcoded never fetches or
opens a URL from a message, and it refuses RFC 8058 one-click unsubscribe for that reason. An
unsubscribe by `mailto:` is ordinary outgoing mail and goes through the two-phase send.

## Where the guarantees stop

Honest limits:

- The store is a file on your disk with your user's permissions. Anyone who can read it can read your
  mail and your audit trail.
- An agent you give read access to can read everything, one query at a time. mailcoded narrows what an
  agent can *do*, not what it can *see*. See [docs/agents.md](https://github.com/MailCoded/mailcoded/blob/main/docs/agents.md).
- Verification is only as good as the server you trusted. If that server passes along a header a
  sender wrote with the server's own name on it instead of stripping it, a forged result can look
  genuine. mailcoded reads only the topmost trusted header, which blunts this without eliminating it.
- The Microsoft sign-in currently borrows a public client registration (chapter 2). The consent screen
  names another application, and that registration is outside this project's control.
