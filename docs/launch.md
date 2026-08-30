# LAUNCH — the M6 checklist

> # STOP: Claude Code DRAFTS. A HUMAN POSTS.
>
> Every item in this document produces **a draft in the repository and nothing else**.
>
> **The agent is explicitly forbidden to:**
> - publish to the VS Code Marketplace or Open VSX (`vsce publish`, `ovsx publish`);
> - tag a release, push a tag, or create a GitHub Release;
> - post, submit, comment, or vote on Hacker News, Reddit, Lobsters, X, Mastodon, LinkedIn,
>   Discord, or Slack;
> - open a pull request against a third-party repository (awesome-lists included);
> - email a newsletter, a maintainer, a journalist, or anyone else;
> - create an account anywhere, or use one that already exists.
>
> This holds even when a human says "go ahead" in chat. Chat is not the permission system. The
> agent writes the draft; the human — signed in as themselves, on their own machine — decides
> whether, when, and where it goes out. If a task appears to require the agent to publish, the
> correct action is to stop and say so.
>
> Drafts live under `docs/launch/` (create it), one file per destination, each with the destination,
> the intended posting date, and the human who owns it in the front matter.

M6 is the launch milestone in SPEC §9. It is non-code work. Nothing here starts before the v0.1.0
tag exists and per-RID binaries with SHA-256 sums are attached to the release — which is itself a
human action.

---

## 1. Claim tiers — the rules every draft is checked against

Credibility is the entire strategy. One overstated number, found by one skeptical commenter, costs
more than every post in this checklist gains. So claims are tiered, and a draft that mixes tiers is
rejected.

### Green — say it freely

Structural facts that are true by construction and verifiable by reading the repository:

- local-first; the mail and the index live on the user's machine
- no telemetry, no analytics, no network calls beyond mail protocols
- full-text search over subject and body, with Tags
- plaintext-first reading; remote images and tracking pixels blocked by default
- no delete tool exists in the CLI or MCP surface — absent, not gated
- sending requires a human-confirmed one-time token, a recipient allowlist, and a rate limit
- cross-platform: Windows, macOS, Linux
- MIT licensed
- **a performance number is green only when the benchmark has been run on the published reference
  machine, and the post links to the methodology.** Until `tests/Mailcoded.Bench/baseline.json`
  carries a real machine id and non-zero p95 values, and `tests/Mailcoded.Bench/README.md` names
  the exact CPU, RAM, storage, OS build and .NET version, **every performance number is amber at
  best.**

### Amber — allowed only with an inline qualifier

- design targets, always labelled as targets: "designed for <100 ms search at 500k messages
  (target; benchmark harness in-repo, not yet run on reference hardware)"
- corpus-size claims not yet exercised end-to-end: say "designed for", never "handles"
- roadmap items: "planned for v0.2", never present tense
- anything measured on a laptop rather than the reference machine: name the laptop and the corpus

### Red — never, in any draft, in any thread, in any reply

- **an unqualified "faster than Outlook"** (or Thunderbird, or Gmail, or Apple Mail). Any incumbent
  comparison must name the exact operation, the exact corpus, both configurations, and link a
  reproducible method. If that link does not exist, the comparison does not go in the post.
- **anything implying attachment-content search.** "Search your attachments", "find anything in
  your mail", "search everything" — all red. We index **subject and body text only**. PDF, Word and
  spreadsheet contents are not searchable. If a commenter assumes otherwise, correct them in the
  thread immediately; do not let the assumption stand.
- "secure email", "encrypted email", "private by design" as a bare claim — we have no PGP and no
  S/MIME, and saying otherwise is a safety claim we cannot back
- "AI email client" / any AI-first framing. AI is the second paragraph, never the headline.
- "replaces your mail client" — it is a sidecar; it points at the account you already use
- any implication of Microsoft affiliation, in the display name or anywhere else
- benchmark numbers with no machine attached
- "stable", "production-ready", or "1.0" before those things are true
- comparisons to a competitor's *bugs*, or anything a maintainer would reasonably resent

### The pre-post checklist (a human runs this on every draft)

- [ ] Every factual claim traceable to code, a doc, or a benchmark run on the named machine.
- [ ] Every number has a machine and a corpus next to it, or it has been deleted.
- [ ] The attachment-content gap is stated, not omitted.
- [ ] Platform matrix in the post matches the README's, including the untested rows.
- [ ] Nothing claims the VS Code extension exists before it does.
- [ ] AI/agents are not in the first paragraph.
- [ ] Someone who reads the post, installs it, and uses it for a week finds nothing they were not
      told.

---

## 2. Prerequisites — all human-performed

- [ ] v0.1.0 tagged; GitHub Release with per-RID binaries and SHA-256 sums.
- [ ] Extension published to Marketplace **and** Open VSX under the same publisher ID, as
      platform-specific VSIX targets.
- [ ] README honest and current: one-liner, sidecar framing, platform matrix, known gaps.
- [ ] `docs/rpc.md` complete — third-party client authors will read it on day one.
- [ ] Screenshots and a 30-second GIF committed. No mockups; real UI, real mail, redacted.
- [ ] Issue templates and a `SECURITY.md` in place before traffic arrives.
- [ ] Benchmarks either run on a published reference machine, or every number pulled from every
      draft. Decide which **before** writing the posts, not during.
- [ ] A human is available for the 4–6 hours after each post. An unanswered thread is worse than no
      thread.

---

