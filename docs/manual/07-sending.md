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
| `--from <addr>` | override the account's own address |
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

## `reply`

    mailcoded reply <id> [--all] [--body <text> | --body-file <path>] [--no-quote] [--no-fetch]

Builds a reply draft from an existing message: the recipients (`--all` adds everyone on the original),
a `Re:` subject, the quoted original (`--no-quote` leaves it out), and the In-Reply-To and References
chain that keeps the conversation threaded for the recipient. It fetches the original body if it has
to; `--no-fetch` stays offline. The result is a draft, exactly as from `draft`.

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

Phase two. By default a copy of the sent message is appended to the account's Sent folder;
`--no-append` skips that. Refusals, by exit code:

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
