# AgentSessions

One place to see, resume, tidy up and hand over your coding-agent sessions.

Claude Code, Codex CLI and GitHub Copilot CLI each keep their own session history in their own folder, in their
own format. AgentSessions reads all of them and gives you:

- a single list of every session (agent, project, last activity, size, title), with stale ones flagged;
- resume in the right agent, in the right folder;
- cleanup of old sessions that is safe by default (a dry run, then an archive you can restore, never a silent delete);
- transfer of a session to a different agent, as a plain Markdown handoff written into the project;
- your own session labels, kept in a file this tool owns.

It is a .NET 10 tool. It is Windows-first (developed and tested on Windows 11) and runs entirely locally.

## Install and run

Run it without installing (requires the .NET 10 SDK):

```
dnx AgentSessions
```

Or install it as a global tool:

```
dotnet tool install -g AgentSessions
agentsessions
```

`agentsessions --version` prints the version. (Through `dnx`, put `--` before tool arguments that dnx also
understands, e.g. `dnx AgentSessions -- --version`, since `dnx` has its own `--version` option.)

With no arguments in an interactive terminal it opens the TUI. Without a terminal (piped, redirected) it prints the
command help instead. Every operation is also a plain command, listed below.

## The TUI

```
agentsessions   filter: all agents · all projects · any age
   AGENT   PROJECT   LAST ACTIVE   SIZE    TITLE
>  claude  myapp     2d ago        1.2 MB  Fix the parser bug
*  codex   other     90d ago       340 KB  Old experiment [stale]
3 session(s), 1 marked, 1 stale
[Enter] resume  [t] transfer to…  [n] label  [a] archive  [d] delete
[/] filter  [s] select stale  [space] mark  [r] reload  [q] quit
```

`>` is the current row, `*` a marked row. Sessions are newest first.

| Key | Action |
| --- | --- |
| Up / Down | Move the cursor |
| PageUp / PageDown | Move a page |
| Home / End | First / last session |
| Space | Mark or unmark the current session |
| `s` | Mark every stale session in the current view |
| `/` | Set the filter (see below); empty input clears it |
| Enter | Resume the current session in its agent |
| `t` | Transfer the current session to another agent (you pick the target) |
| `n` | Set a label for the current session (empty clears it; Esc cancels) |
| `a` | Archive the marked sessions (or the current one if none are marked) |
| `d` | Permanently delete the marked sessions (or the current one) |
| `r` | Reload from disk |
| `q` / Esc | Quit |

`a` and `d` first print the plan (paths, active-session warnings, declined items) and then ask for a single `y`
keypress; anything else cancels. Keys typed ahead before the plan appears are discarded, so they cannot confirm.

Filter syntax: space-separated tokens.

- `agent:claude` - one agent (claude, codex, copilot)
- `project:myapp` - project path contains the text (a bare word does the same)
- `older:30` - inactive for more than 30 days (0 to 36500)

Example: `agent:codex project:myapp older:14`.

## Commands

Exit codes everywhere: `0` success, `1` error (or nothing could be done), `2` usage error (bad or missing arguments).

### `list`

```
agentsessions list [--agent claude|codex|copilot] [--project TEXT] [--older-than DAYS] [--stale]
```

Prints a table and a count. Sessions that could not be read are not hidden: a final line says how many were skipped.

### `clean`

```
agentsessions clean [--older-than DAYS] [--ids agent:id,agent:id] [--archive | --delete] [--apply] [--yes]
```

Selects stale sessions (older than `staleDays`, or `--older-than`), or the exact `--ids`. `--ids` and
`--older-than` cannot be combined. Default is to print the plan and change nothing.

- `--archive` (the default action) moves the sessions to the archive.
- `--delete` removes them permanently. It is never the default and does nothing without `--apply`.
- `--apply` actually performs the plan. Unless `--yes` is given you are asked to confirm (default No). With no
  interactive terminal and no `--yes`, it refuses and exits 1.
- Exit 1 if any item failed or if everything selected was declined.

### `resume`

```
agentsessions resume <agent> <id>
```

Launches the agent's own resume command in the session's project folder. The id may be a unique prefix. If the
folder no longer exists you are asked for one (interactive only).

### `transfer`

```
agentsessions transfer <agent> <id> --to <agent> [--polish]
```

Builds a handoff from the source session, writes `<project>/.docs/handoff.md`, and starts the target agent with the
opening prompt `Read <that file> and continue the work described there.` If the target cannot take an opening
prompt the text is copied to the clipboard instead. Source and target must differ.

### `rename`

```
agentsessions rename <agent> <id> [label words...] [--clear]
```

Sets a label shown instead of the agent's own title. Omit the label or pass `--clear` to remove it. See Labels.

### `restore`

```
agentsessions restore <archive folder>
```

Puts an archived session back exactly where it came from. The folder is the one under the archive root that
contains `manifest.json` (for example `<archiveRoot>\claude\<id>`).

## Safety rules

- `clean` is a dry run unless you pass `--apply`.
- The default action is archive, not delete. Delete must be asked for explicitly with `--delete` (or `d` in the TUI)
  and also needs `--apply`.
- Every applied cleanup asks for confirmation (default No); `--yes` skips it explicitly. The TUI needs a deliberate `y`.
- Sessions with recent activity (within `recentActiveMinutes`) are flagged as possibly still running; the plan shows
  a warning and the confirmation prompt counts them.
