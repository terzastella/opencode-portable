// Opencode Portable V2 — Windows launcher: folder picker, embedded-payload
// first-run extraction (fully offline, no runtime downloads), portable env
// (XDG redirect + isolated tmp + host guards), run opencode with
// CWD = selected workspace, full cleanup on exit.
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace OpencodePortable;

internal static class Program
{

    [STAThread]
    private static int Main(string[] args)
    {
        // Silence Windows Error Reporting for OUR crashes (including the
        // supervised picker-child AVs): they die with their exit code and never
        // land in %ProgramData%\...\WER\ReportArchive. Our own crash-*.log
        // mechanism keeps diagnostics. Tradeoff, deliberate, for zero traces.
        try { SetErrorMode(SEM_NOGPFAULTERRORBOX | SEM_FAILCRITICALERRORS); } catch { }
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            // Fatal guard: on double-click the console closes instantly, so show a
            // native message box (P/Invoke) AND write a crash log with full stack —
            // never die silently, always diagnosable in one shot.
            string stack = ex.ToString();
            string[] lines = stack.Split('\n');
            string shortStack = string.Join("\n", lines[..Math.Min(8, lines.Length)]);
            string msg = $"[opencode-portable] ERRORE FATALE: {ex.GetType().Name}: {ex.Message}\n\n{shortStack}";
            Console.Error.WriteLine(msg);
            try
            {
                // Prefer the workspace-local state dir; host TEMP only if we crash
                // before any workspace is known. Never next to the exe.
                // NOTE: forwarded opencode args are NEVER logged (they may carry
                // secrets); only the launcher-owned flag count.
                int ownedCount = args.Count(a => a.StartsWith("--"));
                string logDir = s_crashDir ?? Path.Combine(Path.GetTempPath(), "opencode-portable");
                Directory.CreateDirectory(logDir);
                // Privacy: scrub the username from paths; keep only the last 5 logs.
                string profile = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
                if (!string.IsNullOrEmpty(profile)) stack = stack.Replace(profile, "~", StringComparison.OrdinalIgnoreCase);
                File.WriteAllText(
                    Path.Combine(logDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log"),
                    $"[{DateTime.Now:O}] {stack}\nLauncherFlags: {ownedCount}\n");
                foreach (string old in Directory.GetFiles(logDir, "crash-*.log")
                    .OrderByDescending(f => f).Skip(5))
                {
                    try { File.Delete(old); } catch { }
                }
            }
            catch { }
            try { MessageBoxW(IntPtr.Zero, msg, "Opencode Portable", 0x10); }
            catch { }
            return 1;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOGPFAULTERRORBOX = 0x0010;

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();

    internal static bool WerSilenced() =>
        (GetErrorMode() & (SEM_NOGPFAULTERRORBOX | SEM_FAILCRITICALERRORS))
        == (SEM_NOGPFAULTERRORBOX | SEM_FAILCRITICALERRORS);

    /// <summary>
    /// Crash-log directory, set once the workspace is known. The exe itself is
    /// stateless: nothing is ever written next to it.
    /// </summary>
    private static string? s_crashDir;

    private enum PickOutcome { Ok, Cancel, Fallback }

    /// <summary>
    /// Child mode entry: runs the native dialog in THIS process and reports via
    /// stdout protocol. A native AV here kills only the child (see parent).
    /// </summary>
    private static int RunPickerChild(string title, string initial)
    {
        try
        {
            string? picked = NativeFolderDialog.PickFolder(title, initial);
            if (picked is null) { Console.WriteLine("PICK-CANCEL"); return 1; }
            Console.WriteLine("PICK-OK:" + picked);
            return 0;
        }
        catch (InvalidOperationException ex) // traced COM failure
        {
            Console.WriteLine("PICK-ERROR:" + ex.Message.Replace("\n", " | "));
            return 2;
        }
    }

    /// <summary>
    /// Parent supervision: runs the picker in a CHILD process (self exe, or the
    /// --picker-exe <path> override for tests). A native AV in the dialog kills
    /// only the child (exit 0xC0000005) and the parent falls back gracefully —
    /// an in-process AV would be uncatchable by any try/catch.
    /// </summary>
    private static (PickOutcome, string?) RunPickerSupervised(string title, string initial, string? pickerExeOverride)
    {
        string childExe = string.IsNullOrEmpty(pickerExeOverride)
            ? (Environment.ProcessPath ?? "")
            : pickerExeOverride;
        if (string.IsNullOrEmpty(childExe))
        {
            Console.Error.WriteLine("[opencode-portable] impossibile localizzare l'eseguibile picker.");
            return (PickOutcome.Fallback, null);
        }
        var psi = new ProcessStartInfo
        {
            FileName = childExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Child writes UTF-8 (see RunPickerChild): decode accordingly, or
            // non-ASCII workspace paths (es. "Caffè") arrive corrupted.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("--pick-folder");
        psi.ArgumentList.Add(title);
        psi.ArgumentList.Add(initial);
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return (PickOutcome.Fallback, null);
            // Async pipe drains BEFORE wait: a chatty child must never block on
            // full stdout/stderr buffers while we block on its exit (deadlock).
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            proc.WaitForExit(); // modal user dialog: wait indefinitely
            string stdout = outTask.GetAwaiter().GetResult();
            string stderr = errTask.GetAwaiter().GetResult();
            foreach (string line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                Console.Error.WriteLine("[picker] " + line);
            if (proc.ExitCode == unchecked((int)0xC0000005))
            {
                Console.Error.WriteLine("[opencode-portable] il dialog nativo è crashato (AV); passo al fallback.");
                return (PickOutcome.Fallback, null);
            }
            foreach (string line in stdout.Split('\n', StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("PICK-OK:", StringComparison.Ordinal))
                    return (PickOutcome.Ok, line["PICK-OK:".Length..]);
                if (line == "PICK-CANCEL") return (PickOutcome.Cancel, null);
                if (line.StartsWith("PICK-ERROR:", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("[opencode-portable] dialog fallito: " + line);
                    return (PickOutcome.Fallback, null);
                }
            }
            Console.Error.WriteLine($"[opencode-portable] picker terminato senza risultato (exit={proc.ExitCode}); passo al fallback.");
            return (PickOutcome.Fallback, null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[opencode-portable] impossibile avviare il picker ({ex.Message}); passo al fallback.");
            return (PickOutcome.Fallback, null);
        }
    }

    /// <summary>
    /// Last-resort folder input without any shell API: the user drags a folder
    /// from Explorer into this console and presses Enter. Three attempts.
    /// Redirected stdin is decoded as UTF-8 (PowerShell pipes UTF-8); interactive
    /// console input keeps the default console encoding.
    /// </summary>
    private static string? ConsoleFolderFallback()
    {
        for (int i = 1; i <= 3; i++)
        {
            Console.WriteLine($"[opencode-portable] trascina la cartella di lavoro in questa finestra e premi Invio (tentativo {i}/3):");
            string? line = Console.IsInputRedirected ? ReadRedirectedLine() : Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) continue;
            // PowerShell drag-drop pastes `& 'C:\path'` (or `&"C:\path"`): strip
            // the invocation operator and both quote styles.
            string path = line.Trim();
            if (path.StartsWith("&")) path = path[1..].Trim();
            path = path.Trim('"').Trim('\'').Trim();
            if (Directory.Exists(path)) return path;
            Console.Error.WriteLine($"[opencode-portable] cartella inesistente: {path}");
        }
        Console.Error.WriteLine("[opencode-portable] ERRORE: nessuna cartella valida fornita.");
        return null;
    }

    private static string? ReadRedirectedLine()
    {
        using var ms = new MemoryStream();
        // Do NOT dispose the shared stdin handle: attempts 2-3 need it too.
        var stdin = Console.OpenStandardInput();
        int b;
        while ((b = stdin.ReadByte()) != -1)
        {
            if (b == '\n') break;
            if (b != '\r') ms.WriteByte((byte)b);
        }
        if (ms.Length == 0) return null;
        byte[] raw = ms.ToArray();
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return Console.InputEncoding.GetString(raw);
        }
    }

    /// <summary>
    /// Shell dialogs may return extended-length paths (\\?\...). Normalize to a
    /// regular usable path: \\?\UNC\server\share -&gt; \\server\share, \\?\C:\...
    /// -&gt; C:\... (only when it stays a valid existing directory).
    /// </summary>
    private static string NormalizeLongPath(string path)
    {
        const string unc = @"\\?\UNC\";
        const string prefix = @"\\?\";
        if (path.StartsWith(unc, StringComparison.Ordinal))
        {
            string normal = @"\\" + path[unc.Length..];
            if (Directory.Exists(normal)) return normal;
        }
        else if (path.StartsWith(prefix, StringComparison.Ordinal))
        {
            string normal = path[prefix.Length..];
            if (Directory.Exists(normal)) return normal;
        }
        return path;
    }

    private static int Run(string[] args)
    {
        // The exe is stateless: NOTHING is ever written next to it. Everything
        // (config, cache, state, tmp, binary) lives inside the selected workspace
        // under .opencode-portable, so deleting the workspace removes all traces.
        // Never TrimEnd a drive root ("C:\" must stay "C:\", not become "C:").
        string baseDir = AppContext.BaseDirectory;
        string root = baseDir.Length > 3
            ? baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : baseDir;

        // Launcher-owned args (stripped, never forwarded to opencode).
        bool allowPath = Environment.GetEnvironmentVariable("OPENCODE_ALLOW_PATH_FALLBACK") == "1";
        bool forceConsole = false;
        string? pickerExe = null;
        string? workspace = null;
        var fwd = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--from-path") { allowPath = true; continue; }
            if (args[i] == "--console") { forceConsole = true; continue; }
            if (args[i] == "--picker-exe") { if (i + 1 >= args.Length) { Console.Error.WriteLine("[opencode-portable] ERRORE: --picker-exe richiede un percorso."); return 1; } pickerExe = args[++i]; continue; }
            if (args[i].StartsWith("--picker-exe=")) { pickerExe = args[i]["--picker-exe=".Length..]; continue; }
            if (args[i] == "--workspace") { if (i + 1 >= args.Length) { Console.Error.WriteLine("[opencode-portable] ERRORE: --workspace richiede una cartella."); return 1; } workspace = args[++i]; continue; }
            if (args[i].StartsWith("--workspace=")) { workspace = args[i]["--workspace=".Length..]; continue; }
            if (args[i] == "--self-test")
            {
                if (NativeFolderDialog.SelfTest(out string detail))
                {
                    Console.WriteLine($"[opencode-portable] self-test: {detail}");
                    if (!WerSilenced())
                    {
                        Console.Error.WriteLine("[opencode-portable] self-test FALLITO: WER non silenziato (SetErrorMode).");
                        return 1;
                    }
                    Console.WriteLine("[opencode-portable] self-test: WER silenziato OK");
                    return 0;
                }
                Console.Error.WriteLine($"[opencode-portable] self-test FALLITO: {detail}");
                return 1;
            }
            if (args[i] == "--test-fallback")
            {
                // Exercises ConsoleFolderFallback with piped stdin (CI-friendly).
                string? tp = ConsoleFolderFallback();
                if (tp is null) return 1;
                Console.WriteLine($"[opencode-portable] fallback OK: {tp}");
                return 0;
            }
            if (args[i] == "--test-close")
            {
                // Exercises the native close-handler path (kill tree + trash +
                // delete) without touching any real console window.
                return PortableRun.TestCloseHandler();
            }
            if (args[i] == "--watch")
            {
                // Watchdog mode: wait for the given pid, then delete the dir.
                // Never runs opencode, never touches the workspace otherwise.
                if (args.Length > i + 3 && int.TryParse(args[i + 1], out int wpid)
                    && long.TryParse(args[i + 2], out long wticks))
                {
                    string wdir = args[i + 3];
                    return PortableRun.WatchdogRoot(wpid, wticks, wdir);
                }
                Console.Error.WriteLine("[opencode-portable] ERRORE: uso: --watch <pid> <startTicks> <dir>");
                return 1;
            }
            if (args[i] == "--test-robust-delete")
            {
                return PortableRun.TestRobustDelete();
            }
            if (args[i] == "--test-watchdog")
            {
                return PortableRun.TestWatchdog();
            }
            if (args[i] == "--test-watchdog-proc")
            {
                return PortableRun.TestWatchdogProc();
            }
            if (args[i] == "--test-watchdog-hop")
            {
                return PortableRun.TestWatchdogHop();
            }
            if (args[i] == "--test-watchdog-copy")
            {
                return PortableRun.TestWatchdogCopy();
            }
            if (args[i] == "--watch-hop")
            {
                // Hop mode: spawn the detached --watch grandchild from this copy,
                // then exit immediately (orphaning it). Never watches directly.
                if (args.Length > i + 3)
                {
                    string hp = args[i + 1], ht = args[i + 2], hd = args[i + 3];
                    return PortableRun.WatchHop(hp, ht, hd);
                }
                Console.Error.WriteLine("[opencode-portable] ERRORE: uso: --watch-hop <pid> <startTicks> <dir>");
                return 1;
            }
            if (args[i] == "--report-console")
            {
                // Reports this process' console window handle to a file.
                // Detached processes have none (0).
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("[opencode-portable] ERRORE: --report-console richiede un file di output.");
                    return 1;
                }
                string rf = args[++i];
                try { File.WriteAllText(rf, PortableRun.ConsoleWindowHandle().ToString()); }
                catch (Exception ex)
                {
                    try { File.WriteAllText(rf, "EX:" + ex.GetType().Name); } catch { }
                }
                return 0;
            }
            if (args[i] == "--test-detached")
            {
                return PortableRun.TestDetached();
            }
            if (args[i] == "--test-junction")
            {
                return PortableRun.TestJunction();
            }
            if (args[i] == "--test-midrun-delete")
            {
                return PortableRun.TestMidrunDelete();
            }
            if (args[i] == "--has-payload")
            {
                // Prints embedded payload info for build verification (used by
                // publish-fat instead of size heuristics). Exit 0 iff present.
                const string resName = "OpencodePortable.payload.opencode.zip";
                using Stream? res = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName);
                if (res is null)
                {
                    Console.Error.WriteLine("[opencode-portable] ERRORE: nessun payload incorporato.");
                    return 1;
                }
                Console.WriteLine($"[opencode-portable] payload OK: {resName} ({res.Length} bytes)");
                return 0;
            }
            if (args[i] == "--clean")
            {
                // Sweep stale roots on demand (e.g. after a Task Manager kill),
                // without launching anything.
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("[opencode-portable] ERRORE: --clean richiede una cartella.");
                    return 1;
                }
                string target = args[++i];
                if (!Directory.Exists(target))
                {
                    Console.Error.WriteLine($"[opencode-portable] ERRORE: cartella inesistente: {target}");
                    return 1;
                }
                int n = SweepStalePortableRoots(target);
                Console.WriteLine($"[opencode-portable] rimosse {n} root stale in {target}");
                return 0;
            }
            if (args[i] == "--clean-host")
            {
                // Remove OUR host-side traces (WER archives from supervised child
                // crashes, watchdog telemetry leftovers, TEMP crash fallback dir).
                // Only files carrying our name; anything else is never touched.
                // May need elevation for ReportArchive: skips with a note then.
                return CleanHostTraces();
            }
            if (args[i] == "--pick-folder")
            {
                // Child mode: show the native dialog, print the protocol, exit.
                // Never runs opencode. Stdout protocol: PICK-OK:<path> (0),
                // PICK-CANCEL (1), PICK-ERROR:<trace> (2).
                // Only consume following args that are not flags (a title never
                // starts with "--"; this keeps `-- --pick-folder` forwarding safe).
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                string pTitle = "Seleziona cartella";
                string pInitial = "";
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) pTitle = args[++i];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) pInitial = args[++i];
                return RunPickerChild(pTitle, pInitial);
            }
            if (args[i] == "--")
            {
                // End of launcher options: forward everything after verbatim,
                // even if it looks like a launcher flag.
                for (int j = i + 1; j < args.Length; j++) fwd.Add(args[j]);
                break;
            }
            fwd.Add(args[i]);
        }

        if (workspace is null)
        {
            // Stateless: always start from Documents, no memory file anywhere.
            string initial = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            string? picked = null;
            if (!forceConsole)
            {
            var (outcome, childPath) = RunPickerSupervised(
                "Seleziona la cartella dove lavorare con Opencode Portable", initial, pickerExe);
                if (outcome == PickOutcome.Cancel)
                    return 0; // user cancelled: nothing launched, nothing to clean
                if (outcome == PickOutcome.Ok && !string.IsNullOrEmpty(childPath))
                    picked = childPath;
            }
            if (picked is null)
            {
                // --console, or child crashed / errored / empty: drag-and-drop.
                picked = ConsoleFolderFallback();
                if (picked is null) return 1;
            }
            workspace = NormalizeLongPath(picked);
        }

        string workFull;
        try { workFull = Path.GetFullPath(NormalizeLongPath(workspace)); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[opencode-portable] ERRORE: percorso non valido '{workspace}': {ex.Message}");
            return 1;
        }
        if (!Directory.Exists(workFull))
        {
            Console.Error.WriteLine($"[opencode-portable] ERRORE: cartella inesistente: {workFull}");
            return 1;
        }
        if (Path.GetPathRoot(workFull) == workFull)
        {
            // A drive root as workspace would scatter the per-run root at top
            // level and sweep the whole drive: refuse explicitly.
            Console.Error.WriteLine($"[opencode-portable] ERRORE: la workspace non può essere la radice di un drive: {workFull}");
            return 1;
        }

        // Portable root: per-run, INSIDE the workspace. Deleting it on exit
        // leaves zero traces; concurrent runs get separate roots (no sharing,
        // no locks, no double-delete hazards).
        string stamp = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Random.Shared.Next(100000)}";
        string data = Path.Combine(workFull, $".opencode-portable-{stamp}");
        foreach (string d in new[] { "config", "data", "cache", "state", "tmp", "bin" })
            Directory.CreateDirectory(Path.Combine(data, d));
        s_crashDir = Path.Combine(data, "state");
        SweepStalePortableRoots(workFull);

        // First run: local config from the template next to the exe (read-only),
        // or from the embedded default when distributing the exe alone.
        string cfg = Path.Combine(data, "config", "opencode.json");
        if (!File.Exists(cfg))
        {
            string cfgExample = Path.Combine(root, "config", "opencode.example.json");
            if (File.Exists(cfgExample))
                File.Copy(cfgExample, cfg);
            else
                File.WriteAllText(cfg, DefaultConfig);
        }

        string? bin = allowPath ? FindBundled(root, data) : EnsureBinary(root, data);
        if (bin is null && !allowPath) { CleanupRoot(data); return 1; }
        string? resolved = ResolveBinary(root, bin, allowPath);
        if (resolved is null) { CleanupRoot(data); return 1; }

        return PortableRun.Execute(data, cfg, workFull, resolved, fwd);
    }

    /// <summary>
    /// Removes a just-created per-run root on early-error paths (missing
    /// binary etc.): Execute's own cleanup never runs there, so do it here.
    /// </summary>
    private static void CleanupRoot(string data)
    {
        try { if (Directory.Exists(data)) Directory.Delete(data, recursive: true); }
        catch { /* startup sweeper covers leftovers */ }
    }

    /// <summary>
    /// Sweeps crash/kill leftovers. STRICT policy (shared with the watchdog):
    /// only directories whose name matches our exact per-run stamp AND whose
    /// PID is dead. Live processes are never touched, and user directories
    /// that merely share the prefix are never matched.
    /// </summary>
    private static int SweepStalePortableRoots(string workspace)
    {
        int removed = 0;
        string[] dirs;
        try { dirs = Directory.GetDirectories(workspace, ".opencode-portable-*"); }
        catch { return 0; }
        foreach (string dir in dirs)
        {
            try
            {
                string name = Path.GetFileName(dir);
                if (!PortableRun.IsPortableRootName(name)) continue;
                int pid = int.Parse(name.Split('-')[4]);
                if (IsPidAlive(pid)) continue;
                PortableRun.RobustDelete(dir, 3000);
                if (!Directory.Exists(dir)) removed++;
            }
            catch { }
        }
        return removed;
    }

    private static bool IsPidAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    /// <summary>
    /// Removes our host-side traces: WER ReportArchive/ReportQueue entries from
    /// supervised child crashes, watchdog telemetry leftovers, TEMP crash dir.
    /// Only paths carrying our names; reparse-safe (a planted link is removed
    /// itself, never followed); returns 0 even if some need elevation.
    /// </summary>
    private static int CleanHostTraces()
    {
        int removed = 0, skipped = 0;
        var candidates = new List<string>();
        string werBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Windows", "WER");
        foreach (string sub in new[] { "ReportArchive", "ReportQueue" })
        {
            string dir = Path.Combine(werBase, sub);
            string[] hits;
            try { hits = Directory.GetDirectories(dir, "*OpencodePortable*"); }
            catch { skipped++; continue; }
            candidates.AddRange(hits);
        }
        string tmp = Path.GetTempPath();
        try
        {
            foreach (string f in Directory.GetFiles(tmp, "opencode-portable-watchdog-*.log"))
                candidates.Add(f);
        }
        catch { skipped++; }
        try
        {
            int n = PortableRun.SweepWatchdogCopies();
            if (n > 0) Console.WriteLine($"[clean-host] rimosse {n} copie guardiano stale.");
        }
        catch { skipped++; }
        try
        {
            string crashDir = Path.Combine(tmp, "opencode-portable");
            if (Directory.Exists(crashDir)) candidates.Add(crashDir);
        }
        catch { skipped++; }
        foreach (string p in candidates)
        {
            try
            {
                // Reparse-safe: links removed themselves, never followed.
                if (PortableRun.IsReparsePoint(p)) { PortableRun.DeleteTreeNoFollow(p); }
                else if (Directory.Exists(p)) PortableRun.DeleteTreeNoFollow(p);
                else if (File.Exists(p)) File.Delete(p);
                else continue;
                removed++;
                Console.WriteLine("[clean-host] rimosso: " + p);
            }
            catch (Exception ex)
            {
                skipped++;
                Console.Error.WriteLine("[clean-host] saltato (serve admin?): " + p + " (" + ex.GetType().Name + ")");
            }
        }
        Console.WriteLine($"[clean-host] rimossi={removed} saltati={skipped}");
        return 0;
    }

    /// <summary>Default config when no template file ships next to the exe.</summary>
    private const string DefaultConfig =
        "{\n  \"$schema\": \"https://opencode.ai/config.json\",\n  \"autoupdate\": false,\n  \"share\": \"disabled\"\n}\n";

    private static string? FindBundled(string root, string data)
    {
        string bundled = Path.Combine(data, "bin", "opencode.exe");
        if (File.Exists(bundled)) return bundled;
        string repoBin = Path.Combine(root, "bin", "opencode.exe");
        if (File.Exists(repoBin)) return repoBin;
        return null;
    }

    private static string? EnsureBinary(string root, string data)
    {
        string? found = FindBundled(root, data);
        if (found is not null) return found;
        // First run: extract the payload embedded at BUILD time (no network, ever).
        // The expected SHA-256 travels in a second embedded resource: verify it
        // BEFORE extracting anything (supply-chain pin without freezing versions,
        // publish-fat resolves latest at build).
        const string resName = "OpencodePortable.payload.opencode.zip";
        const string hashName = "OpencodePortable.payload.opencode.zip.sha256";
        using Stream? res = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName);
        if (res is null)
        {
            Console.Error.WriteLine("[opencode-portable] ERRORE: payload opencode non incorporato in questo exe.");
            Console.Error.WriteLine("[opencode-portable] Ricompila con scripts\\publish-fat.ps1 (i download a runtime sono disabilitati).");
            return null;
        }
        if (res.Length <= 0 || res.Length > 500L * 1024 * 1024)
        {
            Console.Error.WriteLine("[opencode-portable] ERRORE: payload di dimensione sospetta, annullo.");
            return null;
        }
        string expected;
        using (Stream? hs = Assembly.GetExecutingAssembly().GetManifestResourceStream(hashName))
        {
            if (hs is null)
            {
                Console.Error.WriteLine("[opencode-portable] ERRORE: hash atteso mancante, annullo (build incompleta).");
                return null;
            }
            using var sr = new StreamReader(hs);
            expected = (sr.ReadToEnd() ?? "").Trim().ToLowerInvariant();
        }
        string actual;
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            actual = Convert.ToHexString(sha.ComputeHash(res)).ToLowerInvariant();
        }
        if (actual != expected || expected.Length != 64)
        {
            Console.Error.WriteLine("[opencode-portable] ERRORE: hash payload non corrispondente, annullo (possibile manomissione).");
            return null;
        }
        try { res.Position = 0; }
        catch
        {
            Console.Error.WriteLine("[opencode-portable] ERRORE: payload non rileggibile, annullo.");
            return null;
        }
        Directory.CreateDirectory(Path.Combine(data, "bin"));
        // Stage inside the workspace tmp (NOT host TEMP): a kill mid-extract
        // leaves the zip where the sweeper/root-delete already covers.
        string tmpZip = Path.Combine(data, "tmp", $"opencode-portable-{Guid.NewGuid():N}.zip");
        try
        {
            Console.WriteLine("[opencode-portable] primo avvio: estraggo opencode incorporato...");
            using (var fs = File.Create(tmpZip)) res.CopyTo(fs);
            using var zip = ZipFile.OpenRead(tmpZip);
            var entries = zip.Entries;
            if (entries.Count > 50 || entries.Sum(e => e.Length) > 1024L * 1024 * 1024)
            {
                Console.Error.WriteLine("[opencode-portable] ERRORE: archivio sospetto (troppe entry o troppo grande), annullo.");
                return null;
            }
            var entry = entries.FirstOrDefault(e =>
                e.Name.Equals("opencode.exe", StringComparison.OrdinalIgnoreCase))
                ?? entries.FirstOrDefault(e =>
                    e.Name.StartsWith("opencode", StringComparison.OrdinalIgnoreCase) &&
                    e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                Console.Error.WriteLine("[opencode-portable] ERRORE: nessun opencode.exe nel payload incorporato.");
                return null;
            }
            entry.ExtractToFile(Path.Combine(data, "bin", "opencode.exe"), overwrite: true);
            Console.WriteLine($"[opencode-portable] OK -> {Path.Combine(data, "bin", "opencode.exe")}");
            return Path.Combine(data, "bin", "opencode.exe");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[opencode-portable] ERRORE estrazione payload: {ex.Message}");
            return null;
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
        }
    }

    private static string? ResolveBinary(string root, string? bin, bool allowPath)
    {
        if (bin is not null && File.Exists(bin)) return bin;
        // Fail-closed PATH fallback: explicit opt-in only, resolved path always shown.
        if (!allowPath)
        {
            Console.Error.WriteLine($"[opencode-portable] ERRORE: '{bin}' non trovato.");
            Console.Error.WriteLine("[opencode-portable] Riusa opencode da PATH con --from-path come primo argomento o OPENCODE_ALLOW_PATH_FALLBACK=1.");
            return null;
        }
        string? found = FindOnPath("opencode.exe");
        if (found is null)
        {
            Console.Error.WriteLine("[opencode-portable] ERRORE: nessun opencode in PATH.");
            return null;
        }
        Console.Error.WriteLine($"[opencode-portable] ATTENZIONE: uso opencode da PATH: {found}");
        return found;
    }

    private static string? FindOnPath(string file)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null) return null;
        foreach (string dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                string cand = Path.Combine(dir.Trim().Trim('"'), file);
                if (File.Exists(cand)) return cand;
            }
            catch { }
        }
        return null;
    }
}
