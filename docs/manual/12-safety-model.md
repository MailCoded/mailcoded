# 12. The safety model, in plain terms

[← Contents](README.md)

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

## Sending needs a human

Every send — TUI, CLI or MCP — is two-phase: a preview that lists every recipient and mints a one-time
token, then a send that consumes it. The token is single-use, bound to the exact bytes previewed,
expires in ten minutes, and is never logged or shown by the TUI. The command line is additionally
treated as an agent surface: `MAILCODED_SEND=1`, a recipient allowlist and a budget of five sends an
hour apply to it (chapter 7). Every attempt is audited, allowed or not.

## What it talks to

Your IMAP and SMTP servers. For a Microsoft sign-in, Microsoft's login endpoint. Nothing else: no
telemetry, no update check, no analytics.

## Where the guarantees stop

Honest limits:

- The store is a file on your disk with your user's permissions. Anyone who can read it can read your
  mail and your audit trail.
- An agent you give read access to can read everything, one query at a time. mailcoded narrows what an
  agent can *do*, not what it can *see*. See [docs/agents.md](../agents.md).
- The Microsoft sign-in currently borrows a public client registration (chapter 2). The consent screen
  names another application, and that registration is outside this project's control.
