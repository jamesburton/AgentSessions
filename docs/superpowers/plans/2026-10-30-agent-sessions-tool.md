# AgentSessions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A .NET 10 tool, published to NuGet and runnable with `dnx`, that lists, resumes, cleans up and transfers (via handoff) coding-agent sessions across Claude, Codex and Copilot, with a TUI as the primary interface.

**Architecture:** One tool project with a plug-in `ISessionAdapter` per agent (discover, transcript, launch, owned files). Agent-neutral services (listing, cleanup, handoff, launch) work only against that interface. CLI commands and the TUI are thin layers over the services.

**Tech Stack:** .NET 10, C#, xUnit, Spectre.Console + Spectre.Console.Cli (the plan's call; the spec left the TUI library to the builder), `Microsoft.Data.Sqlite` (read-only, Copilot spike only if needed), System.Text.Json.

**Spec:** `docs/superpowers/specs/2026-10-30-agent-sessions-tool-design.md`

## Global Constraints

- .NET 10, C#, packaged as a .NET tool, published to NuGet, runnable with `dnx`.
- Windows-first; must not preclude macOS/Linux (all paths derived from the user's home directory).
- Discovery is read-only and safe while agents are running (open files with `FileShare.ReadWrite`; never lock out an agent).
- Conversation content never leaves the machine except the opt-in polish step.
- Nothing is lost compared with native resume: same-agent resume uses the agent's native resume; archive is restorable; delete is explicit; dry-run is the default; a transfer never overwrites (existing `.docs/handoff.md` is backed up as a timestamped sibling and linked).
- Stale threshold default 30 days, configurable by setting and `--older-than` flag.
- Active sessions (recent write or lock) warn but still allow archive/delete after confirmation.
- Adapters fail soft: an unreadable session is reported as skipped, never crashes the list.
- v1 agents end to end: Claude, Codex, Copilot. Copilot cleanup may ship declined if the spike (Task 6) finds it unsafe.
- Tests use synthetic fixture sessions written to a temp directory. Never commit real session content.

## Review Focus

Failure modes the spec implies but that no feature test would otherwise exercise; each is pinned in the named task.

1. Session file whose last JSONL line is truncated (agent killed mid-write): the session is still listed and handoff still works. (Tasks 4, 5, 6, 9)
2. Session file being written or locked by a running agent: read succeeds with shared access. (Task 4)
3. Very large session (20 MB+): read by streaming, not loaded whole; list stays fast. (Tasks 4, 5, 6)
4. Project path that has spaces, non-ASCII characters, or no longer exists: resume/transfer asks for a folder, then refuses. (Tasks 11, 13)
5. Name collisions: backup of `handoff.md` within the same minute, and restoring an archived session onto an existing path, never overwrite. (Tasks 8, 10)

---

## File Structure

```
AgentSessions.sln
src/AgentSessions/
  AgentSessions.csproj            tool packaging (PackAsTool, ToolCommandName=agentsessions)
  Program.cs                      entry: no args -> TUI, else CLI commands
  Model/       SessionInfo.cs, TranscriptEntry.cs, LaunchSpec.cs, AdapterCapabilities.cs, ISessionAdapter.cs
  Adapters/    ClaudeAdapter.cs, CodexAdapter.cs, CopilotAdapter.cs, JsonlReader.cs
  Services/    SessionCatalog.cs, CleanupService.cs, ArchiveStore.cs, HandoffBuilder.cs,
               HandoffWriter.cs, HandoffPolisher.cs, LaunchService.cs
  Config/      AppSettings.cs, SettingsStore.cs
  Cli/         ListCommand.cs, CleanCommand.cs, ResumeCommand.cs, TransferCommand.cs, RestoreCommand.cs
  Tui/         MainScreen.cs
tests/AgentSessions.Tests/        one test file per source file, plus Fixtures/SessionFixtures.cs
docs/formats/                     claude-format.md, codex-format.md, copilot-format.md (schema notes from the format spikes)
```

---

### Task 1: Scaffold solution and packaging

**Files:**
- Create: `AgentSessions.sln`, `src/AgentSessions/AgentSessions.csproj`, `src/AgentSessions/Program.cs`, `tests/AgentSessions.Tests/AgentSessions.Tests.csproj`, `tests/AgentSessions.Tests/SmokeTests.cs`

**Interfaces:**
- Produces: tool command `agentsessions`; `Program.Main(string[] args) : int`.

- [ ] **Step 1:** Create the solution, a console project targeting `net10.0` with `PackAsTool=true`, `ToolCommandName=agentsessions`, `PackageId=AgentSessions`, and an xUnit test project referencing it. Add the Spectre.Console and Spectre.Console.Cli packages.
- [ ] **Step 2: Write the failing test** `SmokeTests.Main_with_version_flag_returns_zero` asserting `Program.Main(["--version"]) == 0`.
- [ ] **Step 3:** Run `dotnet test`; expected FAIL (no `Main` behavior).
- [ ] **Step 4:** Implement `Main` returning 0 and printing the assembly version for `--version`.
- [ ] **Step 5:** Run `dotnet test`; expected PASS. Run `dotnet pack src/AgentSessions -o artifacts` and expect `AgentSessions.<version>.nupkg`.
- [ ] **Step 6: Commit** `feat: scaffold agentsessions tool and tests`

### Task 2: Core model and adapter interface

**Files:**
- Create: `src/AgentSessions/Model/*.cs` (listed above), `tests/AgentSessions.Tests/Fixtures/SessionFixtures.cs`
- Test: `tests/AgentSessions.Tests/Model/ModelTests.cs`

**Interfaces:**
- Produces (all later tasks use these exactly):
  - `record SessionInfo(string Agent, string Id, string? ProjectPath, string Title, DateTimeOffset LastActivity, long SizeBytes, string StorePath)`
  - `enum Role { User, Assistant, Tool, System }`
  - `record TranscriptEntry(Role Role, string Text, DateTimeOffset? Timestamp, string? ToolName, IReadOnlyList<string> FilesTouched)`
  - `record LaunchSpec(string FileName, IReadOnlyList<string> Args, string WorkingDirectory)`
  - `[Flags] enum AdapterCapabilities { List=1, Resume=2, LaunchWithPrompt=4, Transcript=8, Archive=16, Delete=32 }`
  - `record SkippedSession(string Path, string Reason)`; `record DiscoveryResult(IReadOnlyList<SessionInfo> Sessions, IReadOnlyList<SkippedSession> Skipped)`
  - ```csharp
    interface ISessionAdapter {
      string Name { get; }
      AdapterCapabilities Capabilities { get; }
      DiscoveryResult Discover();
      IEnumerable<TranscriptEntry> ReadTranscript(SessionInfo s);   // streaming
      LaunchSpec BuildResume(SessionInfo s);
      LaunchSpec BuildNew(string projectPath, string prompt);
      IReadOnlyList<string> OwnedPaths(SessionInfo s);              // files/dirs to archive or delete
      void AfterRemove(SessionInfo s);                              // update agent index if any; default no-op
    }
    ```
  - `SessionFixtures` helpers: `static string TempHome()` (a fresh temp directory), `static void WriteLines(string path, params string[] lines)`.

- [ ] **Step 1: Write failing tests** `SessionInfo_is_value_equal_by_fields` and `Capabilities_flags_combine` (assert `(List|Resume).HasFlag(List)`).
- [ ] **Step 2:** Run; expected FAIL (types missing).
- [ ] **Step 3:** Add the types exactly as above, plus a default-interface-method no-op for `AfterRemove`.
- [ ] **Step 4:** Run; expected PASS. **Step 5: Commit** `feat: core model and adapter interface`

### Task 3: Settings

**Files:**
- Create: `src/AgentSessions/Config/AppSettings.cs`, `SettingsStore.cs`
- Test: `tests/AgentSessions.Tests/Config/SettingsStoreTests.cs`

**Interfaces:**
- Produces: `record AppSettings(int StaleDays = 30, string? ArchiveRoot = null, bool PolishEnabled = false, string? PolishCommand = null, int RecentActiveMinutes = 15)`; `class SettingsStore(string settingsPath)` with `AppSettings Load()` and `void Save(AppSettings)`; `static string DefaultDataDir()` (per-user app-data folder, `AgentSessions`).

- [ ] **Step 1: Write failing tests** `Load_returns_defaults_when_file_missing` (StaleDays==30, PolishEnabled==false), `Save_then_Load_roundtrips`, `Load_with_corrupt_json_returns_defaults`.
- [ ] **Step 2:** Run; FAIL. **Step 3:** Implement with System.Text.Json; corrupt file returns defaults. **Step 4:** PASS. **Step 5: Commit** `feat: settings store`

### Task 4: Claude adapter

**Files:**
- Create: `src/AgentSessions/Adapters/JsonlReader.cs`, `ClaudeAdapter.cs`, `docs/formats/claude-format.md`
- Test: `tests/AgentSessions.Tests/Adapters/ClaudeAdapterTests.cs`

**Interfaces:**
- Consumes: Task 2 model.
- Produces: `static class JsonlReader { static IEnumerable<JsonElement> Read(string path) }` (streams lines, opens with `FileShare.ReadWrite`, skips blank or unparseable lines, including a truncated final line); `class ClaudeAdapter(string homeDir) : ISessionAdapter` with `Name == "claude"`, store root `<home>/.claude/projects`.

- [ ] **Step 1 (format spike):** Inspect ONE real session under `~/.claude/projects/*/` printing only JSON property names, never message text. Record the schema (where the user prompt, assistant text, tool calls, cwd, timestamps and session id live; how the project folder name encodes the path) in `docs/formats/claude-format.md`. Build fixtures from this schema.
- [ ] **Step 2: Write failing tests** (fixture sessions in a temp home):
  - `Discover_lists_session_with_title_from_first_user_prompt`
  - `Discover_recovers_project_path_from_session_content` (path with spaces)
  - `Discover_skips_subagents_folder_as_separate_sessions`
  - `Truncated_final_line_still_lists_session_and_reads_earlier_entries` (Review Focus 1)
  - `Read_succeeds_while_file_is_open_for_write` (Review Focus 2)
  - `ReadTranscript_streams_large_file_without_loading_whole` (generate ~20 MB fixture; assert it completes and yields the first entry before reading all)  (Review Focus 3)
  - `BuildResume_returns_claude_resume_with_session_id_in_project_dir` (`claude --resume <id>`, WorkingDirectory = ProjectPath)
  - `BuildNew_passes_prompt_as_positional_argument`
  - `OwnedPaths_includes_session_jsonl_and_its_sibling_folder`
- [ ] **Step 3:** Run; FAIL. **Step 4:** Implement `JsonlReader` and `ClaudeAdapter` (Capabilities: all). `LastActivity` from the newest entry timestamp, falling back to file mtime. **Step 5:** PASS. **Step 6: Commit** `feat: claude adapter`

### Task 5: Codex adapter

**Files:**
- Create: `src/AgentSessions/Adapters/CodexAdapter.cs`, `docs/formats/codex-format.md`
- Test: `tests/AgentSessions.Tests/Adapters/CodexAdapterTests.cs`

**Interfaces:**
- Produces: `class CodexAdapter(string homeDir) : ISessionAdapter`, `Name == "codex"`. Scans `<home>/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`; also lists `<home>/.codex/archived_sessions/` sessions flagged by `StorePath`.

- [ ] **Step 1 (format spike):** As Task 4 step 1 for `~/.codex/sessions` (property names only); write `docs/formats/codex-format.md`.
- [ ] **Step 2: Write failing tests:** `Discover_lists_rollout_with_id_from_filename`, `Discover_recovers_project_path_from_session_meta`, `Truncated_final_line_still_lists_session`, `BuildResume_returns_codex_resume_with_id` (`codex resume <id>`), `BuildNew_passes_prompt_positionally`, `OwnedPaths_returns_the_rollout_file`, `Discover_includes_archived_sessions`.
- [ ] **Step 3:** FAIL. **Step 4:** Implement (Capabilities: all). **Step 5:** PASS. **Step 6: Commit** `feat: codex adapter`

### Task 6: Copilot adapter and cleanup-safety spike

**Files:**
- Create: `src/AgentSessions/Adapters/CopilotAdapter.cs`, `docs/formats/copilot-format.md`
- Test: `tests/AgentSessions.Tests/Adapters/CopilotAdapterTests.cs`

**Interfaces:**
- Produces: `class CopilotAdapter(string homeDir) : ISessionAdapter`, `Name == "copilot"`. Scans `<home>/.copilot/session-state/<id>/` (`workspace.yaml`, `events.jsonl`, `checkpoints/`).

- [ ] **Step 1 (format spike):** Inspect one real session folder (property names only; `workspace.yaml` keys). Record in `docs/formats/copilot-format.md`. Use the `checkpoints/` summaries as the title source when present.
- [ ] **Step 2 (safety spike, decision gate):** Open `~/.copilot/session-store.db` READ-ONLY and determine whether it holds per-session rows that must change when a session folder is archived or deleted. Record the finding in `docs/formats/copilot-format.md` as one of: **safe** (folder move is enough or rows can be updated consistently) or **unsafe** (cleanup declined). Do not modify the real database.
- [ ] **Step 3: Write failing tests:** `Discover_lists_session_with_title_from_checkpoint`, `Discover_recovers_project_path_from_workspace_yaml`, `Truncated_events_line_still_lists_session`, `BuildResume_returns_copilot_resume_with_id` (`copilot --resume <id>`), `BuildNew_uses_interactive_prompt_flag` (`-i <prompt>`), and by outcome of step 2 either `Capabilities_include_archive_and_delete` or `Capabilities_exclude_archive_and_delete`.
- [ ] **Step 4:** FAIL. **Step 5:** Implement; if unsafe, Capabilities omit `Archive|Delete`. If safe, implement `AfterRemove` to keep the database consistent. **Step 6:** PASS. **Step 7: Commit** `feat: copilot adapter`

### Task 7: Session catalog (merged list, stale, filters)

**Files:**
- Create: `src/AgentSessions/Services/SessionCatalog.cs`
- Test: `tests/AgentSessions.Tests/Services/SessionCatalogTests.cs`

**Interfaces:**
- Consumes: `ISessionAdapter`, `AppSettings`.
- Produces: `record ListedSession(SessionInfo Info, bool IsStale, bool IsActive, AdapterCapabilities Capabilities)`; `record SessionFilter(string? Agent = null, string? ProjectContains = null, TimeSpan? OlderThan = null)`; `class SessionCatalog(IEnumerable<ISessionAdapter> adapters, AppSettings settings, Func<DateTimeOffset> now)` with `(IReadOnlyList<ListedSession> Sessions, IReadOnlyList<SkippedSession> Skipped) List(SessionFilter? filter = null)`.

- [ ] **Step 1: Write failing tests** with fake adapters: `List_merges_agents_newest_first`, `Session_older_than_StaleDays_is_stale` (31 days stale, 29 not), `StaleDays_setting_is_respected`, `Session_within_RecentActiveMinutes_is_active`, `Filter_by_agent_and_project_and_age`, `One_adapter_throwing_does_not_break_list_and_is_reported_as_skipped`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement; wrap each adapter's `Discover` in try/catch converting failures to a `SkippedSession`. **Step 4:** PASS. **Step 5: Commit** `feat: session catalog`

### Task 8: Archive store and cleanup service

**Files:**
- Create: `src/AgentSessions/Services/ArchiveStore.cs`, `CleanupService.cs`
- Test: `tests/AgentSessions.Tests/Services/CleanupServiceTests.cs`

**Interfaces:**
- Consumes: `ISessionAdapter.OwnedPaths/AfterRemove`, `AppSettings.ArchiveRoot`.
- Produces: `enum CleanupAction { Archive, Delete }`; `record CleanupPlanItem(ListedSession Session, CleanupAction Action, IReadOnlyList<string> Paths, string? Warning, string? Declined)`; `class CleanupService(IReadOnlyDictionary<string, ISessionAdapter> adapters, ArchiveStore archive)` with `IReadOnlyList<CleanupPlanItem> Plan(IEnumerable<ListedSession> sessions, CleanupAction action)` (no side effects: the dry run), `CleanupResult Apply(IEnumerable<CleanupPlanItem> items)`, and `class ArchiveStore(string root)` with `string Archive(SessionInfo s, IReadOnlyList<string> paths)` (moves paths under `root/<agent>/<id>/` preserving each path relative to the home directory, writes `manifest.json`) and `void Restore(string archivedDir)` (throws if any destination exists).

- [ ] **Step 1: Write failing tests:** `Plan_has_no_side_effects`, `Plan_declines_action_when_adapter_lacks_capability` (Declined set, not an exception), `Plan_warns_for_active_session_but_allows`, `Archive_moves_files_and_writes_manifest_preserving_relative_path`, `Restore_puts_files_back_exactly`, `Restore_refuses_when_destination_exists_and_changes_nothing` (Review Focus 5), `Delete_removes_owned_paths_only`, `Apply_calls_AfterRemove`, `Apply_with_one_failure_continues_and_reports`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** PASS. **Step 5: Commit** `feat: archive store and cleanup service`

### Task 9: Handoff builder (direct extraction)

**Files:**
- Create: `src/AgentSessions/Services/HandoffBuilder.cs`
- Test: `tests/AgentSessions.Tests/Services/HandoffBuilderTests.cs`

**Interfaces:**
- Produces: `record HandoffOptions(int MaxRecentEntries = 20, int MaxCharsPerEntry = 2000, int MaxCommands = 10)`; `static class HandoffBuilder { static string Build(SessionInfo s, IEnumerable<TranscriptEntry> transcript, HandoffOptions? options = null) }` returning Markdown with sections: Source (agent, id, StorePath, project, last activity), Original request, Recent activity, Files touched (distinct), Last commands, Where it stopped.

- [ ] **Step 1: Write failing tests:** `Includes_original_request_from_first_user_entry`, `Always_contains_source_agent_id_and_store_path`, `Caps_recent_entries_and_truncates_long_entries` (output shorter than input; includes a truncation marker), `Lists_distinct_files_touched`, `Lists_last_commands_from_tool_entries_with_shell_names`, `Empty_transcript_still_produces_source_section_and_placeholder` (Review Focus 1/3), `Streams_input_without_materialising_more_than_needed` (feed a 100k-entry enumerable and assert it completes).
- [ ] **Step 2:** FAIL. **Step 3:** Implement with a single pass and a bounded ring buffer for recent entries. **Step 4:** PASS. **Step 5: Commit** `feat: handoff builder`

### Task 10: Handoff writer (backup and link)

**Files:**
- Create: `src/AgentSessions/Services/HandoffWriter.cs`
- Test: `tests/AgentSessions.Tests/Services/HandoffWriterTests.cs`

**Interfaces:**
- Produces: `record HandoffWriteResult(string HandoffPath, string? BackupPath)`; `class HandoffWriter(Func<DateTimeOffset> now)` with `HandoffWriteResult Write(string projectPath, string content, SessionInfo source)` writing `<project>/.docs/handoff.md`. If it exists it is first copied to `handoff.<yyyy-MM-ddTHHmm>.md` (adding `-2`, `-3` ... on collision); the new file records `Replaced: <backup file name>` and the source session.

- [ ] **Step 1: Write failing tests:** `Creates_docs_folder_and_file_when_none_exists` (BackupPath null), `Existing_handoff_is_backed_up_unchanged_before_write`, `New_handoff_links_backup_and_source_session`, `Two_writes_in_the_same_minute_do_not_overwrite_the_first_backup` (Review Focus 5).
- [ ] **Step 2:** FAIL. **Step 3:** Implement; copy the backup before touching the original and verify byte equality before overwrite. **Step 4:** PASS. **Step 5: Commit** `feat: handoff writer with backup and link`

### Task 11: Launch service

**Files:**
- Create: `src/AgentSessions/Services/LaunchService.cs`
- Test: `tests/AgentSessions.Tests/Services/LaunchServiceTests.cs`

**Interfaces:**
- Consumes: `LaunchSpec`, `ISessionAdapter.BuildResume/BuildNew`.
- Produces: `interface IProcessRunner { int Run(LaunchSpec spec); }`; `interface IClipboard { void Set(string text); }`; `class LaunchService(IProcessRunner runner, IClipboard clipboard)` with `LaunchResult Resume(ISessionAdapter a, SessionInfo s, Func<string?> askForFolder)` and `LaunchResult StartWithHandoff(ISessionAdapter target, string projectPath, string handoffPath)`. `record LaunchResult(bool Launched, string? Message)`.
- The opening prompt text is: `Read <handoffPath> and continue the work described there.`

- [ ] **Step 1: Write failing tests** with fake runner/clipboard: `Resume_runs_adapter_resume_spec`, `Resume_with_missing_project_asks_for_folder_then_runs_there`, `Resume_with_missing_project_and_declined_prompt_refuses_and_does_not_run` (Review Focus 4), `Resume_with_nonexistent_folder_refuses`, `StartWithHandoff_runs_BuildNew_with_the_opening_prompt`, `StartWithHandoff_falls_back_to_clipboard_when_adapter_lacks_LaunchWithPrompt` (prompt on clipboard, message says so), `Project_path_with_spaces_and_non_ascii_is_passed_intact`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement. The real `IProcessRunner` starts a child with inherited console and waits; on Windows run through `cmd.exe /c` so npm `.cmd`/`.ps1` shims resolve. The real `IClipboard` pipes to `clip.exe` on Windows (`pbcopy` / `xclip` elsewhere). **Step 4:** PASS. **Step 5: Commit** `feat: launch service`

### Task 12: Polish (opt-in, fallback)

**Files:**
- Create: `src/AgentSessions/Services/HandoffPolisher.cs`
- Test: `tests/AgentSessions.Tests/Services/HandoffPolisherTests.cs`

**Interfaces:**
- Produces: `interface IModelCaller { string Polish(string handoffMarkdown); }`; `class HandoffPolisher(AppSettings settings, IModelCaller? caller)` with `(string Content, string? Note) Apply(string extracted)`.

- [ ] **Step 1: Write failing tests:** `Disabled_returns_extracted_unchanged_and_never_calls_model`, `Enabled_returns_polished_output`, `Enabled_but_model_throws_returns_extracted_with_note`, `Enabled_but_model_returns_empty_returns_extracted_with_note`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement. The real `IModelCaller` runs `settings.PolishCommand` (a user-configured command that reads the handoff on stdin and writes the polished text to stdout) with a timeout; no command configured counts as a failure. **Step 4:** PASS. **Step 5: Commit** `feat: opt-in handoff polish with fallback`

### Task 13: CLI commands

**Files:**
- Create: `src/AgentSessions/Cli/{List,Clean,Resume,Transfer,Restore}Command.cs`; Modify: `Program.cs`
- Test: `tests/AgentSessions.Tests/Cli/CliTests.cs`

**Interfaces:**
- Consumes: Tasks 3, 7-12.
- Produces: commands `list [--agent A] [--project P] [--older-than DAYS] [--stale]`; `clean [--older-than DAYS] [--archive|--delete] [--apply] [--ids ...]` (dry run unless `--apply`); `resume <agent> <id>`; `transfer <agent> <id> --to <agent> [--polish]`; `restore <archived-dir>`. `Program.Main` with no arguments starts the TUI (Task 14).

- [ ] **Step 1: Write failing tests** driving commands through an in-memory console and fake adapters: `list_prints_agent_project_age_title_and_stale_tag`, `clean_without_apply_changes_nothing_and_prints_plan`, `clean_apply_archives_stale_sessions`, `clean_uses_older_than_flag_over_setting`, `clean_reports_declined_for_agent_without_capability`, `transfer_writes_handoff_backs_up_existing_and_launches_target`, `transfer_with_polish_falls_back_when_model_fails`, `resume_with_unknown_id_exits_nonzero`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement with Spectre.Console.Cli; compose real adapters with the user's home directory. **Step 4:** PASS. **Step 5: Commit** `feat: cli commands`

### Task 14: TUI main screen

**Files:**
- Create: `src/AgentSessions/Tui/MainScreen.cs`; Modify: `Program.cs`
- Test: `tests/AgentSessions.Tests/Tui/MainScreenTests.cs`

**Interfaces:**
- Consumes: `SessionCatalog`, `CleanupService`, `LaunchService`, `HandoffBuilder`, `HandoffWriter`, `HandoffPolisher`.
- Produces: `class MainScreen(IAnsiConsole console, ...)` with `void Run()`; and a testable `MainScreenModel` holding selection, marks, and filter state with methods `MoveNext()`, `MovePrevious()`, `ToggleMark()`, `SelectStale()`, `SetFilter(SessionFilter)`.
- Layout is the user-chosen **flat list, newest first**: columns AGENT, PROJECT, LAST ACTIVE, SIZE, TITLE, `[stale]` tag. Keys: Enter resume, `t` transfer (target-agent picker, then optional polish), `a` archive, `d` delete, `/` filter, `s` select stale, space mark, `q` quit.

- [ ] **Step 1: Write failing model tests:** `SelectStale_marks_only_stale_sessions`, `ToggleMark_toggles_current_row`, `Navigation_clamps_at_ends`, `Filter_narrows_rows`, and a rendering test `Renders_header_columns_and_stale_tag` using `TestConsole`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement the model and a Spectre.Console `Live`/key loop rendering the table. Destructive keys show the cleanup plan (with active-session warnings and declined reasons) and require confirmation. Return to the list after any launched agent exits. **Step 4:** PASS. **Step 5: Commit** `feat: tui main screen`

### Task 15: End-to-end verification and docs

**Files:**
- Create: `README.md`; Test: `tests/AgentSessions.Tests/EndToEndTests.cs`

- [ ] **Step 1: Write failing test** `Fixture_codex_session_transfers_to_claude`: temp home with a fixture Codex session and project; run the transfer service path with fake runner; assert `.docs/handoff.md` exists with the source id, an existing handoff was backed up, and the runner was invoked with Claude's `BuildNew` spec and the opening prompt.
- [ ] **Step 2:** FAIL. **Step 3:** Wire up whatever is missing. **Step 4:** `dotnet test` all PASS.
- [ ] **Step 5 (packaging):** `dotnet pack` then run the package from the local feed with `dnx AgentSessions --version --source <artifacts dir>`; expected: prints the version.
- [ ] **Step 6 (real-data smoke, read-only):** run `agentsessions list` against this machine; confirm Claude/Codex/Copilot sessions appear and any unreadable ones are reported as skipped. Do NOT run `clean --apply` against real data.
- [ ] **Step 7:** Write `README.md` (install via `dnx`, commands, TUI keys, settings, archive location, the safety rules). **Step 8: Commit** `feat: end-to-end verification and docs`

---

### Task 16: Session labels (user amendment, executed BEFORE Task 15)

The user asked (2026-10-30, mid-build) for the ability to name/label sessions so they can be recognised before opening. Decision: labels are **tool-owned**, stored in `labels.json` in the tool's data directory keyed by `agent:id`. They NEVER modify any agent's own session store (anti-goal: losing information). A label replaces the title in listings; the agent's own title is unchanged.

**Files:**
- Create: `src/AgentSessions/Services/LabelStore.cs`, `src/AgentSessions/Cli/RenameCommand.cs`
- Modify: `src/AgentSessions/Services/SessionCatalog.cs` (apply labels), `src/AgentSessions/Cli/AppServices.cs` (compose the store), `src/AgentSessions/Cli/ListCommand.cs` (show the label), `src/AgentSessions/Tui/MainScreen.cs` (key `n`), `src/AgentSessions/Program.cs` (register `rename`)
- Test: `tests/AgentSessions.Tests/Services/LabelStoreTests.cs`, additions to the catalog, CLI and TUI tests

**Interfaces:**
- Produces: `interface ILabelSource { IReadOnlyDictionary<string,string> Load(); }`; `class LabelStore(string path) : ILabelSource` with `Load()` (corrupt or missing file => empty, never throws), `void Set(string agent, string id, string? label)` (null/blank removes; key is `agent:id`, agent lowercased) and `static string? Sanitize(string? label)` (trim, strip control characters, collapse newlines/tabs to spaces, max 120 chars, blank => null). `Set` writes atomically (temp file + replace) and, if the existing file is unreadable/corrupt, first copies it to `labels.corrupt-<yyyyMMddTHHmmss>.json` instead of discarding it. `ListedSession` gains an optional trailing `string? Label = null`. `SessionCatalog(...)` gains an optional trailing `ILabelSource? labels = null` and fills `Label`. Display rule everywhere (list table, TUI): show `Label` when set, else `Info.Title`; the label is untrusted text: render via `CliSupport.Esc`.
- CLI: `rename <agent> <id> [label...]` (unique id prefix allowed like `resume`; omitted label or `--clear` removes the label; unknown/ambiguous id => exit 1; label over 120 chars is truncated with a message). TUI: key `n` on the current row opens a text prompt "Label (empty to clear):" prefilled with the current label; the list reloads and shows it; footer help gains `[n] label`.

- [ ] **Step 1: Write the failing tests** (names): `LabelStore_roundtrips_and_normalises_key`, `Set_blank_removes_label`, `Sanitize_strips_controls_and_caps_length`, `Corrupt_file_loads_empty_and_Set_preserves_a_copy_before_writing`, `Set_never_leaves_a_truncated_file` (temp+replace; no stray temp), `Catalog_applies_label_and_keeps_original_title`, `List_shows_label_instead_of_title`, `rename_sets_and_clears_label`, `rename_unknown_id_exits_nonzero`, `Tui_n_key_sets_label_and_list_shows_it`, `Labels_survive_reload`, `Rename_writes_only_labels_json_and_never_touches_agent_stores` (assert the agent store files are byte-identical and no other file was created).
- [ ] **Step 2:** Run; expected FAIL (types missing).
- [ ] **Step 3: Implement** as specified (YAGNI: no label search, no colour, no label history).
- [ ] **Step 4:** Run the covering tests, then the full suite; expected PASS.
- [ ] **Step 5: Commit** `feat: session labels`

---

## Self-review notes

- Spec coverage: list (7, 13, 14), resume (11, 13), cleanup archive/delete/dry-run/stale/manual (8, 13, 14), transfer with handoff, backup and link, launch, clipboard fallback (9-11, 13), polish opt-in with fallback (12), TUI flat list (14), Copilot test-then-decline (6), fail-soft adapters (4-7), project-folder ask-then-refuse (11), active sessions warn-but-allow (7, 8, 14), archive in tool data folder preserving relative paths (8), `dnx` packaging (1, 15).
- Not in v1 per spec: Antigravity, OpenCode, Hermes adapters (each is one new `ISessionAdapter` file); native full-session conversion.
- "Hide from the agent's own picker after archive" is realised through `AfterRemove` per adapter (Tasks 4-6); where an adapter cannot do it safely it omits the `Archive` capability.
