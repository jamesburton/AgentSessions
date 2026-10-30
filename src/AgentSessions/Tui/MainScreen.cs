using AgentSessions.Cli;
using AgentSessions.Model;
using AgentSessions.Services;
using Spectre.Console;
using static AgentSessions.Cli.CliSupport;

namespace AgentSessions.Tui;

/// <summary>
/// The interactive main screen: a flat, newest-first list across all agents. A thin layer over the services;
/// every destructive action goes through <see cref="Confirm"/> first.
/// </summary>
public sealed class MainScreen(IAnsiConsole console, AppServices services, IKeySource keys)
{
    private readonly List<string> _status = [];   // markup lines, already escaped
    private MainScreenModel _model = null!;

    /// <summary>Test seam: replaces <c>CleanupService.Apply</c>.</summary>
    internal Func<IEnumerable<CleanupPlanItem>, CleanupResult>? ApplyOverride { get; init; }

    public int Run()
    {
        if (!Interactive)
        {
            console.MarkupLine("The interactive screen needs a terminal. Use [bold]agentsessions list[/] or [bold]agentsessions clean[/] instead.");
            return 1;
        }

        try { _model = new MainScreenModel(services.Catalog.List().Sessions, services.Now); }
        catch (Exception ex)
        {
            console.MarkupLine($"[red]Could not list sessions: {Esc(ex.Message)}[/]");
            return 1;
        }

        while (true)
        {
            try { Render(); }
            catch (Exception ex) { console.MarkupLine($"[red]Display error: {Esc(ex.Message)}[/]"); }

            ConsoleKeyInfo key;
            try { key = keys.ReadKey(); }
            catch (Exception ex)
            {
                console.MarkupLine($"[red]Cannot read the keyboard: {Esc(ex.Message)}[/]");
                return 1;
            }

            _status.Clear();
            try
            {
                if (!Handle(key)) return 0;
            }
            catch (Exception ex)
            {
                _status.Add($"[red]Error: {Esc(ex.Message)}[/]");
            }

            // Anything the user must read is printed below what is on screen and waits for a key,
            // because the next Render() clears the screen (and scrollback).
            if (_status.Count > 0)
            {
                foreach (var line in _status) console.MarkupLine(line);
                _status.Clear();
                Pause();
            }
        }
    }

    private bool Interactive => IsInteractive(console, services.ConsoleEnvironment);

    /// <summary>
    /// Returns false to quit. Only <c>q</c> quits: a bare Escape is ignored here because a child agent can leave the
    /// console in VT input mode, where an arrow key arrives as ESC [ A (Escape still cancels prompts).
    /// </summary>
    private bool Handle(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow: _model.MovePrevious(); return true;
            case ConsoleKey.DownArrow: _model.MoveNext(); return true;
            case ConsoleKey.PageUp: _model.MoveBy(-PageSize); return true;
            case ConsoleKey.PageDown: _model.MoveBy(PageSize); return true;
            case ConsoleKey.Home: _model.MoveFirst(); return true;
            case ConsoleKey.End: _model.MoveLast(); return true;
            case ConsoleKey.Spacebar: _model.ToggleMark(); return true;
            case ConsoleKey.Enter: Resume(); return true;
            case ConsoleKey.Escape: return true;
        }

