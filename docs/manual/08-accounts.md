# 8. Accounts

[← Contents](./)

    mailcoded account list
    mailcoded account test    [--account <id|email>] [--no-smtp]
    mailcoded account reauth  [--account <id|email>] [--client-id <guid>] [--tenant <name>]
    mailcoded account trust   [--account <id|email>] [--authserv-id <id>]... [--none] [--yes]
    mailcoded account identity  <list|add|forget> [<address>] [--name <text>] [--reply-to <addr>] [--yes]
    mailcoded account signature <list|set|forget|default> [<name>] [--file <path> | --stdin] [--none]
    mailcoded account add     ...                              (chapter 2)
    mailcoded account forget  --account <id|email> [--yes]

With one account, `--account` is implied. With more, name it by id or by address.

## `account list`

    1  you@example.com
        auth      oauth2
        imap      outlook.office365.com:993
        mail      8451 in 12 folders, 37 unread
        synced    2026-09-06 01:12:04Z

`synced` reads `never` until a folder has completed a sync. A `verified` line lists the
`authserv-id`s this account trusts (see `account trust`), or says none is trusted.

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

## `account trust`

    mailcoded account trust [--account <id|email>] [--authserv-id <id>]... [--none] [--yes] [--json]

Decides which mail server's `Authentication-Results` headers mailcoded believes, which is what
`is:verified` rests on (chapter 5). Without options it shows the list; it never changes anything
unless you pass `--authserv-id` or `--none`.

**Why a list at all.** Anyone can write `Authentication-Results: mx.example.net; dmarc=pass` into a
message they send. The only header worth reading is the one your own mail server added, so mailcoded
reads a header only if the name that starts it, its *authserv-id*, is one you have trusted. With none
trusted, nothing is verified. Gmail (`imap.gmail.com`) trusts `mx.google.com` without any setup.
For other providers the list starts empty, because a name is only safe to trust if you have checked it.

**Finding your server's authserv-id.** Open a message you know is genuine, in your mail program's
"show original" or "view source", and find the topmost line that begins `Authentication-Results:`.
The first word after the colon is the id, for example `mx.example.net`; what follows (`dkim=pass`,
`dmarc=pass`, and so on) is the server's verdict. Check that the same id appears on several genuine
messages, and that a message you know is spoofed does not show a passing `dmarc=` under that id.
Only trust a name your own provider's server writes and strips from incoming mail: if it does not
strip a header a sender wrote with its name on it, that name cannot be trusted.

    mailcoded account trust --account me@example.com --authserv-id mx.example.net

An id is an exact host name, or `*.example.net` to match any host below it (at least two name
parts after the star). Up to 16 ids; `--authserv-id` replaces the whole list, accepts commas, and `--none`
empties it (the provider's built-in id, if any, stays).

Which servers you trust decides what "verified" means, so the change is gated like `move`: it asks
for confirmation at a terminal, and an unattended run needs `--yes` **and**
`MAILCODED_ALLOW_TRUST_CHANGE=1`. It is absent from the MCP surface, and an agent must not run it.
`account add` and `setup` also take `--trust-authserv-id <id>` for a new account; re-adding an
address that already exists leaves its list alone.

Changing the list clears the recorded DMARC results of that account, so nothing stays verified under
a list you no longer hold. A message whose body is stored gets a result again the next time you read
it; other mail gets one when it is synced or its body is fetched. Mail loaded with `import-eml` is
never verified, and changing the list does not change that.

## `account identity`

    mailcoded account identity list
    mailcoded account identity add sales@example.com [--name 'Sales Desk'] [--reply-to help@example.com] [--yes]
    mailcoded account identity forget sales@example.com

The extra addresses an account may send as. The account's own address is always allowed and cannot be
forgotten. A draft whose From is neither it nor an identity is refused with exit 2 (chapter 7), whoever
drafts it. `add` again for the same address, in any case, updates it. `--name` is the display name used
in From for that address (at most 100 characters, no control characters), and `--reply-to` adds a
Reply-To header to mail sent as it. An account can have 32 identities. Identities also decide which Sent
mail counts as yours when mailcoded learns whom you write to (chapter 5, `correspondents`): a message in a
Sent folder teaches nothing unless its From is the account address or an identity.

mailcoded cannot discover your aliases; IMAP has no way to ask. Your mail server decides whether it
accepts a From address, so add only the ones it lets this login use.

An identity widens who your mail can claim to be from, so `add` is gated like `account trust`: it asks
for confirmation at a terminal, and an unattended run needs `--yes` **and**
`MAILCODED_ALLOW_IDENTITY_CHANGE=1`. It is absent from the MCP surface, and an agent must not run it.
`forget` only narrows the list and needs no confirmation. Every change writes an audit row. `setup` and
`account add` leave an existing account's identities alone.

## `account signature`

    mailcoded account signature list
    mailcoded account signature set work --file ~/signature.txt        (or --stdin)
    mailcoded account signature default new work
    mailcoded account signature default reply short
    mailcoded account signature default new --none
    mailcoded account signature forget work

Named plain-text signatures, with a default for new messages and another for replies (chapter 7 shows
where one lands). A name is 1 to 32 letters, digits, `.`, `_` or `-` and is not case-sensitive. A
signature is at most 2000 characters and 20 lines, and an account holds 16. Control characters, bidi
overrides and zero-width characters are stripped, and a first line of `--` is dropped because the
separator is added for you. A signature over the limit is refused, never cut short. `forget` clears
any default that named the signature.

Changing a signature needs no confirmation: its text appears in every `send-preview`, and it changes
neither who the mail is from nor who receives it. Each change writes an audit row with the name and the
length, never the text.

## `account forget`

    mailcoded account forget --account <id|email> [--yes]

Removes one account **from this machine**: its folders, cached messages, search-index entries,
its list of correspondents, unreferenced blobs, and its stored credential — the password or the OAuth grant, whichever it had.
Use it to undo a mistaken `setup`, or to stop reading an account here.

**Nothing is removed from the mail server.** This is not a delete command for mail — there is none —
and re-adding the account re-syncs everything.

It asks before acting. For an unattended run you need **both** `--yes` and
`MAILCODED_ALLOW_FORGET=1`, for the same reason `move` does (chapter 6). The verb is absent from the
MCP surface.
