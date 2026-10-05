# 7. Sending

[← Contents](./)

Sending is two-phase everywhere — the TUI, the CLI and the MCP server — because a mail tool that can
be scripted can be scripted into sending the wrong thing:

1. **Draft.** Build the message and queue it. Drafting is always allowed, and never sends.
2. **Preview.** See exactly who would receive it, and receive a single-use **confirm token** bound to
   those exact bytes, valid for ten minutes.
3. **Send.** Hand the token back. A spent, expired or mismatched token is refused.

In the TUI that is `ctrl-s`, the confirmation screen, and `Y` (chapter 4). From the command line it is
three verbs.

## Who the gate applies to

This surprises people, so it comes first. The command line is deliberately an **agent surface** — the
same thing an AI agent would drive — and the agent controls apply to it whoever is typing. From the
CLI, `send-draft` needs, on top of the token:

- `MAILCODED_SEND=1` in the environment (`true` and `yes` also work);
- every recipient — To, Cc and Bcc — matching `MAILCODED_APPROVED_RECIPIENTS`, a comma-, semicolon-
  or whitespace-separated list of exact addresses (`bob@example.com`) or domains (`@example.com`,
  `*@example.com`). An empty or unset list approves nobody, so `MAILCODED_SEND=1` on its own changes
  nothing;
- room in the budget of **five sends per rolling hour**, which every agent-surface process on the
  machine shares.

Every attempt writes an audit row, whether it was allowed or denied.

The TUI connects to the daemon as an editor-style client, which is not an agent surface. It needs the
token and nothing else; its confirmation screen is the supervision.

## `draft`

    mailcoded draft --to <addr> --subject <text> (--body-file <path> | --body-stdin) [options]

| Option | |
|---|---|
| `--to <addr>` | repeatable; at least one |
| `--cc <addr>`, `--bcc <addr>` | repeatable |
| `--subject <text>` | required |
| `--body-file <path>` | plaintext body, UTF-8, at most 1 MiB |
| `--body-stdin` | read the body from stdin instead |
| `--from <addr>` | send as this address: the account's own, or one of its identities (see below) |
| `--signature <name>` | insert this signature |
| `--no-signature` | insert none; otherwise the account's default is inserted |
| `--reply-to <id>` | the local message id being replied to; sets In-Reply-To and References |
| `--in-reply-to <mid>` | a raw Message-ID being replied to, without angle brackets |
| `--account <id>` | which account to send as |

    $ mailcoded draft --to bob@example.org --subject 'Re: invoice' --body-file /tmp/reply.txt
    draft_id:   1
    message_id: 24711edcc05243e19fc5ee2ad1440847@example.com
    from:       you@example.com
    to:         bob@example.org
    subject:    Re: invoice
    size:       283 bytes
    send gate:  denied
    next:       mailcoded send-preview 1

`send gate: denied` is the gate's answer *right now*, so you learn before writing a whole reply chain
that sending is off. `draft` never prints a token.

## Sending as another address

An account sends as its own address, and as any **identity** you add to it:

    mailcoded account identity add sales@example.com --name 'Sales Desk' --reply-to help@example.com
    mailcoded draft --from sales@example.com --to bob@example.org --subject 'Quote' --body-file q.txt

`--from` is checked in Core, so it holds for the command line, the MCP tools, the daemon and the
terminal client alike. The address is compared without regard to case, and the message goes out with
the spelling you configured, that identity's display name (the account's, when it has none) and its
Reply-To as a `Reply-To` header. Anything else is refused:

    $ mailcoded draft --from boss@example.com ...
    sender_not_allowed: The From address is not this account's address or one of its configured sender identities.

That is exit 2, error name `sender_not_allowed`, and nothing is queued. Your mail server has the last
word on whether it accepts a From address, and mailcoded cannot ask it which ones your login may use,
so add only addresses it lets you send as. Chapter 8 covers `account identity`.

A reply is sent as the identity the original was addressed to: the first address in its To, then Cc,
that the account owns; otherwise the account address. `reply` has no `--from`; to choose another address use `draft --reply-to <message id> --from <address>`.

## Signatures

A signature is plain text kept per account under a name (chapter 8, `account signature`). Each account
can have one for new messages and another for replies. `draft` and `reply` insert the default one;
`--signature <name>` picks another and `--no-signature` leaves it out. It goes after a line holding
`-- ` (two dashes and a space, RFC 3676), so mail programs know where the message ends:

    Thanks, I'll look at it today.

    -- 
    Alex Example
    Example Pty

In a reply the signature sits between your text and the quoted original, not under the quote. A
default signature is not added to a body that already contains its own `-- ` line, so a body you
signed by hand is not signed twice. The signature is part of the draft, so `send-preview` shows it
and the confirm token covers it. The terminal client puts the default signature into the compose
buffer, where you can edit it. HTML mail and per-message signature files are not supported.