        switch (char.ToLowerInvariant(key.KeyChar))
        {
            case 'q': return false;
            case ' ': _model.ToggleMark(); break;
            case 's': _model.SelectStale(); break;
            case '/': AskFilter(); break;
            case 'r': Reload(); break;
            case 't': Transfer(); break;
            case 'n': EditLabel(); break;
            case 'a': Cleanup(CleanupAction.Archive); break;
            case 'd': Cleanup(CleanupAction.Delete); break;
        }
        return true;
    }

    private int PageSize => Math.Max(1, VisibleRows - 1);

    private int VisibleRows
    {
        get
        {
            int h;
            try { h = console.Profile.Height; } catch { h = 24; }
            return Math.Max(5, h - 9);
        }
    }

    private void Reload() => _model.Reload(services.Catalog.List().Sessions);

    // ---- rendering ----

    /// <summary>
    /// Redraws the whole screen. Clear() also erases the terminal scrollback (ESC[3J) BY DESIGN: the list is a
    /// full-screen view. Therefore anything the user must read is printed and followed by <see cref="Pause"/>
    /// before the next Render.
    /// </summary>
    private void Render()
    {
        console.Clear();
        var rows = _model.Rows;
        var now = services.Now();

        console.MarkupLine($"[bold]agentsessions[/]   filter: {Esc(Truncate(DescribeFilter(_model.Filter), 60))}"
            );

        var table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn("").AddColumn("").AddColumn("AGENT").AddColumn("PROJECT").AddColumn("LAST ACTIVE").AddColumn("SIZE").AddColumn("TITLE");
        table.AddRow(Head(""), Head(""), Head("AGENT"), Head("PROJECT"), Head("LAST ACTIVE"), Head("SIZE"), Head("TITLE"));

        // Cap long cells to the console width so rows never wrap and the window arithmetic holds.
        // Truncate the plain text first, then escape, so a cut never lands inside an escaped sequence.
        var first = Math.Clamp(_model.Cursor - VisibleRows / 2, 0, Math.Max(0, rows.Count - VisibleRows));
        var shown = rows.Skip(first).Take(VisibleRows).ToList();
        var agentW = Math.Max(5, shown.Select(r => r.Info.Agent.Length).DefaultIfEmpty(0).Max());
        var projW = Math.Max(7, shown.Select(r => Truncate(ProjectName(r.Info.ProjectPath), MaxProjectWidth).Length).DefaultIfEmpty(0).Max());
        // 2 narrow columns + 7 columns of padding, plus age/size columns and room for the " [stale]" tag.
        var titleMax = Math.Max(8, ConsoleWidth - (16 + agentW + projW + 11 + 9 + 8 + 2));
        for (var i = first; i < Math.Min(rows.Count, first + VisibleRows); i++)
        {
            var r = rows[i];
            var current = i == _model.Cursor;
            var title = Esc(Truncate(r.DisplayTitle, titleMax)) + (r.IsStale ? " " + Esc("[stale]") : "");
            string[] cells =
            [
                current ? ">" : " ",
                _model.MarkedKeys.Contains(MainScreenModel.KeyOf(r)) ? "*" : " ",
                Esc(r.Info.Agent), Esc(Truncate(ProjectName(r.Info.ProjectPath), MaxProjectWidth)), Age(now, r.Info.LastActivity),
                Size(r.Info.SizeBytes), title
            ];
            table.AddRow(current ? cells.Select(c => $"[bold]{c}[/]").ToArray() : cells);
        }
        console.Write(table);
        if (rows.Count == 0) console.MarkupLine("  (no sessions)");
        else if (rows.Count > VisibleRows) console.MarkupLine($"  ({_model.Cursor + 1}/{rows.Count})");

        console.MarkupLine($"{rows.Count} session(s), {_model.MarkedKeys.Count} marked, {_model.StaleCount} stale");
        console.MarkupLine(Esc("[Enter] resume  [t] transfer to…  [n] label  [a] archive  [d] delete"));
        console.MarkupLine(Esc("[/] filter  [s] select stale  [space] mark  [r] reload  [q] quit"));

        static string Head(string s) => $"[underline]{s}[/]";
    }

    private static string DescribeFilter(SessionFilter f) =>
        $"{(f.Agent is null ? "all agents" : "agent " + f.Agent)} · "
        + $"{(f.ProjectContains is null ? "all projects" : "project " + f.ProjectContains)} · "
        + $"{(f.OlderThan is { } o ? $"older than {(int)o.TotalDays}d" : "any age")}";

    private const int MaxProjectWidth = 24;

    private int ConsoleWidth
    {
        get
        {
            try { return Math.Max(40, console.Profile.Width); } catch { return 80; }
        }
    }

    /// <summary>Plain-text truncation (control characters removed first) with an ellipsis; escape AFTER calling this.</summary>
    private static string Truncate(string? text, int max)
    {
        var plain = StripControls(text ?? "");
        if (plain.Length <= max) return plain;
        var cut = Math.Max(0, max - 1);
        if (cut > 0 && char.IsHighSurrogate(plain[cut - 1])) cut--;   // never split a surrogate pair
        return plain[..cut] + "…";
    }

    private static string ProjectName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "(unknown)";
        var trimmed = path.TrimEnd('/', '\\');
        var name = trimmed.Length == 0 ? "" : Path.GetFileName(trimmed);
        return name.Length > 0 ? name : path;   // roots like C:\ have no last segment: show the full path
    }

    // ---- actions ----

    private void AskFilter()
    {
        string answer;
        try
        {
            answer = console.Prompt(new TextPrompt<string>(
                "Filter ([bold]agent:NAME project:TEXT older:DAYS[/] or a word; empty clears):").AllowEmpty());
        }
        catch (Exception) { _status.Add("[yellow]Could not read a filter; keeping the previous one.[/]"); return; }

        if (!FilterParser.TryParse(answer, out var filter, out var error, out var note))
        {
            _status.Add($"[red]Invalid filter: {Esc(error)} Keeping the previous filter.[/]");
            return;
        }
        if (filter.Agent is not null && !services.Adapters.ContainsKey(filter.Agent))
        {
            _status.Add($"[red]Unknown agent '{Esc(filter.Agent)}'. Known agents: {Esc(string.Join(", ", services.Adapters.Keys))}. Keeping the previous filter.[/]");
            return;
        }
        _model.SetFilter(filter);
        if (note is not null) _status.Add($"[yellow]{Esc(note)}[/]");
    }

    private void Resume()
    {
        if (_model.Current is not { } cur) { _status.Add("Nothing to resume."); return; }
        if (!services.Adapters.TryGetValue(cur.Info.Agent, out var adapter))
        {
            _status.Add($"[red]No adapter for agent '{Esc(cur.Info.Agent)}'.[/]");
            return;
        }
        if (!adapter.Capabilities.HasFlag(AdapterCapabilities.Resume))
        {
            _status.Add($"[red]The {Esc(adapter.Name)} adapter cannot resume sessions.[/]");
            return;
        }
        var result = services.Launcher.Resume(adapter, cur.Info, FolderPrompt(console, services.ConsoleEnvironment));
        DiscardTypedAhead();
        Report(result.Launched, result.Message);
        Reload();
    }

    /// <summary>Keys typed while a child agent ran (still buffered when it exits) must not drive the next action.</summary>
    private void DiscardTypedAhead()
    {
        try { keys.DiscardPending(); } catch (Exception) { /* nothing readable to drop */ }
    }

    private void EditLabel()
    {
        if (_model.Current is not { } cur) { _status.Add("Nothing to label."); return; }
        // The stored value is re-read: the row may be stale (e.g. a CLI rename while this screen is open).
        services.Labels.Load().TryGetValue(LabelStore.KeyOf(cur.Info.Agent, cur.Info.Id), out var current);
        var typed = ReadLine("Label (empty to clear):", current ?? "");
        if (typed is null) { _status.Add("Cancelled; the label is unchanged."); return; }

        var label = LabelStore.Sanitize(typed, out var truncated);
        if (label == current) { _status.Add("Label unchanged."); return; }

        LabelWrite write;
        try { write = services.Labels.Set(cur.Info.Agent, cur.Info.Id, label); }
        catch (Exception ex)
        {
            _status.Add($"[red]Could not save the label: {Esc(ex.Message)}[/]");
            return;
        }
        if (write.BackupPath is not null)
            _status.Add($"[yellow]Existing labels file was unreadable; a copy was kept as {Esc(write.BackupPath)}[/]");
        _status.Add(label is null ? "Label cleared." : $"Label set: {Esc(label)}");
        if (truncated) _status.Add($"[yellow]The label was truncated to {LabelStore.MaxLength} characters.[/]");
        Reload();
    }

    /// <summary>
    /// A one-line editor whose text starts as <paramref name="initial"/> (Spectre's TextPrompt cannot prefill).
    /// Enter (the Enter KEY only) accepts, Esc cancels, Backspace deletes; control and format characters are ignored
    /// and input stops at <see cref="LabelStore.MaxLength"/>. Null means cancelled (Esc, or no key could be read).
    /// Typed-ahead keys are dropped first so only keys pressed after the prompt appears are used.
    /// </summary>
    private string? ReadLine(string prompt, string initial)
    {
        if (!Interactive) return null;
        try { keys.DiscardPending(); } catch (Exception) { return null; }
        var text = new System.Text.StringBuilder(LabelStore.Sanitize(initial) ?? "");
        console.Markup(Esc(prompt) + " ");
        console.Write(new Text(text.ToString()));
        while (true)
        {
            ConsoleKeyInfo? k;
            try { k = console.Input.ReadKey(intercept: true); }
            catch (Exception) { console.WriteLine(); return null; }
            if (k is not { } key) { console.WriteLine(); return null; }
            if (key.Key == ConsoleKey.Enter) { console.WriteLine(); return text.ToString(); }
            if (key.Key == ConsoleKey.Escape) { console.WriteLine(); return null; }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length == 0) continue;
                var drop = text.Length >= 2 && char.IsLowSurrogate(text[^1]) ? 2 : 1;
                text.Length -= drop;
                console.Write(new Text("\b \b"));
            }
            else if (text.Length < LabelStore.MaxLength && !char.IsControl(key.KeyChar)
                && char.GetUnicodeCategory(key.KeyChar) != System.Globalization.UnicodeCategory.Format)
            {
                text.Append(key.KeyChar);
                console.Write(new Text(key.KeyChar.ToString()));
            }
        }
    }

    private void Transfer()
    {
        if (_model.Current is not { } cur) { _status.Add("Nothing to transfer."); return; }
        if (!services.Adapters.TryGetValue(cur.Info.Agent, out var source))
        {
            _status.Add($"[red]No adapter for agent '{Esc(cur.Info.Agent)}'.[/]");
            return;
        }
        var others = services.Adapters.Values
            .Where(a => !string.Equals(a.Name, source.Name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name, StringComparer.Ordinal).ToList();
        if (others.Count == 0) { _status.Add("[yellow]There is no other agent to transfer to.[/]"); return; }

        const string cancel = "(cancel)";
        string choice;
        try
        {
            choice = console.Prompt(new SelectionPrompt<string>()
                .Title("Transfer to which agent?")
                .AddChoices(others.Select(a => Markup.Escape(a.Name)).Append(Markup.Escape(cancel))));
        }
        catch (Exception) { _status.Add("[yellow]No choice was made; nothing was transferred.[/]"); return; }

        var target = others.FirstOrDefault(a => Markup.Escape(a.Name) == choice);
        if (target is null) { _status.Add("Transfer cancelled."); return; }

        var polish = false;
        if (services.Settings.PolishEnabled)
            polish = ConfirmKey("Polish the handoff with the configured model? [y/N]") == ConfirmResult.Yes;

        var r = services.Transfer.Transfer(source, cur.Info, target, polish, FolderPrompt(console, services.ConsoleEnvironment));
        DiscardTypedAhead();
        if (r.HandoffPath.Length > 0) _status.Add($"Handoff: {Esc(r.HandoffPath)}");
        if (r.BackupPath is not null) _status.Add($"Previous handoff backed up to: {Esc(r.BackupPath)}");
        if (r.Note is not null) _status.Add(r.Launched ? $"[yellow]{Esc(r.Note)}[/]" : $"[red]{Esc(r.Note)}[/]");
        Reload();
    }

    private const int MaxListedItems = 20;

    private void Cleanup(CleanupAction action)
    {
        var targets = _model.Targets;
        if (targets.Count == 0) { _status.Add("Nothing selected."); return; }

        var plan = services.Cleanup.Plan(targets, action);
        var verb = action == CleanupAction.Archive ? "archive" : "delete";
        var warnedAll = plan.Count(p => p.Warning is not null);
        var declinedAll = plan.Count(p => p.Declined is not null);
        console.MarkupLine($"Plan: {verb} {plan.Count} session(s) ({warnedAll} with warnings, {declinedAll} declined).");
        // Very large plans are summarised so the confirmation prompt below stays on screen.
        foreach (var p in plan.Take(MaxListedItems))
        {
            var i = p.Session.Info;
            console.MarkupLine($"  {Esc(i.Agent)}:{Esc(i.Id)}  {Esc(p.Session.DisplayTitle)}  ({Size(i.SizeBytes)})");
            foreach (var path in p.Paths.Take(3)) console.MarkupLine($"      {Esc(path)}");
            if (p.Paths.Count > 3) console.MarkupLine($"      ... and {p.Paths.Count - 3} more path(s)");
            if (p.Warning is not null) console.MarkupLine($"      [yellow]Warning:[/] {Esc(p.Warning)}");
            if (p.Declined is not null) console.MarkupLine($"      [red]Declined:[/] {Esc(p.Declined)}");
        }
        if (plan.Count > MaxListedItems) console.MarkupLine($"  ... and {plan.Count - MaxListedItems} more");

        var actionable = plan.Where(p => p.Declined is null).ToList();
        if (actionable.Count == 0)
        {
            _status.Add("Nothing to do: every selected session was declined (see the plan above).");
            return;
        }

        var what = action == CleanupAction.Delete ? "PERMANENTLY delete" : "archive";
        var warned = actionable.Count(p => p.Warning is not null);
        var question = $"About to {what} {actionable.Count} session(s)"
            + (warned > 0 ? $", {warned} with active-session warnings" : "") + ". Proceed? [y/N] (y confirms; any other key cancels)";

        // The only route to Apply: an explicit "y" pressed after the plan was shown. Anything else is No.
        switch (ConfirmKey(question))
        {
            case ConfirmResult.Yes:
                break;
            case ConfirmResult.Refused:
                _status.Add("[red]Refused: no confirmation could be read, so nothing was changed.[/]");
                return;
            default:
                _status.Add("Cancelled; nothing was changed.");
                return;
        }

        try
        {
            var result = (ApplyOverride ?? services.Cleanup.Apply)(actionable);
            foreach (var d in result.Done.Take(MaxListedItems))
                _status.Add($"[green]Done[/] {Esc(d.Session.Info.Agent)}:{Esc(d.Session.Info.Id)}");
            if (result.Done.Count > MaxListedItems) _status.Add($"... and {result.Done.Count - MaxListedItems} more done");
            foreach (var f in result.Failed.Take(MaxListedItems))
                _status.Add($"[red]Failed[/] {Esc(f.Item.Session.Info.Agent)}:{Esc(f.Item.Session.Info.Id)}: {Esc(f.Error)}");
            if (result.Failed.Count > MaxListedItems) _status.Add($"[red]... and {result.Failed.Count - MaxListedItems} more failed[/]");
            _status.Add($"{result.Done.Count} done, {result.Failed.Count} failed, {plan.Count - actionable.Count} declined.");
        }
        catch (Exception ex)
        {
            _status.Add($"[red]Cleanup failed unexpectedly: {Esc(ex.Message)}. Some sessions may or may not have been changed; the list is being reloaded.[/]");
        }
        finally
        {
            _model.ClearMarks();
            Reload();
        }
    }

    /// <summary>
    /// Confirmation by a single key read through the key source. Pending (typed-ahead) keys are discarded AFTER the
    /// plan is on screen, so only a key pressed in answer to the question can confirm. Only 'y' means yes.
    /// </summary>
    private ConfirmResult ConfirmKey(string question)
    {
        if (!Interactive) return ConfirmResult.Refused;
        try { keys.DiscardPending(); } catch (Exception) { return ConfirmResult.Refused; }
        console.MarkupLine(Markup.Escape(question));
        try
        {
            var k = keys.ReadKey();
            return k.KeyChar is 'y' or 'Y' ? ConfirmResult.Yes : ConfirmResult.No;
        }
        catch (Exception) { return ConfirmResult.Refused; }
    }

    private void Pause()
    {
        try
        {
            keys.DiscardPending();
            console.MarkupLine("[grey]Press any key to continue...[/]");
            keys.ReadKey();
        }
        catch (Exception) { /* the main loop's next read reports an unreadable keyboard */ }
    }

    private void Report(bool ok, string? message)
    {
        if (message is not null) _status.Add(ok ? $"[yellow]{Esc(message)}[/]" : $"[red]{Esc(message)}[/]");
    }
}
