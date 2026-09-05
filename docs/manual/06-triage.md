# 6. Triage

[← Contents](README.md)

## Tags and Flags

mailcoded keeps two kinds of marker on a message, and the words are deliberate.

- **Flags** belong to the server: `unread`, `flagged`, `replied`, `draft`. Every mail client you own sees
  them. **The server wins**: whatever it says on the next sync is what you get.
- **Tags** are local to this machine: `triaged`, `followup`, `project-x`, anything you like. **Local
  wins**: sync never touches them.

The four Flag names are also Tags, so one command handles both. Set `unread` and mailcoded pushes the
IMAP flag; set `triaged` and it stays on your machine. Nothing is ever called a "label".

A Tag name is at most 128 characters of printable ASCII, with no whitespace and none of
`" ' ( ) * : ; , \ /`, and is lower-cased.

## `tag`

    mailcoded tag <id> +tag -tag [...] [--local]

`+` adds, `-` removes; the sign is required.

    $ mailcoded tag 37 +triaged --local
    37: triaged,unread
    flags: unread  pushed_to_server=false
    the server push was deferred (no provider connection); the local tags are saved.

The first line is the message's complete Tag set afterwards; the second says which of them are Flags
and whether they reached the server. `--local` applies the change locally and opens no connection, which
is why the push above was deferred. Use it for your own Tags, which never go to the server anyway.
**Do not** use it for the four Flag names: a local-only change to `unread` or `flagged` is reverted by
the next sync, because the server wins.

Without `--local`, mailcoded connects and pushes the Flags in the same call. If the server cannot be
reached, the local change is still saved and the same `deferred` line tells you so.

## `move`

    mailcoded move <id> --folder <name|id>

Moves the message to another folder of the same account, on the server. `--folder` takes a folder
id, a full path (`Projects/Acme`) or a leaf name (`Acme`), matched case-insensitively; an unknown name
is answered with the list of folders the account has.

The command line is treated as an agent surface (chapter 11), and moving mail is outside the default
agent posture, so on the CLI `move` and `archive` need `MAILCODED_ALLOW_MOVE=1` in the environment
**in every case** — a flag alone is something an agent could pass; the environment variable is a
change a human had to make deliberately. With it set, a terminal still asks first —
`Move message 37 to Archive? [Y/n]:`, and Enter accepts — and an unattended run passes `--yes` instead
of answering.

Without the variable, a script is refused before anything is looked up:

    mailcoded: invalid_params (-32602): Moving mail is outside the default agent posture. Re-run from a
    terminal to confirm, or set MAILCODED_ALLOW_MOVE=1 and pass --yes for an unattended run.

and an interactive run is stopped by the core gate *after* you have answered the prompt, with exit 4:
`Moving mail is not part of the agent surface.` The first message's suggestion to re-run from a
terminal is not enough on its own; set the variable. The TUI is not an agent surface and needs none of
this — `m` just works.

`move` does not exist on the MCP surface at all.

## `archive`

    mailcoded archive <id>

A `move` to the folder the server marks with the Archive special-use role, confirmed the same way. If
the account has no such folder you are told to use `move --folder` instead.

## There is no delete

No verb removes mail. There is no `delete`, `expunge`, `trash` or `purge` — not in the CLI, the TUI,
the MCP server or the protocol — and no flag that enables one. This is deliberate: a tool that cannot
delete cannot be talked into deleting, and re-syncing from the server is always a safe way back.

What you can do is move a message to your provider's Trash or Deleted Items folder with `move`, and
let the server apply its own retention. mailcoded moves it; the server removes it, on its own
schedule.