### Drafts on the server (opt-in)

By default a draft exists only in mailcoded's own outbox. Turn on `mailcoded account server-drafts on`
and each new draft is also appended to the account's Drafts folder on the mail server, flagged as a
draft, so other mail programs show it. Switching it on needs a terminal confirmation (or
`MAILCODED_ALLOW_SERVER_DRAFTS=1` with `--yes`); switching it off needs nothing. `draft` and
`send-preview` report `server_draft`: `off`, `saved`, or `failed` (with `server_draft_reason`). A
failure, such as an unreachable server or an account with no Drafts folder, never stops the draft: it
simply exists locally only.

The local draft is still the one that is sent, with the same preview and one-time token. The server copy is
only a copy: it is never read back, and editing it elsewhere changes nothing about what is sent. It holds
exactly the bytes that will go out, so it has no Bcc header; blind recipients are kept only in mailcoded's
outbox. Each `draft` makes its own server copy, and one you abandon stays in Drafts for you to discard in
your usual mail program, because mailcoded never deletes mail on a server.

After a successful send, mailcoded does the one thing that is safe and otherwise leaves the draft alone:

| Situation | What happens to the server draft | Sent copy |
|---|---|---|
| The server supports MOVE (RFC 6851), the account keeps a Sent copy, and the draft is still where it was saved | moved into Sent with one command, and its draft flag cleared | that moved message; nothing is appended |
| The server has no MOVE | left in Drafts (`no-move-capability`) | appended as usual |
| Gmail, or any server that saves sent mail itself | left in Drafts (`server-saves-sent`) | the server's own |
| `--no-append` | left in Drafts (`sent-copy-not-wanted`) | none |
| The Drafts folder was renumbered, or another client replaced the draft | left in Drafts (`uidvalidity-changed`, `draft-gone`) | appended as usual |
| The connection dropped during the move | left; it may already have moved (`move-unconfirmed`) | none, so there is never a second one |
| mailcoded stopped between the send and the move | left (`interrupted`), never retried | as before the crash |

`send-draft` reports the outcome as `server_draft` (`moved` or `left`) and, when left, `server_draft_reason`.
There is no case in which anything is deleted, flagged for deletion or expunged, and a move is attempted at
most once. On Gmail the draft stays under Gmail's Drafts after a send; discard it there.

Recipients you have written to before can be completed from the terminal client (`ctrl-n`, chapter 4)
or listed with `mailcoded correspondents` (chapter 5). Each successful send adds its To and Cc
recipients to that list, once.

## `reply`

    mailcoded reply <id> [--all] [--body <text> | --body-file <path>] [--no-quote] [--no-fetch]
                         [--signature <name> | --no-signature]

Builds a reply draft from an existing message: the recipients (`--all` adds everyone on the original),
a `Re:` subject, the quoted original (`--no-quote` leaves it out), and the In-Reply-To and References
chain that keeps the conversation threaded for the recipient. It fetches the original body if it has
to; `--no-fetch` stays offline. The result is a draft, exactly as from `draft`. It is sent as the
identity the original was addressed to, and `--all` leaves every one of your own addresses, not only
the account address, out of Cc.

## `send-preview`

    mailcoded send-preview <draftId>

    draft_id:      1
    message_id:    24711edcc05243e19fc5ee2ad1440847@example.com
    from:          you@example.com
    to:            bob@example.org
    size:          283 bytes
    send gate:     denied
    confirm_token: (a long random string)
    expires:       2026-09-06T02:10:15.126Z
    note: Show this preview to the human and let the human decide before running send-draft.
    note: The token is single use, expires at the time shown, and is bound to these exact bytes: ...

Read the recipients. The token is a bearer capability for those ten minutes: whoever can read this
output can spend it. Do not paste it anywhere it will be kept.

## `send-draft`

    mailcoded send-draft <draftId> --confirm-token <token> [--no-append]

Phase two. By default a copy of the sent message is appended to the account's Sent folder (or,
for a draft saved to the server, that draft is moved there; see above); `--no-append` skips that.
Refusals, by exit code:

| Exit | |
|---|---|
| 6 | no valid token: missing, spent, expired, or the draft changed since the preview |
| 4 | a gate is closed — `MAILCODED_SEND` unset, or a recipient not on the allowlist |
| 5 | over the hourly budget; the error carries `retry_after_ms` |
| 7 | the SMTP server rejected the credential — `mailcoded account reauth` |
| 8 | network; safe to retry |

## `outbox`

    mailcoded outbox [--state queued|sending|sent|failed]

What is queued, sending, sent or stuck: the first place to look when a send misbehaves.

    1  queued   bob@example.org

A `queued` row nobody has previewed is simply a draft. A `failed` row carries the SMTP reply when
there was one.

## What you cannot do yet

There is no attachment upload and no HTML compose in this release. Bodies are plaintext.
