# 11. Agents

[← Contents](README.md)

mailcoded is a mail client first; the agent surface is what falls out of having a good CLI. If you are
going to let an AI agent at your mail, read **[docs/agents.md](../agents.md)**. It is written for the
human making that decision and covers installing for an agent host, the default posture, how to turn
the two off-by-default capabilities on, what is audited and where to read it, and the risk you accept.
This chapter is the two-minute version.

## The shape

| Binary | Surface | Who talks to it |
|---|---|---|
| `mailcoded` | one-shot CLI, `--json` on stdout | shell-capable agents: Claude Code, Codex CLI, Gemini CLI, opencode, Goose |
| `mailcoded-mcp` | MCP server over stdio | hosts that cannot run a shell: Claude Desktop, Cursor |
| `mailcoded-daemon` | JSON-RPC over stdio | editor-style clients such as the TUI — **not** an agent surface |

Every safety gate lives in the core library, so the CLI and the MCP server enforce identical rules;
you cannot loosen one by choosing the other.

## The default posture

| Capability | Default for an agent | Unlock |
|---|---|---|
| search, read, thread | on | — |
| tag | on | — |
| draft | on | — |
| send | **off** | `MAILCODED_SEND=1`, `MAILCODED_APPROVED_RECIPIENTS`, a one-time token, at most 5 an hour |
| raw SQL | **off** | `MAILCODED_ENABLE_SQL=1`; read-only and row-capped even then |
| HTML bodies | never | agents get plaintext only |
| move between folders | never on MCP; **off** on the CLI | `MAILCODED_ALLOW_MOVE=1`; a terminal then confirms, and `--yes` skips the prompt |
| delete, expunge, trash | **does not exist** | there is no such verb or tool |

Set `MAILCODED_AGENT_HOST=<label>` in the agent's environment and it is recorded on every audit row.

## MCP in one block

    dotnet publish src/Mailcoded.Mcp -c Release        # or use the installed mailcoded-mcp

Then, in Claude Desktop's `claude_desktop_config.json`:

    {
      "mcpServers": {
        "mailcoded": {
          "command": "/absolute/path/to/mailcoded-mcp",
          "args": ["--db", "/absolute/path/to/store.db"],
          "env": { "MAILCODED_AGENT_HOST": "claude-desktop" }
        }
      }
    }

Absolute paths, and restart the client after editing. The server offers eight tools — `search`,
`read`, `thread`, `tag`, `draft`, `send_preview`, `send_draft`, `stats` — and no removal tool, by
construction. `--db` may be omitted in favour of `MAILCODED_DB` or the default store.
`MAILCODED_MCP_LOG` sets its stderr log level. It is a local stdio server: it does not reach claude.ai
on the web, or a phone.
