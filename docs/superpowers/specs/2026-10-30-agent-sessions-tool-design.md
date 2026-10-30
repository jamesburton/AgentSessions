# AgentSessions: cross-agent session manager (design)

Date: 2026-10-30. Status: draft for approval.

## Intent and why

The user runs several coding agents (Claude, Codex, Copilot, Antigravity `agy`, and
occasionally OpenCode and Hermes) and hits usage/token limits several times almost every
day, usually moving Claude -> Codex or Copilot (other paths occur; other users will have
different primary/secondary/tertiary agents). Each agent keeps its own session store, so
there is no single view of "what have I worked on recently", old sessions pile up, and
moving work to another agent means rebuilding context by hand.

Today the user mitigates this with a `.docs/handoff.md` (written by their `handoff` skill)
and superpowers plans. That fails exactly when it is needed most: if a session is blocked
or terminated early, the handoff never gets written. The stored session on disk, however,
still exists. This tool reads it directly.

**AgentSessions** is a .NET 10 C# global/tool package, published to NuGet and runnable via
`dnx`, that lists, resumes, cleans up and transfers sessions across agents.

## Goals (v1)

1. **List** sessions from all supported agents in one merged view, newest first. Each row:
   agent, project folder, last activity, size, short title (first prompt or the agent's own
   summary), and a `[stale]` tag.
2. **Resume** a selected session by launching that agent's own native resume in the right
   working directory.
3. **Clean up**: sessions with no activity for more than 30 days are "stale" (threshold
   configurable by setting and flag). The user can also pick sessions manually. Actions are
   **archive** (restorable) and **delete**, each offered per agent only where it can be done
   safely. Preview first; dry-run is the default; confirm before acting.
4. **Transfer** a session to another agent (e.g. Codex -> Claude) via a *handoff* generated
   from the stored session (see below), then launch the target agent to continue.
5. Both a **TUI/menu** (the primary, most common mode; run with no arguments) and
   **scriptable commands** (`list`, `clean`, `resume`, `transfer`, ...) over the same core.

### v1 agent coverage

Claude, Codex, Copilot must work end to end (list, resume, clean up, transfer as source and
target). Antigravity, OpenCode and Hermes follow after. The design must make adding an agent
cheap: each agent is an adapter that can discover sessions, read them, launch/resume them,
and archive/delete them (declaring which of those it supports).

## Non-goals

