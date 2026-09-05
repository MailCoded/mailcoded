# 3. Where your mail lives

[← Contents](README.md)

mailcoded keeps everything in one **data directory**:

| | |
|---|---|
| Linux | `$XDG_DATA_HOME/mailcoded`, normally `~/.local/share/mailcoded` |
| macOS | `~/Library/Application Support/mailcoded` |
| Windows | `%LOCALAPPDATA%\mailcoded` |

Three overrides, in increasing order of specificity: the `MAILCODED_DATA_DIR` environment variable,
`--data-dir <path>`, and `--db <path>`, which names `store.db` itself. `mailcoded tui` passes
`--data-dir` and `--db` through to the client and its daemon.

## What is in it

| | |
|---|---|
| `store.db`, `store.db-wal`, `store.db-shm` | the SQLite database: accounts, folders, every envelope, the search index, Tags, drafts and the outbox, and the append-only `sync_log` audit trail. It runs in WAL mode, so the `-wal` and `-shm` files are normal. |
| `blobs/` | raw message bytes, fetched on demand |
| `secrets.enc`, `secrets.key` | the encrypted-file credential vault and, on Linux and macOS, the machine key that unlocks it. They appear only once a credential has actually been written to the file backend; with a working keyring their absence is normal. On Windows there is no `secrets.key` — the vault key is DPAPI-wrapped inside `secrets.enc` |
| `daemon.lock` | the OS lock that makes one daemon the owner of this store's live connections (chapter 9) |
| `daemon.owner` | a diagnostic stamp — pid, start time, version — of the current or most recent owner; nothing reads it to make a decision |

## Secrets

Credentials never touch the database, the account configuration, a log line, an RPC response or an
error message. They go to a **secret store**, which is a chain: the OS keyring first, an encrypted
file as the fallback.

| Platform | Keyring | Fallback |
|---|---|---|
| Linux | libsecret — the Secret Service API that GNOME Keyring and KWallet provide | encrypted file |
| macOS | Keychain | encrypted file |
| Windows | Credential Manager | DPAPI-protected file |
| Headless, containers | — | encrypted file only |

`mailcoded health` tells you which chain is active — `secrets: chain(libsecret,file)`, say.
`MAILCODED_SECRET_BACKEND` forces one: `auto` (the default), `file`, `libsecret`, `keychain` or
`wincred`.

The file vault is `secrets.enc`. On Linux and macOS it is unlocked by the machine key in `secrets.key`
beside it; copy the two files together or not at all. On Windows the key is wrapped with DPAPI for the
current user and kept inside `secrets.enc` itself, so the file opens only for the Windows account that
created it, on that machine. On any platform, a passphrase from `MAILCODED_SECRET_KEY` — or from a file
named by `MAILCODED_SECRET_KEY_FILE` — takes the place of the machine key, if it is set when the vault
is first created.

A Microsoft sign-in stores its grant in the same secret store, under the account's handle with
`:oauth-cache` appended. `account forget` removes both.

## Backups and moving

Mail is re-syncable: the server is the source of truth, and a fresh sync is always a valid recovery.
What exists **only** in your store is:

- your local Tags — anything other than `unread`, `flagged`, `replied` and `draft`;
- drafts and the outbox;
- the `sync_log` audit trail.

To back it up, copy the whole directory while nothing is running: no TUI open, `daemon.lock` free. To
move it, copy it and set `MAILCODED_DATA_DIR`. Credentials in the OS keyring do not travel with the
directory, and on Windows neither does a DPAPI-protected `secrets.enc`; on a new machine — or as a
different Windows user — run `mailcoded account reauth`.

For experiments, use a throwaway store:

    mailcoded --db /tmp/mail.db import-eml fixtures/eml
    mailcoded --db /tmp/mail.db tui

## Editors

Never open the data directory as an editor workspace, and exclude it from any indexer or file
watcher: a large SQLite database and its WAL would otherwise be re-indexed on every write. For
VS Code:

    {
      "files.watcherExclude": { "**/mailcoded/**": true, "**/*.db": true, "**/*.db-wal": true, "**/*.db-shm": true },
      "search.exclude":       { "**/mailcoded/**": true, "**/*.db": true, "**/*.db-wal": true, "**/*.db-shm": true }
    }

## Windows

- **Enable long paths.** The store root is kept short on purpose, but blob and export paths can still
  exceed `MAX_PATH`. Set `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled` to `1`
  (a reboot applies it), or enable *Enable Win32 long paths* in Group Policy.
- **Consider a Defender exclusion** for `%LOCALAPPDATA%\mailcoded`. Real-time scanning of a busy
  SQLite file and its WAL costs sync throughput. It is your trade-off to make — mail content is what
  would be scanned:
  `Add-MpPreference -ExclusionPath "$env:LOCALAPPDATA\mailcoded"`.
