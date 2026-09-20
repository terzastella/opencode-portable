// Portable environment + guarded execution: XDG redirect into the per-run
// workspace root, host pollution guards (delete-only-what-this-run-created),
// full root deletion on exit (full privacy), native console-close handling
// (X / Alt+F4 skips managed finally: CTRL_CLOSE_EVENT needs SetConsoleCtrlHandler),
// exit code propagation. Child env is set via ProcessStartInfo (no shell pollution).
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OpencodePortable;

internal static class PortableRun
{
    private delegate bool ConsoleCtrlDelegate(uint ctrlType);
    private static readonly ConsoleCtrlDelegate s_ctrlHandler = OnConsoleClose;

    /// <summary>
    /// Strict name check shared by the sweeper and the watchdog: only our exact
    /// per-run stamp (optionally +-trash). User directories that merely share
    /// the prefix are never matched.
    /// </summary>
    internal static readonly System.Text.RegularExpressions.Regex PortableRootName =
        new(@"^\.opencode-portable-\d{8}-\d{6}-\d+-\d+(-trash)?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static bool IsPortableRootName(string name) => PortableRootName.IsMatch(name);

    // The watchdog ignores console events so a window close never kills it
    // before it has done its job (it only waits on a process handle).
    private static readonly ConsoleCtrlDelegate s_ignoreHandler = _ => true;

    // Shared with the close handler (other thread): read via local snapshot,
    // never twice (a second read could see a disposed object).
    private static volatile Process? s_child;
    private static string? s_dataRoot;

    private const uint CTRL_CLOSE_EVENT = 2;
    private const uint CTRL_LOGOFF_EVENT = 5;
    private const uint CTRL_SHUTDOWN_EVENT = 6;

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add);