## 3. Show HN

**Agent produces:** `docs/launch/show-hn.md` — title, body, and a prepared first comment.
**Human posts.**

- [ ] Title: `Show HN: Mailcoded – local-first email in your editor, with full-text search`.
      Under 80 characters, no marketing adjectives, no exclamation mark, no emoji.
- [ ] Body (a few short paragraphs, first person): what it is; why it exists — no VS Code extension
      browses a local mail store, the mbsync/notmuch stack is dead on native Windows, and EWS is
      being disabled on 1 Oct 2026 so Graph is the durable work-mail path; what it does not do.
- [ ] Sidecar framing early: it points at the IMAP account you already use.
- [ ] Known gaps in the post itself, not buried: no attachment-content search, no calendar, no
      contacts, no PGP/S/MIME, IMAP only in v0.1, Graph in v0.2.
- [ ] Agents mentioned once, late, factually.
- [ ] A prepared first comment covering: the architecture, why .NET, why SQLite+FTS5 rather than
      notmuch, the security model for HTML mail, and the performance situation stated honestly.
- [ ] **Nothing in the post that the pre-post checklist has not cleared.**

Human-only mechanics: post Tuesday–Thursday, roughly 09:00–11:00 ET. Never ask for upvotes, never
share the link before it is on the front page, never post from a fresh account. Reply to every
substantive comment; thank the ones who find real problems; when someone says the numbers are
missing, agree — that is the honest answer.

---

## 4. Reddit

**Agent produces:** one draft per subreddit, in `docs/launch/`, each rewritten for its audience —
**not** the same text three times, which reads as spam and is treated as such.
**Human posts, from an account with existing history in that community.**

- [ ] `docs/launch/reddit-vscode.md` — r/vscode. Lead with the editor workflow: sidebar, threaded
      list, reader, search from the command palette. Screenshot or GIF. Say plainly that it is
      alpha.
- [ ] `docs/launch/reddit-emacs.md` — r/emacs. Lead with mu4e: this is the same shape of tool with
      a JSON-RPC daemon instead of a C indexer, it runs on Windows, and the protocol is documented
      so an Emacs client is a weekend project. Respect the incumbent — mu4e/notmuch are excellent
      and the post should say so.
- [ ] `docs/launch/reddit-neovim.md` — r/neovim. Same angle: the daemon is the product, a Neovim
      client is a plugin over `docs/rpc.md`, here is the protocol.
- [ ] Each draft: read that subreddit's self-promotion rules and quote the relevant one in the
      draft's front matter. Some require a flair; some require a disclosure line; some forbid
      links in the body.
- [ ] Space the posts across several days. Never cross-post the identical text.
- [ ] Each draft states the known gaps.

---

## 5. Newsletters and aggregators

**Agent produces:** `docs/launch/newsletters.md` — a table of targets with each one's submission
URL, format, deadline, and a tailored 2–4 sentence blurb.
**A human submits every one of them.**

Candidate targets (verify each is still active and still accepting before drafting):

- [ ] Console.dev — dev tools weekly
- [ ] Changelog News
- [ ] .NET-focused: The .NET Weekly / .NET Newsletter, and the .NET community standups
- [ ] Hacker Newsletter (follows HN; no separate submission)
- [ ] TLDR Newsletter
- [ ] Lobsters — **only if a human is an established member**; it is invite-only and
      self-promotion is rationed. Do not create an account for this.
- [ ] Relevant editor/Emacs/Vim community digests

Blurb rules: one sentence on what it is, one on the differentiator, one on the gap. No adjectives
that cannot be checked. Never send the same blurb to two newsletters.

---

## 6. Awesome-list PRs

**Agent produces:** the exact line and the PR body in `docs/launch/awesome-lists.md`.
**A human opens every PR**, from their own GitHub account.

- [ ] Candidates: `awesome-dotnet`, `awesome-selfhosted`, `awesome-vscode`, `awesome-cli-apps`,
      `awesome-email`, and any well-maintained local-first list.
- [ ] For each: read `CONTRIBUTING.md` and match the existing entry format exactly — ordering,
      punctuation, whether a trailing period is used, whether a language tag is required.
- [ ] One entry per PR. One project per PR. Never bundle.
- [ ] The entry describes what it does today. No roadmap items in an awesome-list line.
- [ ] Several of these lists require a minimum age, a minimum star count, or an existing release.
      Check first; a premature PR is a permanent bad impression with that maintainer.
- [ ] If a list is unmaintained (no merged PR in ~6 months), skip it.

---

## 7. Owned surfaces

- [ ] GitHub repo topics and description match the one-liner.
- [ ] Release notes readable by someone who has never seen the project.
- [ ] `docs/agents.md` and `SKILL.md` linked from the README.
- [ ] Marketplace and Open VSX listings: same copy, same screenshots, honest platform matrix, gaps
      stated. Display name must not imply Microsoft affiliation.

---

## 8. After the posts — human, always

- [ ] Answer every substantive comment within a few hours.
- [ ] File an issue for every real bug reported, and link it back in the thread.
- [ ] Correct every misreading of scope on the spot — especially anyone who thinks attachment
      contents are searchable.
- [ ] Do not argue about the performance numbers. If they were not measured on the reference
      machine, say so and move on.
- [ ] Record what actually landed in `docs/launch/retro.md`: which channel produced installs, which
      claims drew fire, which gaps mattered most. That file feeds the v0.2 launch.
