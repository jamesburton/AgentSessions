# GitHub Copilot CLI session format (schema only)

Derived from a format spike on one real session folder plus the shared store (names and value types only;
no content recorded).

## Layout

- `<home>/.copilot/session-state/<id>/` per session; id = folder name (also `workspace.yaml` `id`,
  `session.start.data.sessionId`).
- Folder contents (not all always present): `events.jsonl` (can be 20+ MB), `workspace.yaml`,
  `checkpoints/`, `files/`, `research/`, `session.db` (per-session SQLite: `todos`, `todo_deps`,
  `inbox_entries`), `rewind-snapshots/`, `plan.md`, `inuse.<pid>.lock`, `.workspace-fork.lock`.
- Loose `<id>.lock` files sit next to the folders in `session-state/`; only directories are sessions.
  A folder with neither `workspace.yaml` nor `events.jsonl` is reported as skipped.
- `<home>/.copilot/session-store.db` (+ `-wal`, `-shm`): shared SQLite store, see the safety verdict.

> Single-sample caveat: the layout notes below come from one machine and one session folder (plus the shared store). In particular the folder holds `rewind-snapshots/` here whereas the original survey named `rewind-file-snapshots/`, and checkpoint file shapes were seen on only one machine; treat both as single-sample observations.

## workspace.yaml

Flat `key: value` lines. Keys: `id`, `cwd`, `git_root`, `branch`, `name`, `user_named`, `summary_count`,
`created_at`, `updated_at` (ISO-8601 UTC). Paths are plain (unquoted) strings. Parsed with a trivial
line reader (no YAML dependency).

## events.jsonl

One JSON object per line: `{ "type": <kind>, "data": { ... }, "id": <string>, "timestamp": <ISO-8601 UTC>, "parentId": <string|null> }`.
Blank lines and a truncated final line are possible while a session is being written.

| `type` | `data` keys |
|---|---|
| `session.start` | `sessionId`, `version`, `producer`, `copilotVersion`, `startTime`, `context{cwd,gitRoot,branch,headCommit}`, `alreadyInUse`, `remoteSteerable` |
| `session.resume` | `resumeTime`, `eventCount`, `selectedModel`, `context{...}`, ... |
| `session.model_change` / `session.info` | `newModel` / `infoType`, `message` |
| `session.shutdown` | usage/metrics summary (`codeChanges`, `modelMetrics`, `shutdownType`, ...) |
| `system.message` | `role`, `content` (skip) |
| `user.message` | `content` (string; the typed prompt), `transformedContent`, `attachments[]`, `interactionId` |
| `assistant.turn_start` / `assistant.turn_end` | `turnId`, `interactionId` (skip) |
| `assistant.message` | `messageId`, `content`, `toolRequests[{toolCallId,name,arguments,type}]`, `phase`, `outputTokens`, `reasoningOpaque`, `encryptedContent` |
| `tool.execution_start` | `toolCallId`, `toolName`, `arguments{...}` (varies per tool; a `path` string is used for files touched when present) |
| `tool.execution_complete` | `toolCallId`, `success`, `result{content,detailedContent}`, `toolTelemetry` |
| `hook.start` / `hook.end`, `permission.requested` / `permission.completed` | bookkeeping (skip) |

## checkpoints/

- `index.md`: `# <heading>`, a sentence, then a table `| # | Title | File |`.
- `NNN-<slug>.md`: agent-written summaries (zero-padded sequence). No markdown heading in the observed
  files; content is XML-ish sections `<overview>`, `<history>`, `<work_done>`, `<technical_details>`,
  `<important_files>`, `<next_steps>`.

## Where things live

| Need | Source |
|---|---|
| session id | folder name |
| project path | `workspace.yaml` `cwd` (fallback `git_root`, then `session.start.data.context.cwd` in the head scan) |
| title | newest `checkpoints/NNN-*.md` (highest name): first `# ` heading that appears before the first `<section>` tag, else first line of `<overview>` (a heading inside `<history>` etc. is content, not a title); else first `user.message` `data.content`; else `(no prompt)` |
| last activity | `JsonlReader.TailNewest` over `events.jsonl` (max `timestamp` in the last 64 KB; returns the events file mtime when the final line exceeds the window). Only if `events.jsonl` is absent (or yields nothing, e.g. empty) does it fall back to `workspace.yaml` `updated_at`, then the folder mtime |
| user / assistant text | `user.message`, `assistant.message` |
| tool calls | `tool.execution_start.toolName` (files from `arguments.path`), output from `tool.execution_complete.result.content` |

`SizeBytes` is an approximation, not a recursive folder size: `events.jsonl` + `workspace.yaml` + the top-level `checkpoints/*.md` lengths (a missing or locked file counts as 0). `rewind-snapshots/`, `files/`, `research/` can hold thousands of files and are never enumerated.

All text files (workspace.yaml, checkpoints) are read with `FileShare.ReadWrite | Delete` so a live CLI holding them open for write never breaks Discover.

Discover limits: head scan stops after `JsonlReader.HeadScanBytes`; tail scan reads the last 64 KB, with
file-mtime fallback when the final line exceeds the window. `index.md` is not used for titles.

## Resume / launch

- Resume: `copilot --resume <id>` in the project directory.
- New with prompt: `copilot -i <prompt>` (interactive with initial prompt) in the project directory.

## Cleanup safety spike: verdict UNSAFE (Archive and Delete declined)

Method: copied `session-store.db` (+ `-wal`, `-shm`) to a scratch directory and inspected the copy
read-only (schema, row counts, id matching as yes/no). Copy deleted afterwards; the real store was never
opened. The WAL was about 4 MB (larger than the 0.7 MB main file), i.e. the CLI writes it live.

Per-session rows exist, keyed by a `session_id` that equals the session folder name (yes for each table):

| Table | Rows | Notes |
|---|---|---|
| `sessions` | 4 | `id, cwd, repository, host_type, branch, summary, created_at, updated_at` |
| `turns` | 74 | `session_id, turn_index, user_message, assistant_response, timestamp` (copies of message content) |
| `checkpoints` | 5 | `session_id, checkpoint_number, title, overview, history, work_done, ...` |
| `session_files` | 72 | `session_id, file_path, tool_name, turn_index` |
| `session_refs`, `forge_trajectory_events` | 0 | `session_id` keyed |
| `assistant_usage_events` | 323 | `session_id` keyed usage rows |
| `search_index` (+ `_data`, `_idx`, `_content`, `_docsize`, `_config`) | 104 | FTS5 virtual table with `session_id`, `source_type`, `source_id` |
| `schema_version` | 1 | `version` |

Only 4 of 9 session folders had rows, so the store is a lazily built index, not a 1:1 registry.

Reasoning for unsafe:
- Moving or deleting a folder leaves rows pointing at a missing folder; for Delete the rows would also keep
  copies of the user's message content (`turns`, `checkpoints`, FTS), defeating the delete.
- Keeping it consistent means row deletes across about 7 tables including an FTS5 virtual table with its
  shadow tables, in a schema that is versioned and undocumented, so any CLI update can break the logic.
- The database is live (WAL and `-shm` present, session lock files); writing to it from a second process
  risks lock contention or corruption, and doing it correctly would need a SQLite dependency in the product.
- Archive (a move) would additionally strand index rows whose folder no longer resolves for `--resume`.

Consequence: `CopilotAdapter.Capabilities` omits `Archive` and `Delete` (List, Resume, LaunchWithPrompt,
Transcript only). `OwnedPaths` is the session folder alone and nothing in `session-store.db` is touched.
Revisit if the CLI documents the store or offers its own session-delete command.
