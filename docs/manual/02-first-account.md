# 2. Your first account

[← Contents](README.md)

    mailcoded setup

`setup` is interactive and needs a terminal. It works out your provider's IMAP and SMTP settings from
your address, tells you which kind of credential that provider actually accepts, proves the settings
work with a real login, and only then writes anything down. A failed password attempt leaves no
account and no stored credential behind.

## What it asks

1. **Your email address.** Skip the question with `--email you@example.com`.
2. **Whether the detected settings look right.** Edit them if not. `--imap-host`, `--imap-port`,
   `--smtp-host` and `--smtp-port` pre-fill them.
3. **Your password, or app password.** Typed at a prompt, never echoed, and never a command-line
   argument, so it cannot land in your shell history. It goes to the OS keyring (chapter 3) — never to
   the database, the config, or a log line.
4. **Whether to sync now.** The first sync of a large mailbox takes a while. Say no and run
   `mailcoded sync --account <id>` later if you prefer.

`--display-name <label>` gives the account a label. `--json` also prints a machine-readable summary on
stdout; the prompts go to stderr, so stdout stays clean.

## Providers it knows

Settings are built in for Gmail, Outlook.com, Yahoo, iCloud, Fastmail, AOL, Zoho, GMX, WEB.DE, Yandex,
Mail.ru, QQ, Foxmail, NetEase (163/126) and Proton Bridge. Any other domain is guessed as
`imap.<your-domain>` and `smtp.<your-domain>`, which you correct at step 2.

### App passwords

Many providers reject your ordinary account password over IMAP and require an **app password** that
you generate in their security settings — Gmail, Yahoo, iCloud, Fastmail, AOL, Zoho and QQ among
them. `setup` says so, with the link, before it asks you to type anything. Gmail additionally requires
2-Step Verification to be on before it will issue one, and WEB.DE needs IMAP enabled in its web
interface first.

If the login fails, `setup` prints a one-line diagnosis — `The server refused that credential.`, or
`Could not reach <host>:<port>.` — with hints, saves nothing, and tells you to run it again.

### Microsoft accounts

For outlook.com, hotmail.com, hotmail.co.uk, live.com, live.co.uk, msn.com and office365.com addresses,
`setup` offers a choice:

    How would you like to sign in?
      1. Sign in with Microsoft (opens a browser, recommended)
      2. Use a password or app password
      3. Cancel

Microsoft has been withdrawing basic authentication, so a plain password is usually refused; an app
password may still work if your account has them enabled. Signing in with Microsoft is the durable
option. It is a **device-code** sign-in: `setup` prints a web address and a short code —

      Open:  https://microsoft.com/devicelogin
      Code:  ABCD-EFGH

      Waiting for you to finish in the browser...

— you open the page on any device, enter the code, and approve. The grant is stored in the keyring, and
from then on mailcoded refreshes its own access tokens. If Microsoft does not hand back a code within
45 seconds you are told `Microsoft did not return a sign-in code within 45s. Check the network, then
try again.`

Two things to know:

- **The consent screen will not say "mailcoded".** mailcoded has no OAuth client registration of its
  own yet, so it borrows a public one — and Microsoft may refuse or revoke that at any time. To use
  your own, register a public-client app with device-code flow enabled and pass `--client-id <guid>`,
  or set `MAILCODED_OAUTH_CLIENT_ID`. `--tenant` (or `MAILCODED_OAUTH_TENANT`) selects `common` — the
  default, personal and work accounts — or `consumers`, `organizations`, or one tenant's GUID.
- It asks for two permissions: IMAP access as you, and sending mail over SMTP. Nothing else.
- **The grant is stored as soon as the browser step completes**, before the mailbox login is checked.
  If the mailbox then refuses the token, no account is created, but the grant stays in the keyring; a
  later successful `setup` for the same address reuses it.
- **A Microsoft 365 mailbox on your own domain is not recognised as Microsoft.** It is treated as an
  unknown provider — guessed hosts, password only — and the sign-in choice is not offered. Point it at
  `outlook.office365.com` with `--imap-host`; it will work only if your tenant still allows an app
  password.

## Afterwards

    mailcoded account list      # what was added
    mailcoded account test      # prove the settings and credential work; changes nothing
    mailcoded folders           # the folders it found, with counts
    mailcoded tui               # read it

## For scripts: `account add`

`setup` refuses to run without a terminal. The non-interactive form takes every setting as an option
and reads the credential from stdin:

    printf '%s' "$IMAP_PASSWORD" | mailcoded account add --json \
      --email me@example.com --imap-host imap.example.com \
      --smtp-host smtp.example.com --password-stdin

| Option | |
|---|---|
| `--email <addr>` | required |
| `--display-name <text>` | |
| `--imap-host <host>` | required |
| `--imap-port <n>` | default 993 |
| `--imap-security <mode>` | `none`, `sslOnConnect`, `startTls`, `startTlsWhenAvailable` |
| `--imap-user <name>` | defaults to the email address |
| `--smtp-host <host>` | required before this account can send |
| `--smtp-port <n>` | default 587 |
| `--smtp-security <mode>` | default `startTls` |
| `--smtp-user <name>` | |
| `--secret-ref <handle>` | reuse an existing secret handle instead of deriving one |
| `--password-stdin` | read the credential from stdin |
| `--no-password` | register without storing a credential |

An account registered with `--no-password` cannot sync until a credential is added with
`account reauth` (chapter 8); the daemon says so once and then leaves it alone, rather than retrying a
login that cannot succeed. Run `mailcoded account test` after any `account add` to prove the settings.

## More than one account

Run `setup` again. Every verb that needs to know which account you mean takes `--account`, by id or by
address — except `search`, whose `--account` is the numeric id only. With a single account it is
implied. The terminal client shows all of them in one sidebar.
