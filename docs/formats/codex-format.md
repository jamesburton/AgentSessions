# Codex CLI session format (schema only)

Derived from a format spike on two real sessions (names and value types only; no content recorded).

## Layout

- Active: `<home>/.codex/sessions/YYYY/MM/DD/rollout-<yyyy-MM-ddTHH-mm-ss>-<uuid>.jsonl`
- Archived: `<home>/.codex/archived_sessions/rollout-<...>-<uuid>.jsonl` (flat; same file name scheme).
  A session is archived when its `StorePath` is under `archived_sessions`.
- Session id = the UUID at the end of the file stem (also `session_meta.payload.id`).
- Other things in `~/.codex` (`history.jsonl`, `session_index.jsonl`, `*.sqlite`) are indexes/state, not sessions.

## JSONL

One JSON object per line: `{ "timestamp": <ISO-8601 UTC string>, "type": <kind>, "payload": { ... } }`.
Blank lines and a truncated final line are possible while a session is being written.

### `type` kinds and `payload.type` sub-kinds

| `type` | `payload.type` | Notes |
|---|---|---|
| `session_meta` | (none) | first line. `payload`: `id`, `timestamp`, `cwd`, `originator`, `cli_version`, `source`, `thread_source`, `model_provider`, `base_instructions{text}`, `git{commit_hash,branch,repository_url}`. The line can be large (instructions text). |
| `turn_context` | (none) | per turn: `turn_id`, `cwd`, `model`, `approval_policy`, `sandbox_policy{...}`, `user_instructions`, ... (skip) |
| `event_msg` | `task_started` / `task_complete` | turn bookkeeping (skip) |
| `event_msg` | `user_message` | the real typed prompt: `payload.message` (string), plus `images[]`, `local_images[]`, `text_elements[]` |
| `event_msg` | `agent_message` | assistant text: `payload.message` (string), `phase` |
| `event_msg` | `token_count` | usage stats (skip) |
| `response_item` | `message` | `payload.role` = `developer` / `user` / `assistant`; `payload.content[]` of `{type, text}`. The `user` messages here include injected preambles (instructions, `<environment_context>`), so they are NOT used for titles; assistant text duplicates `agent_message`. |
| `response_item` | `reasoning` | `summary[]`, `encrypted_content` (skip) |
| `response_item` | `function_call` | `name` (e.g. `shell_command`, `update_plan`), `arguments` (JSON **string**), `call_id`. `shell_command` args: `command`, `workdir`, `timeout_ms` |
| `response_item` | `function_call_output` | `call_id`, `output` (string) |
| `response_item` | `custom_tool_call` | `name` (e.g. `apply_patch`), `input` (string), `call_id`, `status` |
| `response_item` | `custom_tool_call_output` | `call_id`, `output` (string, or in newer CLIs an **array** of `{type, text}`) |
| `event_msg` | `item_completed` | newer CLIs: `item{type: UserMessage / AgentMessage / CommandExecution / FileChange / Reasoning / ...}`, `thread_id`, `turn_id`, timings (not used) |

### Newer CLI shapes (verified 2026-10-30 across ~200 local sessions; names, types and counts only)

- **Most sessions no longer write `event_msg/user_message` or `agent_message`** (18 of 183 active sessions had them).
  The conversation is in `response_item` `message` items (`role` user / assistant / developer; content blocks
  `input_text` / `output_text` with `text`). Where both sources exist, every `user_message` and `agent_message` was
  found verbatim among the `response_item` messages (143/143 and 1015/1015).
- Injected user blocks: `# AGENTS.md instructions for <dir>` and XML-ish blocks whose first tag is
  `environment_context`, `recommended_plugins`, `skill`, `subagent_notification`, `turn_aborted`, `user_instructions`
  (seen counts 234, 105, 14, 26, 8; `user_shell_command` and other tags are kept as user text).
- The dominant tool call is `custom_tool_call` named **`exec`**: `input` is a multi-line **JavaScript code cell**
  (not JSON) that calls tools, e.g. `const r = await tools.exec_command({cmd: "<command>", yield_time_ms: 1000});`
  (a `cmd:` string literal, usually double-quoted) or `await tools.apply_patch("*** Begin Patch\n...")`, where the
  patch is a string literal with **escaped** `\n` separators (1480 of 1496 embedded patches).
- `function_call` names seen: `shell_command` (`command` string, `workdir`, `timeout_ms`), `shell` (`command` **array**
  of strings, `justification`, `sandbox_permissions`), `wait`, `wait_agent`, `send_message`, `spawn_agent`,
  `update_plan`, `request_user_input`, `list_agents`, `close_agent`, ...

Transcript mapping (`CodexAdapter.ReadTranscript`):
- user/assistant: `event_msg` user_message/agent_message AND `response_item` messages (user/assistant roles; injected
  user blocks dropped). A message present in both sources is emitted once (pairwise de-duplication, bounded).
- `function_call` -> invocation (ToolName = `name`): text = `arguments.command` (string, or array joined with spaces),
  else compact JSON of the arguments (200 chars), else the raw arguments string.
- `custom_tool_call` `apply_patch` -> invocation with text `apply_patch: <files>` and the patched files.
- `custom_tool_call` `exec` -> one `exec_command` invocation per `cmd:` literal (unescaped), plus an `apply_patch`
  invocation (with files) for an embedded patch; a cell with neither -> `exec` with the cell's first line.
- `*_output` -> output entry (ToolName null): the string, or the array items' `text` joined with newlines.
- Title: the first `event_msg/user_message`, else the first real (non-injected, not `<`-prefixed) `response_item`
  user message in the head scan.

### Where things live

| Need | Source |
|---|---|
| session id | file stem suffix (36-char UUID); also `session_meta.payload.id` |
| project path | `session_meta.payload.cwd` (fallback: first `turn_context.payload.cwd`) |
| title | first `event_msg/user_message` `payload.message`, else first real `response_item` user message |
| last activity | max `timestamp` across lines (tail of file), else file mtime |
| user / assistant text | `response_item/message` (user, assistant), plus `event_msg/user_message`, `event_msg/agent_message` (de-duplicated) |
| tool calls | `function_call` (`name`, command from `arguments`), `custom_tool_call` (`name`; `exec` cells parsed for `cmd:`) |
| files touched | `apply_patch` `input` lines (or the escaped patch inside an `exec` cell) `*** Add File: <p>`, `*** Update File: <p>`, `*** Delete File: <p>`, `*** Move to: <p>`. Shell commands expose no structured file list. |

### Legacy flat format

Older Codex versions may write entries without the `payload` wrapper. These are not supported:
such sessions list with null cwd and the `(no prompt)` title placeholder.

### Discover scan limits

The head scan stops after `JsonlReader.HeadScanBytes` (256 KB) even if cwd/title are not found
(then ProjectPath is null and title is `(no prompt)`). The tail scan reads the last 64 KB; if that
window holds no complete line (one huge final line) LastActivity falls back to the file mtime.

## Resume / launch

- Resume: `codex resume <id>` run with working directory = project path.
- New with prompt: `codex "<prompt>"` (positional argument) in the project directory.