    /// <summary>
    /// Native close handler: the OS kills the process WITHOUT running managed
    /// finally blocks, within a grace period (~5s, often less on close). Order
    /// matters: RENAME first (atomic — the workspace looks clean instantly even
    /// if we die mid-cleanup), then kill the child tree briefly (it holds file
    /// locks), then delete the trash with the remaining time. The startup
    /// sweeper collects any remainder next run. Returns true = handled.
    /// </summary>
    private static bool OnConsoleClose(uint ctrlType)
    {
        if (ctrlType != CTRL_CLOSE_EVENT && ctrlType != CTRL_LOGOFF_EVENT && ctrlType != CTRL_SHUTDOWN_EVENT)
            return false; // Ctrl+C / Ctrl+Break keep default behavior (finally already covers them)
        try
        {
            string? root = s_dataRoot;
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
            {
                string trash = root + "-trash";
                try
                {
                    if (Directory.Exists(trash) || File.Exists(trash)) DeleteTreeNoFollow(trash);
                    Directory.Move(root, trash);
                    root = trash;
                }
                catch { /* delete in place below */ }
                try
                {
                    var child = s_child;
                    if (child is not null)
                    {
                        try
                        {
                            if (!child.HasExited)
                            {
                                try { child.Kill(entireProcessTree: true); }
                                catch { try { child.Kill(); } catch { } }
                            }
                            // Poll for actual death (up to ~1.5s): deleting while the
                            // image is still mapped always fails on Windows.
                            for (int i = 0; i < 15; i++)
                            {
                                try { if (child.HasExited) break; } catch { break; }
                                Thread.Sleep(100);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
                // Close grace is short (~5s): spend at most ~3s here, the startup
                // sweeper collects any remainder next run.
                var left = RobustDelete(root, 3000);
                if (left.Count > 0)
                    Console.Error.WriteLine("[opencode-portable] ATTENZIONE: resti non cancellati (verranno spazzati al prossimo avvio): " + string.Join(", ", left));
            }
            else
            {
                // No root (yet): still try to stop the child so it can't repopulate.
                try
                {
                    var child = s_child;
                    if (child is not null && !child.HasExited)
                    {
                        try { child.Kill(entireProcessTree: true); }
                        catch { try { child.Kill(); } catch { } }
                    }
                }
                catch { }
            }
        }
        catch { }
        return true;
    }
    public static int Execute(string data, string cfg, string workspace,
        string binary, List<string> fwdArgs)
    {
        // Watchdog: a renamed copy of this exe watching OUR pid. Whatever kills
        // us (exit, crash, X-close, Task Manager single or group) wakes it and
        // it deletes the root, then itself. Spawned before anything else so even
        // early crashes are covered. Deliberately NOT tracked as s_child (the
        // close handler must not kill it). Stale copies swept too.
        SpawnWatchdog(data);
        try
        {
            int n = SweepWatchdogCopies();
            if (n > 0) Console.Error.WriteLine($"[opencode-portable] rimosse {n} copie guardiano stale.");
        }
        catch { }
        // The root itself is per-run (see Program): tmp needs no extra level,
        // TMPDIR points straight at it.
        string tmpRoot = Path.Combine(data, "tmp");
        Directory.CreateDirectory(tmpRoot);
        string runTmp = tmpRoot;

        // Snapshot of host locations BEFORE redirect (guard). Covers XDG-style
        // fallbacks too (USERPROFILE/.config, .local/share) in case something
        // ignores the redirected env vars. runStart narrows the delete race:
        // only paths created after run start are ever removed.
        DateTime runStart = DateTime.Now;
        var sysTargets = new List<(string Path, bool Existed)>();
        string? userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        var bases = new List<string?>();
        bases.Add(Environment.GetEnvironmentVariable("TEMP"));
        bases.Add(Environment.GetEnvironmentVariable("LOCALAPPDATA"));
        bases.Add(Environment.GetEnvironmentVariable("APPDATA"));
        if (!string.IsNullOrEmpty(userProfile))
        {
            bases.Add(Path.Combine(userProfile, ".config"));
            bases.Add(Path.Combine(userProfile, ".local", "share"));
        }
        foreach (string? baseDir in bases)
        {
            if (string.IsNullOrEmpty(baseDir)) continue;
            string p = baseDir.EndsWith("opencode", StringComparison.OrdinalIgnoreCase)
                ? baseDir
                : Path.Combine(baseDir, "opencode");
            sysTargets.Add((p, Directory.Exists(p) || File.Exists(p)));
        }

        int code;
        s_dataRoot = data;
        try { SetConsoleCtrlHandler(s_ctrlHandler, add: true); } catch { }
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = binary,
                WorkingDirectory = workspace,
                UseShellExecute = false,
            };
            foreach (string a in fwdArgs) psi.ArgumentList.Add(a);
            psi.Environment["XDG_CONFIG_HOME"] = Path.Combine(data, "config");
            psi.Environment["XDG_DATA_HOME"] = Path.Combine(data, "data");
            psi.Environment["XDG_CACHE_HOME"] = Path.Combine(data, "cache");
            psi.Environment["XDG_STATE_HOME"] = Path.Combine(data, "state");
            psi.Environment["OPENCODE_CONFIG"] = cfg;
            psi.Environment["OPENCODE_DISABLE_MODELS_FETCH"] = "1";
            psi.Environment["TMP"] = runTmp;
            psi.Environment["TEMP"] = runTmp;
            psi.Environment["TMPDIR"] = runTmp;
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                Console.Error.WriteLine("[opencode-portable] ERRORE: avvio processo fallito.");
                return 1;
            }
            s_child = proc;
            proc.WaitForExit();
            code = proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[opencode-portable] ERRORE: {ex}");
            return 1;
        }
        finally
        {
            s_child = null;
            try { SetConsoleCtrlHandler(s_ctrlHandler, add: false); } catch { }
            // Full privacy: delete the ENTIRE per-run root, always, on any exit
            // path or code. Retry budget ~10s for draining locks (running image,
            // antivirus). Leftovers are reported, never silent.
            var remaining = RobustDelete(data, 10000);
            if (remaining.Count > 0)
            {
                Console.Error.WriteLine("[opencode-portable] ATTENZIONE: impossibile cancellare tutto, restano (verranno spazzati al prossimo avvio):");
                foreach (string r in remaining.Take(10))
                    Console.Error.WriteLine("  " + r);
            }
            foreach (var (path, existed) in sysTargets)
            {
                try
                {
                    // Delete only paths born during THIS run: must not have existed
                    // at snapshot AND must have been created after run start (a
                    // foreign process could have created the same path mid-run).
                    if (existed || (!Directory.Exists(path) && !File.Exists(path))) continue;
                    DateTime created;
                    try { created = Directory.Exists(path) ? Directory.GetCreationTime(path) : File.GetCreationTime(path); }
                    catch { continue; }
                    if (created < runStart.AddMinutes(-1)) continue;
                    DeletePath(path);
                }
                catch { }
            }
        }
        return code;
    }

    /// <summary>
    /// Host-guard cleanup: never follow reparse points (a planted link must
    /// not turn the guard into deletion elsewhere).
    /// </summary>
    private static void DeletePath(string path)
    {
        try { DeleteTreeNoFollow(path); }
        catch { }
    }

    /// <summary>
    /// Watchdog entry (--watch pid startTicks dir): waits for the parent
    /// process to die FOR ANY REASON (exit, crash, X-close, Task Manager kill)
    /// then deletes the per-run root. Survives console close via the ignore
    /// handler. PID reuse is defeated by matching process start time.
    /// </summary>
    public static int WatchdogRoot(int parentPid, long parentStartTicks, string dir)
    {
        // Diagnostic telemetry: buffered in memory, written to host TEMP ONLY on
        // failure/abort/leftovers (a silent watchdog is undebuggable, but a
        // successful run must leave zero host traces). Random suffix, capped.
        string telePath = Path.Combine(Path.GetTempPath(),
            $"opencode-portable-watchdog-{parentPid}-{Random.Shared.Next(1000000)}.log");
        var teleBuf = new List<string>();
        void Tele(string msg)
        {
            try
            {
                if (teleBuf.Count < 50)
                    teleBuf.Add($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
            }
            catch { }
        }
        void FlushTele()
        {
            try { File.WriteAllLines(telePath, teleBuf); } catch { }
        }
        try
        {
            try { SetConsoleCtrlHandler(s_ignoreHandler, add: true); } catch { }
            Tele($"start pid={parentPid} ticks={parentStartTicks} dir={dir}");
            // Strict: only delete our own per-run roots. Anything else (or an
            // unverifiable parent identity) aborts instead of deleting blindly.
            if (!IsPortableRootName(Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar))))
            {
                Tele("abort: dir non valida");
                WEcho("[watchdog] ERRORE: dir non valida, niente da cancellare: " + dir);
                FlushTele();
                try { SelfDeleteCopy(); } catch { }
                return 1;
            }
            bool verifiedAlive = false;
            bool verifiedDead = false;
            try
            {
                using var parent = Process.GetProcessById(parentPid);
                if (parentStartTicks != 0 && parent.StartTime.Ticks == parentStartTicks)
                {
                    if (!parent.HasExited)
                    {
                        verifiedAlive = true; // same identity, still running: wait below
                    }
                    else verifiedDead = true;
                }
                // else: PID recycled by another process (ticks differ) -> NOT our
                // parent; fall through to no-delete abort below.
            }
            catch
            {
                verifiedDead = true; // PID unknown to the OS: original is gone
            }
            if (verifiedAlive)
            {
                try
                {
                    // Re-validate identity on the second open: the PID could have
                    // been recycled between the two opens; waiting on (and later
                    // trusting) the wrong process must not happen.
                    using var parent = Process.GetProcessById(parentPid);
                    bool same = false;
                    try { same = parentStartTicks != 0 && parent.StartTime.Ticks == parentStartTicks; }
                    catch { same = false; }
                    if (same && !parent.HasExited)
                        parent.WaitForExit();
                    else if (!same)
                    {
                        Tele("abort: identità cambiata tra i due controlli");
                        WEcho("[watchdog] ERRORE: identità padre cambiata, annullo.");
                        FlushTele();
                        try { SelfDeleteCopy(); } catch { }
                        return 1;
                    }
                }
                catch { }
                Tele("parent morto, procedo.");
            }
            else if (!verifiedDead)
            {
                // Identity uncertain (ticks==0 or recycled PID): do NOT delete.
                Tele("abort: identità non verificabile");
                WEcho("[watchdog] ERRORE: identità padre non verificabile, annullo.");
                FlushTele();
                try { SelfDeleteCopy(); } catch { }
                return 1;
            }
            else Tele("parent già morto, procedo.");
            // Belt and braces: terminate anything still executing from inside the
            // root (surviving children hold image locks that defeat deletion).
            // Only processes whose image lives under our unique root: safe by
            // construction. Never touch self. dir is canonicalized once so a
            // trailing slash or \\?\ prefix can't defeat the prefix match.
            int me = Environment.ProcessId;
            string dirCanon;
            try { dirCanon = Path.GetFullPath(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
            catch { dirCanon = dir; }
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    string? img = null;
                    try { img = p.MainModule?.FileName; } catch { }
                    if (string.IsNullOrEmpty(img)) continue;
                    if (p.Id == me || p.Id == parentPid) continue;
                    if (!img.StartsWith(dirCanon + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        Tele($"kill residuo pid={p.Id} img={img}");
                        p.Kill(entireProcessTree: true);
                    }
                    catch { try { p.Kill(); } catch { } }
                    try { p.Dispose(); } catch { }
                }
            }
            catch (Exception ex) { Tele("kill-by-path fallito: " + ex.Message); }
            // Parent verifiably dead: clean with retry budget.
            var left = RobustDelete(dir, 15000);
            if (left.Count > 0)
            {
                Tele("resti: " + string.Join(", ", left.Take(10)));
                WEcho("[watchdog] ATTENZIONE resti: " + string.Join(", ", left.Take(10)));
                FlushTele();
            }
            SelfDeleteCopy(); // copies remove themselves; originals never reach here as copies
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                teleBuf.Add("FATAL: " + ex);
                FlushTele();
            }
            catch { }
            WEcho("[watchdog] ERRORE: " + ex.Message);
            try { SelfDeleteCopy(); } catch { }
            return 1;
        }
    }

