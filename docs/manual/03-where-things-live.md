# 3. Where your mail lives

[← Contents](./)

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
| `store.db`, `store.db-wal`, `store.db-shm` | the SQLite database: accounts, folders, every envelope, the search index, Tags, drafts and the outbox, and the people you have written to (learned from sent mail only), and the append-only `sync_log` audit trail. It runs in WAL mode, so the `-wal` and `-shm` files are normal. |
| `blobs/` | raw message bytes, fetched on demand |
| `model/` | where an embedding model for search-by-meaning would live. No model is pinned yet, so this directory does not exist on a current checkout ([chapter 5](05-reading-and-searching.md)). If one is ever installed, deleting it is safe: search falls back to words alone. |
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

### `mailcoded backup`

The safe way to back it up is a verb, and it works while a daemon or a sync is running:

    MAILCODED_ALLOW_BACKUP=1 mailcoded backup ~/mail-backups/2026-10-04
    MAILCODED_ALLOW_BACKUP=1 mailcoded backup-verify ~/mail-backups/2026-10-04

`backup` writes a consistent snapshot of `store.db` (SQLite's `VACUUM INTO`, taken on its own
read-only connection, so a long copy never stalls a sync), a copy of every external attachment file
that snapshot points at, and a `manifest.json` recording the schema version, the sha256 and size of
each file and the row count of every table. The destination must not exist, or be an empty folder
whose parent exists. Nothing else goes in it: **secrets are never copied**, and neither are blob
files written after the snapshot or orphaned by `account forget`. On Linux and macOS the files are
readable by you alone.

`backup-verify` opens the copy read-only (it adds no file to it), runs SQLite's integrity check,
re-hashes everything and lists every difference from the manifest: a changed byte, a missing or extra
file, a wrong row count. It exits 9 when anything differs. The manifest is not signed; it catches
corruption and edits, not someone who rewrote the whole folder.

The variable is required because a backup is a second, plaintext copy of all your mail: the command
line is an agent surface (chapter 11) and an agent must not be able to mint one unasked. Set it for the
one command, as above. MCP has no backup tool at all.

Refused destinations, each with a message saying why: a folder inside the live store; a symbolic link;
a non-empty folder or a file; a relative path in the daemon's RPC (the verb resolves it against your
working directory); on Windows a device path (`\\?\`, `\\.\`), a colon in a name, a reserved name such as `CON`
or a name ending in a dot or space; and **any folder inside a workspace** — below your home folder, a
folder holding `.git`, `.vscode`, `.obsidian`, `.idea` or a `*.code-workspace` file, or sitting under one.
The mail store never lives inside a workspace folder, and a backup is the mail store; pick a folder
outside every project, for example `~/mail-backups`. A backup you write by mistake inside a workspace
is yours to move; mailcoded will not write it there in the first place.

A failed backup removes what it wrote, so you can run it again into the same folder. It fails rather
than copy damage: if a stored attachment file is missing, or its content no longer matches its hash,
nothing is written and the message names the file.

### Restoring

There is no restore verb: replacing a live store is not something a command should do unasked.

1. Run `mailcoded backup-verify` on the backup.
2. Stop the daemon, close the TUI, and make sure no `mailcoded` or `mailcoded-mcp` process is running.
3. Move the current data directory aside (do not delete it).
4. Copy `store.db` and the `blobs` folder from the backup into the data directory. Do not copy
   `manifest.json` in. Do not leave an old `store.db-wal` or `store.db-shm` beside the restored
   file.
5. Run `mailcoded account reauth` for every account: **the backup has no credentials**. Then `mailcoded sync`.

The restored database remembers where each attachment file lived on the old machine, and a store that
cannot find one there looks under its own `blobs` folder, so restoring to a different path or user
works.

### Copying by hand

You can still copy the whole directory while nothing is running (no TUI open, `daemon.lock` free). To
move it, copy it and set `MAILCODED_DATA_DIR`. Credentials in the OS keyring do not travel with the
directory, and on Windows neither does a DPAPI-protected `secrets.enc`; on a new machine — or as a
different Windows user — run `mailcoded account reauth`. Copying `store.db` alone while a daemon runs
can yield a torn file; use `backup`.

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
