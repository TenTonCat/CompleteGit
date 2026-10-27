using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CompleteGit.Shell;

public enum OutputKind { Command, Stdout, Stderr, Info, Error, Summary, Clear, Query, QuerySummary, QueryStderr }

public sealed record CommandResult(int ExitCode, string Stdout, string Stderr, bool Cancelled)
{
    public bool Ok => ExitCode == 0 && !Cancelled;
    public static CommandResult Failed(string message) => new(-1, "", message, false);
}

/// <summary>
/// Single funnel for every process the app launches. Commands run one at a time so the
/// console transcript never interleaves, and every command line plus its output is
/// broadcast through <see cref="Output"/>.
/// </summary>
public sealed class CommandRunner
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _current;
    private int _running;

    public string GitExe { get; set; } = "git";
    /// <summary>pwsh | powershell | cmd</summary>
    public string Shell { get; set; } = "pwsh";
    public string ConsoleDirectory { get; set; } = Environment.CurrentDirectory;

    public event Action<OutputKind, string>? Output;
    public event Action<bool>? BusyChanged;
    public event Action<string>? ConsoleDirectoryChanged;

    public bool IsBusy => Volatile.Read(ref _running) > 0;

    public void Emit(OutputKind kind, string text) => Output?.Invoke(kind, text);

    /// <summary>Runs git in the repository directory. Quiet commands only summarise their stdout in the console.</summary>
    public Task<CommandResult> GitAsync(string repoDir, string args, bool quiet = false, string? stdin = null, CancellationToken ct = default)
    {
        var env = new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_PAGER"] = "cat",
            ["LC_ALL"] = "C.UTF-8",
        };
        if (quiet) env["GIT_OPTIONAL_LOCKS"] = "0";
        return RunAsync(GitExe, args, repoDir, $"git {args}", quiet, stdin, env, ct);
    }

    /// <summary>Runs a line the user typed in the console through the configured shell.</summary>
    public async Task<CommandResult> UserCommandAsync(string line, CancellationToken ct = default)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return new CommandResult(0, "", "", false);

        if (trimmed.Equals("clear", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("cls", StringComparison.OrdinalIgnoreCase))
        {
            Emit(OutputKind.Clear, "");
            return new CommandResult(0, "", "", false);
        }

        var cd = Regex.Match(trimmed, @"^(?:cd|chdir|sl|set-location)(?:\s+(.*))?$", RegexOptions.IgnoreCase);
        if (cd.Success)
        {
            Emit(OutputKind.Command, $"{ConsoleDirectory}> {trimmed}");
            var target = cd.Groups[1].Value.Trim().Trim('"', '\'');
            if (target.Length == 0)
            {
                Emit(OutputKind.Stdout, ConsoleDirectory);
                return new CommandResult(0, ConsoleDirectory, "", false);
            }
            if (target == "~") target = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var full = Path.GetFullPath(Path.Combine(ConsoleDirectory, target));
            if (!Directory.Exists(full))
            {
                Emit(OutputKind.Error, $"Directory not found: {full}");
                return CommandResult.Failed("Directory not found");
            }
            ConsoleDirectory = full;
            ConsoleDirectoryChanged?.Invoke(full);
            return new CommandResult(0, "", "", false);
        }

        string file, args;
        switch (Shell.ToLowerInvariant())
        {
            case "cmd":
                file = "cmd.exe";
                args = $"/d /s /c \"chcp 65001>nul & {trimmed}\"";
                break;
            default:
                file = Shell.Equals("powershell", StringComparison.OrdinalIgnoreCase) ? "powershell.exe" : "pwsh.exe";
                // Errors are re-emitted as plain text on stderr; otherwise PowerShell serialises them as CLIXML when stderr is redirected.
                var script = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $OutputEncoding=[System.Text.Encoding]::UTF8\n"
                           + "if ($PSVersionTable.PSVersion.Major -ge 7) { $PSStyle.OutputRendering = 'PlainText' }\n"
                           + "$global:LASTEXITCODE = 0\n"
                           + "& {\n" + trimmed + "\n} 2>&1 | ForEach-Object { if ($_ -is [System.Management.Automation.ErrorRecord]) { [Console]::Error.WriteLine($_.ToString()) } else { $_ } } | Out-String -Stream -Width 4000 | ForEach-Object { $_.TrimEnd() }\n"
                           + "if ($LASTEXITCODE) { exit $LASTEXITCODE }\n"
                           + "if ($Error.Count -gt 0) { exit 1 }";
                var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                args = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {b64}";
                break;
        }

        var env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" };
        return await RunAsync(file, args, ConsoleDirectory, $"{ConsoleDirectory}> {trimmed}", false, null, env, ct);
    }

    public void Cancel()
    {
        try { _current?.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    private async Task<CommandResult> RunAsync(string file, string args, string cwd, string echo, bool quiet, string? stdin,
        Dictionary<string, string>? env, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        if (Interlocked.Increment(ref _running) == 1) BusyChanged?.Invoke(true);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        int stdoutLines = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            Emit(quiet ? OutputKind.Query : OutputKind.Command, echo);
            if (stdin != null)
            {
                var preview = stdin.Split('\n').Take(4).Select(l => "  | " + l.TrimEnd('\r'));
                foreach (var l in preview) Emit(OutputKind.Info, l);
                var total = stdin.Count(c => c == '\n');
                if (total > 4) Emit(OutputKind.Info, $"  | … ({total + 1} lines on stdin)");
            }

            var psi = new ProcessStartInfo(file, args)
            {
                WorkingDirectory = cwd,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (stdout) { stdout.Append(e.Data).Append('\n'); stdoutLines++; }
                if (!quiet) Emit(OutputKind.Stdout, e.Data);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (stderr) stderr.Append(e.Data).Append('\n');
                Emit(quiet ? OutputKind.QueryStderr : OutputKind.Stderr, e.Data);
            };

            try { p.Start(); }
            catch (Exception ex)
            {
                Emit(OutputKind.Error, $"Failed to start '{file}': {ex.Message}");
                return CommandResult.Failed(ex.Message);
            }
            _current = p;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try
            {
                if (stdin != null)
                {
                    var writer = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false));
                    await writer.WriteAsync(stdin).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (IOException) { /* process exited before reading stdin */ }
            try { p.StandardInput.Close(); } catch { }

            bool cancelled = false;
            try { await p.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                cancelled = true;
                try { p.Kill(entireProcessTree: true); } catch { }
            }
            p.WaitForExit(); // flushes the async readers
            _current = null;
            sw.Stop();

            int code = p.ExitCode;
            string so, se;
            lock (stdout) so = stdout.ToString();
            lock (stderr) se = stderr.ToString();

            if (cancelled) Emit(OutputKind.Error, $"  ✖ cancelled after {sw.Elapsed.TotalSeconds:0.00}s");
            else if (quiet) Emit(OutputKind.QuerySummary, $"  … {stdoutLines} line{(stdoutLines == 1 ? "" : "s")}, exit {code}, {sw.Elapsed.TotalSeconds:0.00}s");
            else if (code != 0) Emit(OutputKind.Error, $"  ✖ exit code {code} ({sw.Elapsed.TotalSeconds:0.00}s)");
            else Emit(OutputKind.Summary, $"  ✔ {sw.Elapsed.TotalSeconds:0.00}s");

            return new CommandResult(code, so, se, cancelled);
        }
        finally
        {
            if (Interlocked.Decrement(ref _running) == 0) BusyChanged?.Invoke(false);
            _gate.Release();
        }
    }
}
