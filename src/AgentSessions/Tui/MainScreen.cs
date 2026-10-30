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

    public int Run()
    {
        if (!IsInteractive(console))
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
            catch (Exception ex) { _status.Add($"[red]Display error: {Esc(ex.Message)}[/]"); }

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
        }
    }

    /// <summary>Returns false to quit.</summary>
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
            case ConsoleKey.Escape: return false;
        }

        switch (char.ToLowerInvariant(key.KeyChar))
        {
            case 'q': return false;
            case ' ': _model.ToggleMark(); break;
            case 's': _model.SelectStale(); break;
            case '/': AskFilter(); break;
            case 'r': Reload(); break;
            case 't': Transfer(); break;
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

    private void Render()
    {
        console.Clear();
        var rows = _model.Rows;
        var now = services.Now();

        console.MarkupLine($"[bold]agentsessions[/]   filter: {Esc(DescribeFilter(_model.Filter))}"
            + $"   {rows.Count} session(s), {_model.MarkedKeys.Count} marked, {_model.StaleCount} stale");

        var table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn("").AddColumn("").AddColumn("AGENT").AddColumn("PROJECT").AddColumn("LAST ACTIVE").AddColumn("SIZE").AddColumn("TITLE");
        table.AddRow(Head(""), Head(""), Head("AGENT"), Head("PROJECT"), Head("LAST ACTIVE"), Head("SIZE"), Head("TITLE"));

        var first = Math.Clamp(_model.Cursor - VisibleRows / 2, 0, Math.Max(0, rows.Count - VisibleRows));
        for (var i = first; i < Math.Min(rows.Count, first + VisibleRows); i++)
        {
            var r = rows[i];
            var current = i == _model.Cursor;
            var title = Esc(r.Info.Title) + (r.IsStale ? " " + Esc("[stale]") : "");
            string[] cells =
            [
                current ? ">" : " ",
                _model.MarkedKeys.Contains(MainScreenModel.KeyOf(r)) ? "*" : " ",
                Esc(r.Info.Agent), Esc(ProjectName(r.Info.ProjectPath)), Age(now, r.Info.LastActivity),
                Size(r.Info.SizeBytes), title
            ];
            table.AddRow(current ? cells.Select(c => $"[bold]{c}[/]").ToArray() : cells);
        }
        console.Write(table);
        if (rows.Count == 0) console.MarkupLine("  (no sessions)");
        else if (rows.Count > VisibleRows) console.MarkupLine($"  ({_model.Cursor + 1}/{rows.Count})");

        console.MarkupLine(Esc("[Enter] resume  [t] transfer to…  [a] archive  [d] delete"));
        console.MarkupLine(Esc("[/] filter  [s] select stale  [space] mark  [r] reload  [q] quit"));
        foreach (var line in _status) console.MarkupLine(line);

        static string Head(string s) => $"[underline]{s}[/]";
    }

    private static string DescribeFilter(SessionFilter f) =>
        $"{(f.Agent is null ? "all agents" : "agent " + f.Agent)} · "
        + $"{(f.ProjectContains is null ? "all projects" : "project " + f.ProjectContains)} · "
        + $"{(f.OlderThan is { } o ? $"older than {(int)o.TotalDays}d" : "any age")}";

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

        if (!FilterParser.TryParse(answer, out var filter, out var error))
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
        var result = services.Launcher.Resume(adapter, cur.Info, FolderPrompt(console));
        Report(result.Launched, result.Message);
        Reload();
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
            polish = Confirm(console, "Polish the handoff with the configured model? [y/N]") == ConfirmResult.Yes;

        var r = services.Transfer.Transfer(source, cur.Info, target, polish, FolderPrompt(console));
        if (r.HandoffPath.Length > 0) _status.Add($"Handoff: {Esc(r.HandoffPath)}");
        if (r.BackupPath is not null) _status.Add($"Previous handoff backed up to: {Esc(r.BackupPath)}");
        if (r.Note is not null) _status.Add(r.Launched ? $"[yellow]{Esc(r.Note)}[/]" : $"[red]{Esc(r.Note)}[/]");
        Reload();
    }

    private void Cleanup(CleanupAction action)
    {
        var targets = _model.Targets;
        if (targets.Count == 0) { _status.Add("Nothing selected."); return; }

        var plan = services.Cleanup.Plan(targets, action);
        var verb = action == CleanupAction.Archive ? "archive" : "delete";
        console.MarkupLine($"Plan: {verb} {plan.Count} session(s).");
        foreach (var p in plan)
        {
            var i = p.Session.Info;
            console.MarkupLine($"  {Esc(i.Agent)}:{Esc(i.Id)}  {Esc(i.Title)}  ({Size(i.SizeBytes)})");
            foreach (var path in p.Paths) console.MarkupLine($"      {Esc(path)}");
            if (p.Warning is not null) console.MarkupLine($"      [yellow]Warning:[/] {Esc(p.Warning)}");
            if (p.Declined is not null) console.MarkupLine($"      [red]Declined:[/] {Esc(p.Declined)}");
        }

        var actionable = plan.Where(p => p.Declined is null).ToList();
        if (actionable.Count == 0)
        {
            _status.Add("Nothing to do: every selected session was declined (see the plan above).");
            return;
        }

        var what = action == CleanupAction.Delete ? "PERMANENTLY delete" : "archive";
        var warned = actionable.Count(p => p.Warning is not null);
        var question = $"About to {what} {actionable.Count} session(s)"
            + (warned > 0 ? $", {warned} with active-session warnings" : "") + ". Proceed? [y/N]";

        // The only route to Apply: an explicit "yes" at a real prompt. Anything else is No.
        switch (Confirm(console, question))
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

        var result = services.Cleanup.Apply(actionable);
        foreach (var d in result.Done)
            _status.Add($"[green]Done[/] {Esc(d.Session.Info.Agent)}:{Esc(d.Session.Info.Id)}");
        foreach (var f in result.Failed)
            _status.Add($"[red]Failed[/] {Esc(f.Item.Session.Info.Agent)}:{Esc(f.Item.Session.Info.Id)}: {Esc(f.Error)}");
        _status.Add($"{result.Done.Count} done, {result.Failed.Count} failed, {plan.Count - actionable.Count} declined.");
        _model.ClearMarks();
        Reload();
    }

    private void Report(bool ok, string? message)
    {
        if (message is not null) _status.Add(ok ? $"[yellow]{Esc(message)}[/]" : $"[red]{Esc(message)}[/]");
    }
}
