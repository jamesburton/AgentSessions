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
| `response_item` | `custom_tool_call_output` | `call_id`, `output` (string) |

### Where things live

| Need | Source |
|---|---|
| session id | file stem suffix (36-char UUID); also `session_meta.payload.id` |
| project path | `session_meta.payload.cwd` (fallback: first `turn_context.payload.cwd`) |
| title | first `event_msg/user_message` `payload.message` |
| last activity | max `timestamp` across lines (tail of file), else file mtime |
| user / assistant text | `event_msg/user_message`, `event_msg/agent_message` |
| tool calls | `function_call` (`name`), `custom_tool_call` (`name`) |
| files touched | `apply_patch` `input` lines `*** Add File: <p>`, `*** Update File: <p>`, `*** Delete File: <p>`, `*** Move to: <p>`. Shell commands expose no structured file list. |

## Resume / launch

- Resume: `codex resume <id>` run with working directory = project path.
- New with prompt: `codex "<prompt>"` (positional argument) in the project directory.