    /// <summary>
    /// Test hook (--test-watchdog-proc): the REAL child-process path — spawns
    /// this same exe with --watch against a fake sleeping parent, kills it,
    /// asserts the child cleans the root. Closes the gap left by TestWatchdog
    /// (which runs the logic in-process and never exercises arg parsing,
    /// process spawn, stdio or teardown of a real --watch child).
    /// </summary>
    public static int TestWatchdogProc()
    {
        string baseDir = Path.Combine(Path.GetTempPath(),
            "opencode-testwatchproc-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(baseDir, ".opencode-portable-20200101-000000-99999999-2");
        Process? fake = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(data, "bin"));
            File.WriteAllText(Path.Combine(data, "bin", "big.exe"), new string('z', 1000));
            var psiFake = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c timeout /t 60 /nobreak > NUL",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            fake = Process.Start(psiFake);
            if (fake is null) { Console.Error.WriteLine("[test-watchdog-proc] ERRORE: parent finto non avviato."); return 1; }
            long ticks;
            try { ticks = fake.StartTime.Ticks; } catch { ticks = 0; }
            string self = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(self)) { Console.Error.WriteLine("[test-watchdog-proc] ERRORE: ProcessPath vuoto."); return 1; }
            var psi = new ProcessStartInfo
            {
                FileName = self,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--watch");
            psi.ArgumentList.Add(fake.Id.ToString());
            psi.ArgumentList.Add(ticks.ToString());
            psi.ArgumentList.Add(data);
            using var child = Process.Start(psi);
            if (child is null) { Console.Error.WriteLine("[test-watchdog-proc] ERRORE: figlio non avviato."); return 1; }
            Thread.Sleep(1500); // let the child verify identity and block in wait
            try { fake.Kill(); } catch { } // simulate Task Manager kill
            bool exited = child.WaitForExit(25000);
            string err = "";
            try { err = child.StandardError.ReadToEnd() + child.StandardOutput.ReadToEnd(); } catch { }
            bool gone = !Directory.Exists(data);
            Console.WriteLine($"[test-watchdog-proc] exited={exited} rc={(exited ? child.ExitCode : -1)} gone={gone} out=[{err.Trim()}]");
            bool ok = exited && child.ExitCode == 0 && gone;
            Console.WriteLine(ok ? "[test-watchdog-proc] OK" : "[test-watchdog-proc] FALLITO");
            try { Directory.Delete(baseDir, recursive: true); } catch { }
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[test-watchdog-proc] ERRORE: {ex.Message}");
            return 1;
        }
        finally
        {
            try { fake?.Dispose(); } catch { }
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }
    /// <summary>
    /// Test hook (--test-watchdog-hop): full orphan chain — fake sleeping
    /// parent, SpawnWatchdogCopy (copy + detached hop + orphan grandchild),
    /// kill parent, assert the root is gone AND the copy self-deleted.
    /// </summary>
    public static int TestWatchdogHop()
    {
        string baseDir = Path.Combine(Path.GetTempPath(),
            "opencode-testwatchhop-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(baseDir, ".opencode-portable-20200101-000000-99999999-4");
        Process? fake = null;
        string? copy = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(data, "bin"));
            File.WriteAllText(Path.Combine(data, "bin", "big.exe"), new string('w', 1000));
            var psiFake = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c timeout /t 60 /nobreak > NUL",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            fake = Process.Start(psiFake);
            if (fake is null) { Console.Error.WriteLine("[test-watchdog-hop] ERRORE: parent finto non avviato."); return 1; }
            long ticks;
            try { ticks = fake.StartTime.Ticks; } catch { ticks = 0; }
            copy = SpawnWatchdogCopy(data, fake.Id, ticks);
            if (copy is null) { Console.Error.WriteLine("[test-watchdog-hop] ERRORE: copia/hop non creati."); return 1; }
            Console.WriteLine($"[test-watchdog-hop] copy={Path.GetFileName(copy)}");
            Thread.Sleep(2000); // hop spawns grandchild and exits; grandchild blocks in wait
            try { fake.Kill(); } catch { } // simulate Task Manager kill
            bool gone = false, copyGone = false;
            for (int i = 0; i < 150; i++) // up to ~30s
            {
                Thread.Sleep(200);
                gone = !Directory.Exists(data);
                copyGone = !File.Exists(copy);
                if (gone && copyGone) break;
            }
            Console.WriteLine($"[test-watchdog-hop] gone={gone} copyGone={copyGone}");
            bool ok = gone && copyGone;
            Console.WriteLine(ok ? "[test-watchdog-hop] OK" : "[test-watchdog-hop] FALLITO");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[test-watchdog-hop] ERRORE: {ex.Message}");
            return 1;
        }
        finally
        {
            try { fake?.Dispose(); } catch { }
            try { Directory.Delete(baseDir, recursive: true); } catch { }
            try { if (copy is not null && File.Exists(copy)) File.Delete(copy); } catch { }
        }
    }

    /// <summary>
    /// Test hook (--test-watchdog-copy): full renamed-copy flow — fake sleeping
    /// parent, SpawnWatchdogCopy (real file copy + detached spawn), kill parent,
    /// assert the root is gone AND the copy self-deleted.
    /// </summary>
    public static int TestWatchdogCopy()
    {
        string baseDir = Path.Combine(Path.GetTempPath(),
            "opencode-testwatchcopy-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(baseDir, ".opencode-portable-20200101-000000-99999999-3");
        Process? fake = null;
        string? copy = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(data, "bin"));
            File.WriteAllText(Path.Combine(data, "bin", "big.exe"), new string('q', 1000));
            var psiFake = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c timeout /t 60 /nobreak > NUL",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            fake = Process.Start(psiFake);
            if (fake is null) { Console.Error.WriteLine("[test-watchdog-copy] ERRORE: parent finto non avviato."); return 1; }
            long ticks;
            try { ticks = fake.StartTime.Ticks; } catch { ticks = 0; }
            copy = SpawnWatchdogCopy(data, fake.Id, ticks);
            if (copy is null) { Console.Error.WriteLine("[test-watchdog-copy] ERRORE: copia non creata."); return 1; }
            bool distinctName = !Path.GetFileName(copy).Equals("OpencodePortable.exe", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"[test-watchdog-copy] copy={Path.GetFileName(copy)} distinct={distinctName}");
            Thread.Sleep(1500); // let the copy verify identity and block in wait
            try { fake.Kill(); } catch { } // simulate Task Manager kill
            bool gone = false, copyGone = false;
            for (int i = 0; i < 150; i++) // up to ~30s
            {
                Thread.Sleep(200);
                gone = !Directory.Exists(data);
                copyGone = !File.Exists(copy);
                if (gone && copyGone) break;
            }
            Console.WriteLine($"[test-watchdog-copy] gone={gone} copyGone={copyGone}");
            bool ok = distinctName && gone && copyGone;
            Console.WriteLine(ok ? "[test-watchdog-copy] OK" : "[test-watchdog-copy] FALLITO");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[test-watchdog-copy] ERRORE: {ex.Message}");
            return 1;
        }
        finally
        {
            try { fake?.Dispose(); } catch { }
            try { Directory.Delete(baseDir, recursive: true); } catch { }
            try { if (copy is not null && File.Exists(copy)) File.Delete(copy); } catch { }
        }
    }

    public static int TestWatchdog()
    {
        string baseDir = Path.Combine(Path.GetTempPath(),
            "opencode-testwatch-" + Guid.NewGuid().ToString("N"));
        // Stamp-format name: the strict watchdog validation only accepts those.
        string data = Path.Combine(baseDir, ".opencode-portable-20200101-000000-99999999-1");
        Process? fake = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(data, "bin"));
            File.WriteAllText(Path.Combine(data, "bin", "big.exe"), new string('y', 1000));
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c timeout /t 60 /nobreak > NUL",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            fake = Process.Start(psi);
            if (fake is null) { Console.Error.WriteLine("[test-watchdog] ERRORE: parent finto non avviato."); return 1; }
            long ticks;
            try { ticks = fake.StartTime.Ticks; } catch { ticks = 0; }
            var task = Task.Run(() => WatchdogRoot(fake.Id, ticks, data));
            Thread.Sleep(1000);
            try { fake.Kill(); } catch { } // simulate Task Manager kill
            bool done = task.Wait(20000);
            int rc = done ? task.Result : -1;
            bool gone = !Directory.Exists(data);
            Console.WriteLine($"[test-watchdog] done={done} rc={rc} gone={gone}");
            bool ok = done && rc == 0 && gone;
            Console.WriteLine(ok ? "[test-watchdog] OK" : "[test-watchdog] FALLITO");
            try { Directory.Delete(baseDir, recursive: true); } catch { }
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[test-watchdog] ERRORE: {ex.Message}");
            return 1;
        }
        finally
        {
            try { fake?.Dispose(); } catch { }
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }
    /// <summary>
    /// Test hook (--test-close): simulates a window-close event with a real
    /// sleeping child and a populated root (~200 MB of junk, like a real
    /// extracted payload), invoking the native handler path directly.
    /// Verifies: handler ran, child dead, ORIGINAL name gone (renamed to trash
    /// and/or deleted — the workspace must look clean instantly). No console
    /// window interaction needed.
    /// </summary>
    public static int TestCloseHandler()
    {
        string baseDir = Path.Combine(Path.GetTempPath(),
            "opencode-testclose-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(baseDir, ".opencode-portable-test");
        try
        {
            Directory.CreateDirectory(Path.Combine(data, "tmp"));
            byte[] chunk = new byte[1024 * 1024];
            new Random(42).NextBytes(chunk);
            for (int i = 0; i < 200; i++)
                File.WriteAllBytes(Path.Combine(data, "tmp", $"f{i:D3}.bin"), chunk);
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c timeout /t 30 /nobreak > NUL",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var child = Process.Start(psi);
            if (child is null)
            {
                Console.Error.WriteLine("[test-close] ERRORE: figlio non avviato.");
                return 1;
            }
            s_child = child;
            s_dataRoot = data;
            var sw = Stopwatch.StartNew();
            bool handled = OnConsoleClose(CTRL_CLOSE_EVENT);
            sw.Stop();
            bool childDead;
            try { childDead = child.WaitForExit(5000) || child.HasExited; }
            catch { childDead = true; }
            bool origGone = !Directory.Exists(data);
            string[] leftovers;
            try { leftovers = Directory.GetDirectories(baseDir, ".opencode-portable-test*"); }
            catch { leftovers = Array.Empty<string>(); }
            s_child = null;
            s_dataRoot = null;
            Console.WriteLine($"[test-close] handled={handled} childDead={childDead} origGone={origGone} leftovers={leftovers.Length} ms={sw.ElapsedMilliseconds}");
            if (handled && childDead && origGone)
            {
                Console.WriteLine("[test-close] OK");
                try { Directory.Delete(baseDir, recursive: true); } catch { }
                return 0;
            }
            Console.Error.WriteLine("[test-close] FALLITO");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[test-close] ERRORE: {ex.Message}");
            return 1;
        }
        finally
        {
            s_child = null;
            s_dataRoot = null;
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Best-effort recursive delete with retry budget: locked files (running
    /// image draining, antivirus scans, lingering handles) often free up within
    /// seconds. Reparse-point safe: links are removed themselves, never followed
    /// (a planted junction must not turn cleanup into deletion elsewhere).
    /// Read-only attributes are cleared first (git objects, payload files).
    /// Returns the remaining top-level entries (empty = fully gone).
    /// Never throws.
    /// </summary>
    public static List<string> RobustDelete(string root, int budgetMs)
    {
        var sw = Stopwatch.StartNew();
        while (Directory.Exists(root) || File.Exists(root))
        {
            try
            {
                ClearReadOnly(root);
                DeleteTreeNoFollow(root);
                break;
            }
            catch { /* retry until budget */ }
            if (sw.ElapsedMilliseconds >= budgetMs) break;
            Thread.Sleep(400);
        }
        var left = new List<string>();
        try
        {
            if (Directory.Exists(root))
            {
                foreach (string e in Directory.GetFileSystemEntries(root))
                    left.Add(e);
            }
            else if (File.Exists(root)) left.Add(root);
        }
        catch { }
        return left;
    }

    internal static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    /// <summary>
    /// Deletes one entry WITHOUT following reparse points: links are removed
    /// themselves, directories are recursed safely. Throws on locked files
    /// (the retry loop above handles that).
    /// </summary>
    internal static void DeleteTreeNoFollow(string path)
    {
        if (IsReparsePoint(path))
        {
            // Link itself only — never the target.
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, false);
                else File.Delete(path);
            }
            catch { File.Delete(path); }
            return;
        }
        if (Directory.Exists(path))
        {
            foreach (string e in Directory.GetFileSystemEntries(path))
                DeleteTreeNoFollow(e);
            Directory.Delete(path, false);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void ClearReadOnly(string root)
    {
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 };
            foreach (string e in Directory.GetFileSystemEntries(root, "*", opts))
            {
                try
                {
                    var a = File.GetAttributes(e);
                    if ((a & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(e, a & ~FileAttributes.ReadOnly);
                }
                catch { }
            }
            try
            {
                var a = File.GetAttributes(root);
                if ((a & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(root, a & ~FileAttributes.ReadOnly);
            }
            catch { }
        }
        catch { }
    }

    /// <summary>
    /// Test hook (--test-robust-delete): holds a file with an exclusive lock
    /// for ~2s while RobustDelete runs with budget on another thread. Verifies
    /// the delete waits out the lock instead of failing silently.
    /// </summary>
    public static int TestRobustDelete()
    {
        string baseDir = Path.Combine(Path.GetTempPath(),
            "opencode-testrobust-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(baseDir, ".opencode-portable-test");
        FileStream? lockStream = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            File.WriteAllText(Path.Combine(root, "bin", "big.exe"), new string('x', 1000));
            lockStream = new FileStream(Path.Combine(root, "bin", "big.exe"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var sw = Stopwatch.StartNew();
            var task = Task.Run(() => RobustDelete(root, 10000));
            Thread.Sleep(2000);
            lockStream.Dispose();
            lockStream = null;
            task.Wait();
            sw.Stop();
            bool gone = !Directory.Exists(root);
            Console.WriteLine($"[test-robust-delete] gone={gone} waited={sw.ElapsedMilliseconds}ms leftovers={task.Result.Count}");
            if (gone && sw.ElapsedMilliseconds >= 1500)
            {
                Console.WriteLine("[test-robust-delete] OK");
                try { Directory.Delete(baseDir, recursive: true); } catch { }
                return 0;
            }
            Console.Error.WriteLine("[test-robust-delete] FALLITO");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[test-robust-delete] ERRORE: {ex.Message}");
            return 1;
        }
        finally
        {
            try { lockStream?.Dispose(); } catch { }
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Spawns the watchdog as a RENAMED COPY of this exe, detached from this
    /// console. Rationale: Task Manager groups (and group-kills) by image/console,
    /// so a watchdog named OpencodePortable.exe dies with the app group no matter
    /// how it is spawned. A copy named opwatch-&lt;pid&gt;-&lt;rand&gt;.exe is a
    /// different group for sure. After cleaning, the copy self-deletes; stale
    /// copies (dead owner PID) are swept at startup. Fallback chain on failure:
    /// detached same-name -&gt; attached same-name (current degraded coverage).
    /// </summary>
    private static void SpawnWatchdog(string data)
    {
        try
        {
            string self = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(self)) return;
            long ticks = 0;
            try { using var me = Process.GetCurrentProcess(); ticks = me.StartTime.Ticks; } catch { }
            string? copy = SpawnWatchdogCopy(data, Environment.ProcessId, ticks);
            if (copy is not null) return;
            string cmd = EscapeArg(self) + " --watch " + Environment.ProcessId + " " + ticks + " " + EscapeArg(data);
            if (TryCreateDetached(self, cmd)) return;
            // Fallback: attached spawn (covers single kills, not group kills).
            Console.Error.WriteLine("[opencode-portable] ATTENZIONE: spawn staccato fallito, guardiano collegato (copertura ridotta).");
            var psi = new ProcessStartInfo
            {
                FileName = self,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--watch");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add(ticks.ToString());
            psi.ArgumentList.Add(data);
            using var w = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[opencode-portable] ATTENZIONE: guardiano non avviato ({ex.Message}).");
        }
    }

    /// <summary>
    /// Spawns this same exe detached with the given args. Shared by the
    /// watchdog hop, the watchdog itself and the detachment test.
    /// </summary>
    internal static bool SpawnSelfDetached(params string[] hopArgs)
    {
        try
        {
            string self = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(self)) return false;
            var sb = new StringBuilder();
            sb.Append(EscapeArg(self));
            foreach (string a in hopArgs) sb.Append(' ').Append(EscapeArg(a));
            return TryCreateDetached(self, sb.ToString());
        }
        catch { return false; }
    }

    /// <summary>
    /// Hop mode body (--watch-hop pid ticks dir): spawn the real detached
    /// --watch grandchild from THIS copy, then exit immediately. The
    /// grandchild's parent is dead on arrival -> orphan, outside any tree
    /// Task Manager can kill starting from our processes.
    /// </summary>
    internal static int WatchHop(string pid, string ticks, string dir)
    {
        try
        {
            return SpawnSelfDetached("--watch", pid, ticks, dir) ? 0 : 1;
        }
        catch { return 1; }
    }

    /// <summary>
    /// Copies this exe to %TEMP%\opwatch-&lt;parentPid&gt;-&lt;rand&gt;.exe and starts
    /// it detached with --watch-hop (double hop -> orphan grandchild does the
    /// watching). Returns the copy path, or null on any failure.
    /// </summary>
    internal static string? SpawnWatchdogCopy(string data, int parentPid, long parentTicks)
    {
        string copy = Path.Combine(Path.GetTempPath(),
            $"opwatch-{parentPid}-{Random.Shared.Next(1000000)}.exe");
        try
        {
            string self = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(self)) return null;
            File.Copy(self, copy, overwrite: false);
            string cmd = EscapeArg(copy) + " --watch-hop " + parentPid + " " + parentTicks + " " + EscapeArg(data);
            if (!TryCreateDetached(copy, cmd))
            {
                try { File.Delete(copy); } catch { }
                return null;
            }
            return copy;
        }
        catch
        {
            try { if (File.Exists(copy)) File.Delete(copy); } catch { }
            return null;
        }
    }

    /// <summary>
    /// Sweeps stale watchdog copies in TEMP (dead owner PID). Live owners keep
    /// their copy: a running watchdog's image is locked anyway.
    /// </summary>
    internal static int SweepWatchdogCopies()
    {
        int removed = 0;
        string tmp;
        try { tmp = Path.GetTempPath(); } catch { return 0; }
        string[] files;
        try { files = Directory.GetFiles(tmp, "opwatch-*.exe"); }
        catch { return 0; }
        foreach (string f in files)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(f); // opwatch-<pid>-<rand>
                string[] parts = name.Split('-');
                if (parts.Length != 3 || parts[0] != "opwatch") continue;
                if (!int.TryParse(parts[1], out int pid)) continue;
                bool alive = true;
                try { using var p = Process.GetProcessById(pid); alive = !p.HasExited; }
                catch { alive = false; }
                if (alive) continue;
                File.Delete(f);
                removed++;
            }
            catch { }
        }
        return removed;
    }

    /// <summary>
    /// Escapes one argv element per CommandLineToArgvW rules (backslash runs
    /// doubled, embedded quotes escaped). Windows paths can never contain a
    /// literal quote, but belt and braces for anything we pass to CreateProcess.
    /// </summary>
    internal static string EscapeArg(string arg)
    {
        if (arg.Length == 0) return "\"\"";
        bool needQuotes = false;
        foreach (char c in arg)
        {
            if (char.IsWhiteSpace(c) || c == '"') { needQuotes = true; break; }
        }
        if (!needQuotes) return arg;
        var sb = new StringBuilder();
        sb.Append('"');
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(c);
            }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Self-deletes the running copy (only when this image IS an opwatch copy):
    /// a running exe cannot delete itself, so a short-lived shell removes it
    /// after we exit. Absolute system powershell (no PATH lookup, no hijack);
    /// Start-Sleep (NOT cmd timeout, which fails instantly without a console).
    /// Path in single quotes with doubling (no $ expansion, no wildcards).
    /// </summary>
    internal static void SelfDeleteCopy()
    {
        try
        {
            string self = Environment.ProcessPath ?? "";
            if (!Path.GetFileName(self).StartsWith("opwatch-", StringComparison.OrdinalIgnoreCase))
                return;
            string shell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(shell)) return; // no shell, no self-delete (sweeper covers)
            string quoted = "'" + self.Replace("'", "''") + "'";
            var psi = new ProcessStartInfo
            {
                FileName = shell,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("Start-Sleep -Seconds 2; Remove-Item -LiteralPath " + quoted + " -Force");
            using var c = Process.Start(psi);
        }
        catch { }
    }

    private const uint DETACHED_PROCESS = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? app, StringBuilder cmd,
        IntPtr pattrs, IntPtr tattrs, bool inherit, uint flags, IntPtr env,
        string? dir, ref StartupInfo si, out ProcessInfo pi);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    internal static IntPtr ConsoleWindowHandle(out string diag)
    {
        diag = "";
        try { return GetConsoleWindow(); }
        catch (Exception ex) { diag = ex.GetType().Name + ": " + ex.Message; return new IntPtr(-1); }
    }

    internal static IntPtr ConsoleWindowHandle()
    {
        try { return GetConsoleWindow(); }
        catch { return new IntPtr(-1); }
    }

    /// <summary>Console write that never throws (detached processes have no console).</summary>
    private static void WEcho(string m) { try { Console.Error.WriteLine(m); } catch { } }

    private static bool TryCreateDetached(string exe, string cmdLine)
    {
        var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        try
        {
            bool ok = CreateProcessW(null, new StringBuilder(cmdLine),
                IntPtr.Zero, IntPtr.Zero, false, DETACHED_PROCESS,
                IntPtr.Zero, null, ref si, out ProcessInfo pi);
            if (!ok) return false;
            try { if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread); } catch { }
            try { if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess); } catch { }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Test hook (--test-detached): spawns a detached reporter like the watchdog
    /// spawn and asserts it has no console window (0x0) — proving it lives
    /// outside the app group Task Manager kills as one.
    /// </summary>
    public static int TestDetached()
    {
        string outFile = Path.Combine(Path.GetTempPath(),
            "opencode-testdetached-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            string self = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(self))
            {
                Console.Error.WriteLine("[test-detached] ERRORE: ProcessPath vuoto.");
                return 1;
            }
            if (!TryCreateDetached(self, EscapeArg(self) + " --report-console " + EscapeArg(outFile)))
            {
                Console.Error.WriteLine("[test-detached] ERRORE: spawn staccato fallito.");
                return 1;
            }
            for (int i = 0; i < 50; i++)
            {
                Thread.Sleep(200);
                if (File.Exists(outFile)) break;
            }
            string content = File.Exists(outFile) ? File.ReadAllText(outFile).Trim() : "<missing>";
            string parentHwnd;
            try { parentHwnd = ConsoleWindowHandle().ToString(); }
            catch { parentHwnd = "<err>"; }
            Console.WriteLine($"[test-detached] console={content} (parent console={parentHwnd} for context)");
            bool ok = content == "0";
            Console.WriteLine(ok ? "[test-detached] OK" : "[test-detached] FALLITO");
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[test-detached] ERRORE: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Test hook (--test-junction): plants a directory JUNCTION (no privilege
    /// needed, unlike symlinks) inside a fake per-run root pointing at a
    /// "victim" dir with a sentinel file, then runs the real cleanup.
    /// Asserts: the link is gone AND the victim (sentinel) is intact — cleanup
    /// must never follow links into user data.
    /// </summary>
    public static int TestJunction()
    {
        string baseDir = Path.Combine(Path.GetTempPath(),
            "opencode-testjunction-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(baseDir, ".opencode-portable-20200101-000000-99999999-5");
        string victim = Path.Combine(baseDir, "victim-outside");
        try
        {
            Directory.CreateDirectory(Path.Combine(data, "tmp"));
            Directory.CreateDirectory(victim);
            File.WriteAllText(Path.Combine(victim, "sentinel.txt"), "do-not-touch");
            var mk = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c mklink /J \"" + Path.Combine(data, "tmp", "evil-link") + "\" \"" + victim + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(mk)) p?.WaitForExit(10000);
            if (!IsReparsePoint(Path.Combine(data, "tmp", "evil-link")))
            {
                Console.Error.WriteLine("[test-junction] ERRORE: junction non creata (mklink fallito).");
                return 1;
            }
            var left = RobustDelete(data, 10000);
            bool linkGone = !Directory.Exists(Path.Combine(data, "tmp", "evil-link")) && !Directory.Exists(data);
            bool victimOk = File.Exists(Path.Combine(victim, "sentinel.txt"));
            Console.WriteLine($"[test-junction] linkGone={linkGone} victimOk={victimOk} leftovers={left.Count}");
            bool ok = linkGone && victimOk;
            Console.WriteLine(ok ? "[test-junction] OK" : "[test-junction] FALLITO");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[test-junction] ERRORE: {ex.Message}");
            return 1;
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Test hook (--test-midrun-delete): starts a real Execute() run with a
    /// sleeping child, deletes the per-run root from another thread mid-run,
    /// and asserts Execute finishes gracefully (exit code set, no hang, no
    /// unhandled exception) with user files intact.
    /// </summary>
    public static int TestMidrunDelete()
    {
        string baseDir = Path.Combine(Path.GetTempPath(),
            "opencode-testmidrun-" + Guid.NewGuid().ToString("N"));
        string ws = Path.Combine(baseDir, "ws");
        try
        {
            Directory.CreateDirectory(ws);
            File.WriteAllText(Path.Combine(ws, "mieifile.txt"), "x");
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId + "-424242";
            string data = Path.Combine(ws, ".opencode-portable-" + stamp);
            foreach (string d in new[] { "config", "data", "cache", "state", "tmp", "bin" })
                Directory.CreateDirectory(Path.Combine(data, d));
            string cfg = Path.Combine(data, "config", "opencode.json");
            File.WriteAllText(cfg, "{}");
            var task = Task.Run(() => Execute(data, cfg, ws, "cmd.exe",
                new List<string> { "/c", "timeout", "/t", "30", "/nobreak" }));
            Thread.Sleep(2000); // let the run start the child inside its root
            try { Directory.Delete(data, recursive: true); } catch { }
            bool done = task.Wait(60000);
            int rc = done ? task.Result : -1;
            bool userOk = File.Exists(Path.Combine(ws, "mieifile.txt"));
            Console.WriteLine($"[test-midrun-delete] done={done} rc={rc} userOk={userOk}");
            bool ok = done && userOk;
            Console.WriteLine(ok ? "[test-midrun-delete] OK" : "[test-midrun-delete] FALLITO");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[test-midrun-delete] ERRORE: {ex.Message}");
            return 1;
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }
}
