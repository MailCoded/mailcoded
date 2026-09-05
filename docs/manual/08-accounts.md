# 8. Accounts

[← Contents](./)

    mailcoded account list
    mailcoded account test    [--account <id|email>] [--no-smtp]
    mailcoded account reauth  [--account <id|email>] [--client-id <guid>] [--tenant <name>]
    mailcoded account add     ...                              (chapter 2)
    mailcoded account forget  --account <id|email> [--yes]

With one account, `--account` is implied. With more, name it by id or by address.

## `account list`

    1  you@example.com
        auth      oauth2
        imap      outlook.office365.com:993
        mail      8451 in 12 folders, 37 unread
        synced    2026-09-06 01:12:04Z

`synced` reads `never` until a folder has completed a sync.

## `account test`

Proves the settings and the credential still work, and changes nothing. It logs in to IMAP and lists
the folders, then — unless you pass `--no-smtp` — authenticates to SMTP as well. It is the first thing
to reach for when sync starts failing, because it separates "wrong host" from "dead credential", and
it tests reading and sending separately: IMAP ok with SMTP refused means you can read but not send,
and the detail line says what the SMTP server said.

## `account reauth`

Replaces the credential for an existing account. Its settings and cached mail are untouched.

- A **password** account prompts for the new password (not echoed), stores it, and verifies it by
  connecting and listing folders.
- An **OAuth** account runs the Microsoft device-code sign-in again — the same address-and-code flow
  as `setup` (chapter 2), with the same `--client-id` and `--tenant` overrides — then verifies.

    Credential replaced and verified for account 2.

If the server still refuses, the new credential has nevertheless been stored and you are told so:
`The new credential was stored but the server still refuses it: <reason>` — exit 7 for an
authentication refusal, 8 for a network failure.

`reauth` needs a terminal. For scripts, write the credential with `account add --password-stdin`
using the same `--email`.

If the TUI is running while you reauth from another terminal, it may keep reporting the old failure
for a little while, because the daemon backs off between login attempts. Restart it if it does not
recover on its own.

## `account forget`

    mailcoded account forget --account <id|email> [--yes]

Removes one account **from this machine**: its folders, cached messages, search-index entries,
unreferenced blobs, and its stored credential — the password or the OAuth grant, whichever it had.
Use it to undo a mistaken `setup`, or to stop reading an account here.

**Nothing is removed from the mail server.** This is not a delete command for mail — there is none —
and re-adding the account re-syncs everything.

It asks before acting. For an unattended run you need **both** `--yes` and
`MAILCODED_ALLOW_FORGET=1`, for the same reason `move` does (chapter 6). The verb is absent from the
MCP surface.
