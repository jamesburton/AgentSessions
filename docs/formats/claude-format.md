# Claude Code session format (schema only)

Derived from a format spike on one real session (names and value types only; no content recorded).

## Layout

- Store root: `<home>/.claude/projects/`
- One folder per project. Folder name = project path with every non-alphanumeric
  character replaced by `-` (`C:\Dev\my app` -> `C--Dev-my-app`). Lossy (cannot
  tell `-` from `\`, `.` or space), so recover the real path from `cwd` inside the session.
- Session = `<projectFolder>/<sessionId>.jsonl` (id is a GUID, equals the file stem).
- Optional sibling folder `<projectFolder>/<sessionId>/` holds auxiliary data, e.g.
  `subagents/agent-<hex>.jsonl` and `subagents/agent-<hex>.meta.json`.
  Subagent files are NOT separate sessions; only top-level `*.jsonl` files are.

## JSONL

One JSON object per line, discriminated by `type`. Lines may be blank, and the
last line may be truncated while a session is being written.

Message-bearing types: `user`, `assistant`. Other types (ignore for transcripts):
`attachment`, `system`, `ai-title`, `last-prompt`, `mode`, `queue-operation`,
`file-history-snapshot`, `cost-state`, `api-request`, `api-request-blob`
(also carries `message` but is debug data, not conversation), `api-request-shape`,
`atis-latch`.

### Common fields on `user` / `assistant` / `system` entries

| Field | Type | Notes |
|---|---|---|
| `type` | string | `user` / `assistant` / `system` |
| `sessionId` | string | session id |
| `timestamp` | string | ISO-8601 UTC, e.g. `2026-01-01T10:00:00.000Z` |
| `cwd` | string | absolute working dir of the project (use for ProjectPath) |
| `uuid`, `parentUuid` | string | entry chain |
| `isSidechain` | bool | true for subagent entries |
| `isMeta` | bool | present on injected (non-prompt) user entries |
| `gitBranch`, `version`, `entrypoint`, `userType` | string | metadata |

Some non-message entries (`ai-title`, `last-prompt`, `mode`) carry only `sessionId` (+ payload field).

### `message`

- `user`: `message.role = "user"`, `message.content` is either
  - a **string** (a typed prompt), or
  - an **array** of blocks: `{type:"text", text}` (prompt) or
    `{type:"tool_result", tool_use_id, content (string or array of {type:"text",text}), is_error?}`.
  - Tool results also add top-level `toolUseResult` (object, shape varies per tool).
- `assistant`: `message.role = "assistant"`, `message.content` is an array of blocks:
  - `{type:"text", text}` assistant text
  - `{type:"thinking", thinking, signature}` (skip)
  - `{type:"tool_use", id, name, input:{...}}` tool call. Files touched appear in
    `input.file_path`, `input.path`, `input.notebook_path` (strings).
  - plus `message.model`, `message.usage`, `message.stop_reason`.

### Tool calls (verified 2026-10-30 against one real session; names and types only)

`tool_use.input` is an object whose keys depend on the tool, e.g. `Bash` / `PowerShell`: `command` (string),
`description` (string), `run_in_background` (bool, optional); `Edit`: `file_path`, `old_string`, `new_string`,
`replace_all`; `Write`: `file_path`, `content`; `Read`: `file_path`; `Grep`: `pattern`, `path`, `output_mode`, flags;
`Agent`: `description`, `prompt`, `subagent_type`, ...

Transcript mapping (`ClaudeAdapter.ReadTranscript`):
- `user`/`assistant` entries flagged `isMeta` or `isSidechain` are skipped (the same predicate as the title), so an
  injected note such as the local-command "Caveat: ..." never becomes the handoff's original request.
- `tool_use` -> a tool invocation entry (ToolName = `name`): text = `input.command` when present, else a one-line
  compact JSON of `input` truncated to 200 chars; files touched from `input.file_path` / `path` / `notebook_path`.
- `tool_result` -> a tool output entry (ToolName null) with the result text.

### Where things live

| Need | Source |
|---|---|
| session id | file stem (also `sessionId`) |
| project path | first entry with `cwd` |
| title | first `user` entry (not `isMeta`, not sidechain) whose content is a string or has a `text` block |
| last activity | max of `timestamp` across entries, else file mtime |
| files touched | `tool_use.input.file_path` / `path` / `notebook_path` |

## Agent index after archive: verdict (AfterRemove stays a no-op)

Method (2026-10-30): structure only on this machine: entry names with ids replaced by a `<guid>` shape, sizes, JSON
key names, and yes/no counts of whether an entry's id matches an existing session file. No contents were read or
recorded. Claude Code itself was not run.

VERIFIED, things outside the session file that are keyed by the session id and are left behind by archive/delete:

| Path | Shape | What it holds |
|---|---|---|
| `~/.claude/history.jsonl` | one JSON line per typed prompt: `display`, `pastedContents`, `project`, `sessionId`, `timestamp` | copies of the prompt text (the up-arrow history). 240 distinct `sessionId`s here, but only 27 session files exist: Claude Code's own transcript cleanup already leaves history lines for sessions that are gone, so a history line without a session is a normal state for the agent. |
| `~/.claude/file-history/<sessionId>/` | 21 folders, all matching a session id | backups of edited files (rewind/checkpoints). |
| `~/.claude/session-env/<sessionId>/` | 25 folders, all matching a session id | per-session environment data. |
| `~/.claude/todos/`, `~/.claude/debug/` | absent / empty on this machine | older versions keep per-session files named by the id (`<id>-agent-<id>.json`, `<id>.txt`): not verified here. |

Owned and moved with the session (not leftovers): `<projectFolder>/<sessionId>.jsonl` and the sibling folder
`<projectFolder>/<sessionId>/` (subagent transcripts), see `OwnedPaths`.

UNVERIFIED (the agent was not run): how `claude --resume` and its picker treat a session whose file is missing.
Expected from the layout: the picker is built from `projects/*/*.jsonl`, so an archived session simply does not appear,
and `claude --resume <id>` for a missing id reports that no conversation was found. No index file listing sessions was
found that the picker would need to agree with.

Why `AfterRemove` stays a no-op:
- Archive is restorable byte for byte to the same paths; the leftovers above are keyed by the unchanged id, so they line
  up again after `agentsessions restore`. Nothing is lost.
- `history.jsonl` is a shared, append-only file written live by every running Claude Code process; rewriting it from
  another process could lose concurrent writes. The leftovers are what the agent already tolerates.
- Consequence for `--delete`: the transcript and its sibling folder are deleted, but prompt copies in `history.jsonl`
  and file backups in `file-history/<id>/` remain (documented in the README's known limitations).

## Resume / launch

- Resume: `claude --resume <sessionId>` run with working directory = project path.
- New with prompt: `claude "<prompt>"` (positional argument) in the project directory.