- Full native-format session conversion (writing a Codex session into Claude's store, etc.)
  is **not** in v1. It is a later step, only if the handoff approach proves insufficient.
- Not a chat UI, not a transcript viewer beyond what listing/preview needs.
- No cloud sync, no account/credential handling, no telemetry.
- Does not replace or modify the user's `handoff` skill; it covers the case where that never
  ran.

## Anti-goals (what makes it a failure even if it works)

**Losing information compared with the normal way of resuming.** Concretely:

- Same-agent resume must use the agent's own native resume, never a re-creation.
- Cleanup must never silently lose anything: archive is the safe default, delete is an
  explicit choice, dry-run is the default.
- A transfer must never overwrite anything. The handoff always references the full original
  session (agent, session id, path) so nothing is unreachable.

## Transfer (handoff) behaviour

- The handoff is built by **direct extraction** from the stored session (default): original
  request, recent messages, files touched, last commands, where it stopped, plus a pointer to
  the full source session. Works offline, costs no tokens, works after a crash.
- An **optional** "polish with a model" step may rewrite it more readably. It is never
  required and never the default (the user may have just run out of tokens).
- Polish is **opt-in and configurable** (decided by the user): when enabled, the tool tries
  to submit the extracted source to a model for polishing; if that is disabled or fails
  (no tokens, offline, error), it falls back to the unpolished extracted handoff and says so.
  Sending content to a model only ever happens when the user has enabled it. Which model or
  agent performs the polish is configurable (builder's call on the mechanism).
- Delivery: the handoff is written to the project as `.docs/handoff.md`. If one already
  exists it is first **backed up** and **linked** (new handoff records which session it came
  from and which file it replaced), so nothing is lost. Then the target agent is launched in
  the project folder with an opening prompt to read the handoff and continue, offered as an
  automatic action with a prompt/confirmation.

## Interface

Main TUI screen: **flat list, newest first** (chosen by the user over a project-grouped
layout):

```
agentsessions   filter: all agents · all projects · any age

  AGENT    PROJECT        LAST ACTIVE  SIZE   TITLE
> claude   AgentSessions  2m ago       0.5M   Brainstorm session-manager tool
  codex    codesharp      1h ago       3.1M   Fix JSON patch edge cases
  copilot  leann-net      2d ago       22M    Reviewing release slice
  agy      jsonpatchsharp 4mo ago      0.2M   Add CLI flags            [stale]

[Enter] resume  [t] transfer to...  [a] archive  [d] delete
[/] filter  [s] select stale  [space] mark  [q] quit
```

Transfer opens a small picker (target agent, then handoff options). Filtering by agent,
project and age.

## Constraints

- .NET 10, C#; packaged as a .NET tool, published to NuGet, runnable with `dnx`.
- Windows-first (this is a Windows 11 dev box); should not preclude macOS/Linux, as session
  stores are under the home directory on all of them.
- Read-only discovery; must be safe to run while agents are active (SQLite stores must be
  opened read-only / without locking out the agent).
- Must not transmit conversation content off the machine, except the opt-in polish step
  above, which the user must have explicitly enabled.
- Copilot cleanup (decided by the user): attempt it, test it, and if testing shows it is
  dangerous (e.g. database state cannot be kept consistent), ship Copilot as list, resume and
  transfer only, with cleanup declined clearly. This is a valid v1 outcome.

## Findings: where sessions live (this machine, surveyed 2026-10-30)

| Agent | Location | Format | Notes |
|---|---|---|---|
| Claude | `~/.claude/projects/<project>/<id>.jsonl` (+ `subagents/`) | JSONL per session | Easy. Project dir name is the path with separators encoded. |
| Codex | `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`, `~/.codex/archived_sessions/` | JSONL per session | Easy. Already has an archive folder. |
| Copilot | `~/.copilot/session-state/<id>/` (`events.jsonl`, `workspace.yaml`, `checkpoints/`), `~/.copilot/session-store.db` | JSONL, YAML, MD, SQLite | `events.jsonl` can be very large (22 MB seen). Checkpoints are agent-written summaries useful for handoff. |
| Antigravity (`agy`) | `~/.gemini/antigravity-cli/` (`conversations/<id>.db`, `brain/<id>/.system_generated/logs/transcript*.jsonl`) | SQLite + JSONL | Binary at `%LOCALAPPDATA%\agy`. Presumed to be Google Antigravity (user to confirm). |
| OpenCode | `~/.local/share/opencode/opencode.db`, `storage/` | SQLite | |
| Hermes | no session store found | unknown | Needs investigation (`%APPDATA%\Hermes`, `%LOCALAPPDATA%\hermes`). |

These formats are undocumented and may change between agent versions. Adapters must fail
soft (skip and report an unreadable session, never crash the list) and be isolated per agent.

## Decisions by the builder (not yet confirmed by the user)

- TUI library (Spectre.Console is the obvious default) and CLI parsing library.
- Archive location and layout (suggest a folder under the tool's own data dir, preserving
  the original relative path so restore is exact).
- Title derivation, "last active" source (file mtime vs last event timestamp), and how
  project folders are recovered from each agent's store.
- How each agent's native resume command is invoked (`claude --resume`, `codex resume`,
  etc.), verified against installed versions.
- Backup naming for `handoff.md` (suggest timestamped sibling file).

## Calls made after builder review (the author's calls, not confirmed by the user)

- **Unrecoverable project folder:** if an adapter cannot recover a session's working
  directory, the tool asks the user for it; if they decline, resume/transfer is refused for
  that session (listing and archive still work).
- **Agent index after archive:** archiving moves the session out of the agent's own store
  and, where the agent keeps an index/database of sessions, updates it so the session no
  longer appears in the agent's own picker. Where that is not safe, archive is declined for
  that agent. A short spike per agent decides this before the adapter is built.
- **Launching agents:** the tool starts the target agent as a child process and returns to
  the list when it exits. If an agent's CLI cannot take an opening prompt, the tool falls
  back to copying the prompt to the clipboard and printing it, and says so.
- **Handoff size:** extraction is bounded (a capped number of recent messages and commands,
  long outputs truncated) with the full source always referenced by path. Exact limits are
  the builder's to tune.
- **Active sessions (user's choice):** a session modified within a short recent window, or
  with an open lock, shows a clear warning but archive/delete is still allowed after
  confirmation.

All the calls above were reviewed with the user on 2026-10-30 and confirmed, except the TUI
library, which the user left to the builder, and the handoff-size cap, whose exact limits
remain the builder's to tune. Archive home: the tool's own data folder, preserving each
session's original relative path. `handoff.md` backup: a timestamped sibling file.

## Amendment 2026-10-30 (user request during the build): session labels

The user asked, "if it is easy", for an option to rename sessions so they can be recognised
before opening, deferrable if non-trivial. Decision (author's call, confirmed with the user in
chat): implement **tool-owned labels** (`labels.json` in the tool's data directory, keyed by
`agent:id`), shown in place of the title in list/TUI/CLI, set via `rename` (CLI) and `n` (TUI).
Labels never modify an agent's own session store (anti-goal: losing information). Renaming
inside each agent's own store is explicitly out of scope. See plan Task 16.

## Open items

- Confirm `agy` = Google Antigravity.
- Hermes session location unknown.
- Whether delete/archive is viable for database-backed stores (Copilot, OpenCode,
  Antigravity); where it is not, the tool declines the action rather than risk damage.