- The archive lives under `archiveRoot` (default: `archive` inside the settings folder, see Settings), laid out as
  `<agent>/<id>/` with a `manifest.json` and the original files. Moves are verified, and if anything fails part way
  the originals are put back.
- `restore` never overwrites: if anything already exists at an original path, it stops and changes nothing.
- A transfer never overwrites `.docs/handoff.md`. An existing file is first copied to
  `.docs/handoff.<yyyy-MM-ddTHHmm>.md` (UTC; `-2`, `-3` on collision), the copy is verified byte for byte, and the
  new handoff links to it in its header. A read-only or locked existing handoff stops the transfer.
- Paths are checked before any move or delete, and anything untrusted (titles, paths) is printed with control
  characters removed.
- The source session is only read, never modified, by a transfer.

## How transfer works

Transfer is direct extraction: the tool reads the source session's transcript with its own parser and writes a
structured Markdown handoff (source reference, original request, recent activity, files touched, last commands,
where it stopped). The
handoff header records the source agent, session id and store path. Nothing leaves the machine, and no model is
involved unless you opt in.

Optional polish: an extra pass through a command you configure. It is off by default.

- Enable with `polishEnabled: true` and set `polishCommand` (settings below).
- Use it with `transfer --polish`, or answer `y` to the prompt in the TUI.
- Contract: the command is run without a shell (arguments split on spaces, double quotes group), receives the handoff
  Markdown on stdin and must write the polished Markdown to stdout and exit 0. It has a 120 second limit and capped
  output sizes.
- The polish is thrown away (and the extracted handoff used, with a note on screen) if the command is missing, fails,
  times out, returns nothing, or drops the `## Source` lines.

## Labels

`rename` and the TUI `n` key store a label per session in `labels.json` in the settings folder, keyed by `agent:id`.
The label replaces the title in `list` and the TUI. This file is the only thing the feature writes: agent session
stores are never modified, so a label cannot corrupt or confuse an agent. Labels are limited to 120 characters; a
corrupt labels file is kept as a backup copy and treated as empty.

## Settings

A JSON file, `settings.json`, in `%LOCALAPPDATA%\AgentSessions\` (the same folder holds `labels.json` and, by
default, the `archive` folder), i.e. `settings.json` next to `labels.json`.

- Key names are case-insensitive (`staleDays`, `staleDays` and `STALEDAYS` are the same key); the tool writes camelCase.
- Unknown or misspelled keys are ignored, so check the spelling against the table.
- A missing file means all defaults. A file that exists but is malformed or unreadable is also ignored and the
  defaults apply; `list` and `clean` then print a dim line, `settings.json could not be read; using defaults.`, but
  the exit code is unchanged. (The TUI shows no such line.)

| Key | Default | Meaning |
| --- | --- | --- |
| `staleDays` | `30` | Sessions inactive longer than this are stale |
| `archiveRoot` | settings folder `\archive` | Where `clean --archive` puts sessions |
| `polishEnabled` | `false` | Allow the opt-in polish pass |
| `polishCommand` | none | Command for polish (stdin to stdout), e.g. `mymodel --polish` |
| `recentActiveMinutes` | `15` | Activity within this window counts as "possibly active" |

```json
{ "staleDays": 45, "archiveRoot": "D:\\session-archive", "polishEnabled": false }
```

## Supported agents

| Agent | List | Resume | Archive / delete | Transfer |
| --- | --- | --- | --- | --- |
| Claude Code | yes | yes | yes | yes (source and target) |
| Codex CLI | yes | yes | yes | yes (source and target) |
| GitHub Copilot CLI | yes | yes | no | yes (source and target) |

Copilot cleanup is declined: its `session-store.db` holds per-session rows that this tool will not edit, so removing
only the session folder would leave the agent's index inconsistent. Copilot sessions are listed, resumed and
transferred, but never archived or deleted.

Not yet supported: Antigravity, OpenCode, Hermes.

## Known limitations

- Windows-first. Other platforms are untested.
- **The TUI has been tested with a simulated console only. It has not been exercised in a real terminal.** Expect
  rough edges there; the plain commands are the better-tested path.
- On Windows, codex and copilot are launched through their `.cmd` shims, and the launch is refused if any argument or
  the shim path contains `!`, `^`, `%` or `"` (cmd.exe would reinterpret them). Run the agent by hand in that case.
- A label that begins with `-` needs `--` before it: `agentsessions rename claude <id> -- -my label`.
- Format-control characters (Unicode category Cf, such as zero-width joiners) are stripped from labels.
- Copilot: hidden (dot-prefixed) folders under `session-state` are treated as the agent's bookkeeping and skipped by `list`, not counted as unreadable sessions.
- The agents' on-disk formats are not public contracts and can change; unreadable sessions are skipped and counted.

## Development

```
dotnet build
dotnet test
dotnet pack src/AgentSessions -c Release -o <output dir>
dnx AgentSessions --source <output dir> --yes -- --version   # try the package from a local feed
```

Tests use only temp folders and fakes; they never touch real agent data, settings or the clipboard.

### Format notes

What each agent writes to disk, and how it is parsed, is documented in [`docs/formats/`](docs/formats/):
[Claude](docs/formats/claude.md), [Codex](docs/formats/codex.md), [Copilot](docs/formats/copilot.md).
