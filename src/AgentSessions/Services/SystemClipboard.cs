using System.Diagnostics;
using System.Text;

namespace AgentSessions.Services;

public sealed class SystemClipboard : IClipboard
{
    public void Set(string text)
    {
        if (OperatingSystem.IsWindows())
        {
            // clip.exe reads UTF-16LE when a BOM is present, so non-ASCII survives.
            var enc = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
            Pipe("clip.exe", [], [.. enc.GetPreamble(), .. enc.GetBytes(text)]);
            return;
        }

        var utf8 = new UTF8Encoding(false).GetBytes(text);
        if (OperatingSystem.IsMacOS())
        {
            Pipe("pbcopy", [], utf8);
            return;
        }

        foreach (var (tool, args) in new (string, string[])[] { ("xclip", ["-selection", "clipboard"]), ("wl-copy", []) })
        {
            try { Pipe(tool, args, utf8); return; }
            catch (Exception) { /* not installed / no display / failed; try next */ }
        }
        throw new InvalidOperationException("No clipboard tool available (install xclip or wl-clipboard).");
    }

    private static void Pipe(string tool, string[] args, byte[] data)
    {
        var psi = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {tool}."); }
        catch (System.ComponentModel.Win32Exception) when (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            throw new InvalidOperationException($"Clipboard tool '{tool}' is not available.");
        }

        using (p)
        {
            p.StandardInput.BaseStream.Write(data);
            p.StandardInput.Close();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException($"{tool} failed with exit code {p.ExitCode}.");
        }
    }
}
