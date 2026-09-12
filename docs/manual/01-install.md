# 1. Installing

[← Contents](./)

There are no release binaries yet. You build from source, and the installer puts four commands on your
PATH. The first time takes a few minutes, because Native AOT compilation is slow; after that it is a
rebuild.

## What you need

- The **.NET 10 SDK**. On NixOS, or under WSL with Nix, run `nix develop` in the checkout first.
- `git`.
- For the full-screen client, a terminal that understands ANSI escape sequences. Every Linux and macOS
  terminal does. On Windows use **Windows Terminal**; the legacy console is refused with
  `This console cannot render ANSI. Try Windows Terminal.`

## Install

    git clone https://github.com/MailCoded/mailcoded
    cd mailcoded
    scripts/install.sh

That publishes and installs:

| Command | What it is |
|---|---|
| `mailcoded` | the command-line tool — every verb in chapters 5 to 10 |
| `mailcoded-tui` | the full-screen terminal client (chapter 4) |
| `mailcoded-daemon` | the engine the TUI talks to; JSON-RPC over stdio (chapter 9) |
| `mailcoded-mcp` | the MCP adapter for agent hosts that cannot run a shell (chapter 11) |

Options:

| | |
|---|---|
| `--prefix DIR` | install root; default `$HOME/.local`, or `$PREFIX` |
| `--rid RID` | build for another runtime identifier, such as `osx-arm64` or `win-x64` |
| `--no-aot` | install the framework-dependent build instead: much faster to install, but needs the .NET runtime present to run |
| `--uninstall` | remove the symlinks and the payload directory |

Make sure `$PREFIX/bin` — normally `~/.local/bin` — is on your PATH.

## Where it goes, and why

The binaries land in `$PREFIX/libexec/mailcoded/`, and only symlinks go into `$PREFIX/bin`. Two reasons
not to "tidy" this:

- The AOT binaries load `libe_sqlite3.so` from their own directory and will not start without it. The
  executable and the library have to stay together; a symlink to the executable is fine.
- `$PREFIX/share/mailcoded` is deliberately **not** used, because on Linux that path is
  `$XDG_DATA_HOME/mailcoded` — your mail store. The installer refuses to write over, or remove, any
  directory that looks like one.

`mailcoded tui` looks for `mailcoded-tui` and `mailcoded-daemon` beside its own binary first, then on
PATH, so an installed set stays together even if an older copy is lying around elsewhere.

## Check it

    mailcoded version
    mailcoded-tui --check

The first prints the version and protocol numbers and opens nothing:

    mailcoded 0.1.0
    protocol 1, output schema 1
    .NET 10.0.10 on linux-x64

The second starts a daemon, connects to it over the same wire the TUI uses, reports, and exits:

    daemon      0.1.0
    methods     21
    maxSearch   200
    accounts    0
    ok

`accounts 0` is right before you have added one. If `--check` fails instead, chapter 13 lists the
messages.

## Sizes

Measured on 2026-09-12 on one linux-x64 machine. Three of the four are Native AOT: `mailcoded-tui`
5.90 MiB, `mailcoded` 15.53 MiB, `mailcoded-daemon` 16.25 MiB, each beside a shared
`libe_sqlite3.so` of 1.40 MiB. The TUI is small because it is built against the wire protocol alone
and carries no IMAP, MIME or SQLite code. `mailcoded-mcp` is the odd one out: it is not AOT, needs
the .NET runtime, and at 64.11 MiB is the largest part of the install, mostly SQLite natives for
platforms you are not using. None of these is "a single binary"; distribute each with its library.

## Building without installing

    scripts/build.sh        # or: dotnet build Mailcoded.slnx -m:1
    scripts/test.sh         # the unit suite

The binaries are then under `src/<Project>/bin/Debug/net10.0/`, in four separate directories, so
`mailcoded tui` cannot find its siblings from there. Run the client directly instead and tell it where
the daemon is:

    MAILCODED_DAEMON=src/Mailcoded.Daemon/bin/Debug/net10.0/mailcoded-daemon \
      src/Mailcoded.Tui/bin/Debug/net10.0/mailcoded-tui

## Uninstalling

    scripts/install.sh --uninstall

removes the commands. It never touches your mail store, which lives somewhere else entirely
(chapter 3).
