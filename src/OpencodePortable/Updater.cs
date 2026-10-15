// Self-updater for OpencodePortable.exe: checks the portable's own GitHub
// releases for a newer build, downloads + verifies + stages it.
// Design constraints (see README trust model):
// - Explicit only: --check-update / --update. Normal runs never touch the
//   network (offline-first preserved, fast startup preserved).
// - Trust: HTTPS + .sha256 from the SAME release, fail-closed. Tokens via
//   GITHUB_TOKEN header in-process, never argv, never logged.
// - Zero traces: download temp lives in a unique host-TEMP dir deleted via
//   RobustDelete (leftovers reported + swept next run). Success writes
//   nothing to host except the new exe itself (the update).
// - Windows lock: a running exe cannot replace itself, so --update stages
//   OpencodePortable.new.exe next to the current exe and spawns a detached
//   system powershell helper that moves it over after exit.
// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace OpencodePortable;

internal static class Updater
{
    internal const string Repo = "terzastella/opencode-portable";
    private const string ApiLatest = "https://api.github.com/repos/terzastella/opencode-portable/releases/latest";
    private const string UserAgent = "OpencodePortable-updater";

    internal static string CurrentVersion()
    {
        try
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v is not null) return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { }
        return "0.0.0-unknown";
    }

    internal static bool IsNewer(string current, string latest)
    {
        if (!TryParseVer(current, out var c)) return false;
        if (!TryParseVer(latest, out var l)) return false;
        return l > c;
    }

    internal static bool TryParseVer(string s, out Version v)
    {
        v = new Version(0, 0, 0);
        try
        {
            s = (s ?? "").Trim().TrimStart('v', 'V').Trim();
            // Accept x.y.z plus optional suffix after - or + (e.g. 1.4.0-beta).
            int cut = s.IndexOfAny(new[] { '-', '+' });
            string core = cut >= 0 ? s[..cut] : s;
            var parts = core.Split('.');
            if (parts.Length < 2 || parts.Length > 4) return false;
            foreach (string p in parts)
                if (!int.TryParse(p, out _) || p.Length == 0) return false;
            // Normalize to 3 components for comparison. Fixed count on purpose:
            // the old `while (parts.Length < 3)` never re-split parts and hung
            // forever on 2-component input (e.g. tag "v1.4").
            if (parts.Length == 2) core += ".0";
            v = new Version(core);
            return true;
        }
        catch { return false; }
    }

    private static HttpClient MakeClient(bool withAuth = true)
    {
        var h = new HttpClientHandler { AllowAutoRedirect = true };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        // Bearer token (rate limits) only where needed: the API call. Download
        // URLs redirect to CDN hosts — HttpClient forwards Authorization
        // across redirect hosts, so downloads use an unauthenticated client.
        if (withAuth)
        {
            string? tok = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrEmpty(tok))
                c.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tok);
        }
        return c;
    }

    internal sealed record ReleaseInfo(string Tag, string ExeUrl, string HashUrl);

    internal static async Task<ReleaseInfo?> FetchLatestAsync()
    {
        for (int i = 1; i <= 3; i++)
        {
            try
            {
                using var c = MakeClient();
                using var resp = await c.GetAsync(ApiLatest);
                if (!resp.IsSuccessStatusCode)
                {
                    if (i == 3) return null;
                    await Task.Delay(2000 * i);
                    continue;
                }
                string body = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                string exeUrl = "", hashUrl = "";
                if (doc.RootElement.TryGetProperty("assets", out var assets))
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        string url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                        if (name.Equals("OpencodePortable.exe", StringComparison.OrdinalIgnoreCase)) exeUrl = url;
                        if (name.Equals("OpencodePortable.exe.sha256", StringComparison.OrdinalIgnoreCase)) hashUrl = url;
                    }
                }
                if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(exeUrl)) return null;
                return new ReleaseInfo(tag, exeUrl, hashUrl);
            }
            catch
            {
                if (i == 3) return null;
                await Task.Delay(2000 * i);
            }
        }
        return null;
    }

    public static int CheckUpdate()
    {
        string cur = CurrentVersion();
        Console.WriteLine($"[update] versione corrente: {cur}");
        var rel = FetchLatestAsync().GetAwaiter().GetResult();
        if (rel is null)
        {
            Console.Error.WriteLine("[update] ERRORE: impossibile contattare le release (offline?). Riprova più tardi.");
            return 1;
        }
        string latest = rel.Tag.TrimStart('v', 'V');
        Console.WriteLine($"[update] ultima release: {rel.Tag}");
        if (IsNewer(cur, latest))
        {
            Console.WriteLine($"[update] aggiornamento disponibile: {cur} -> {latest}");
            Console.WriteLine("[update] esegui: OpencodePortable.exe --update");
            return 0;
        }
        Console.WriteLine("[update] sei già aggiornato.");
        return 0;
    }

    public static int Update(bool yes)
    {
        string cur = CurrentVersion();
        var rel = FetchLatestAsync().GetAwaiter().GetResult();
        if (rel is null)
        {
            Console.Error.WriteLine("[update] ERRORE: impossibile contattare le release (offline?).");
            return 1;
        }
        string latest = rel.Tag.TrimStart('v', 'V');
        if (!IsNewer(cur, latest))
        {
            Console.WriteLine($"[update] sei già aggiornato ({cur}).");
            return 0;
        }
        Console.WriteLine($"[update] aggiornamento disponibile: {cur} -> {latest}");
        if (!yes)
        {
            Console.Write("[update] applicare? [y/N] ");
            string? ans = Console.ReadLine();
            if (!string.Equals(ans?.Trim(), "y", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ans?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("[update] annullato.");
                return 0;
            }
        }
        string? self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self))
        {
            Console.Error.WriteLine("[update] ERRORE: impossibile localizzare l'eseguibile corrente.");
            return 1;
        }
        string dir;
        try { dir = Path.GetDirectoryName(Path.GetFullPath(self)) ?? ""; }
        catch { dir = ""; }
        if (string.IsNullOrEmpty(dir))
        {
            Console.Error.WriteLine("[update] ERRORE: percorso eseguibile non valido.");
            return 1;
        }
        string tmpRoot = Path.Combine(Path.GetTempPath(), "opencode-portable-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tmpRoot);
            string tmpExe = Path.Combine(tmpRoot, "OpencodePortable.exe");
            string tmpHash = Path.Combine(tmpRoot, "OpencodePortable.exe.sha256");
            Console.WriteLine("[update] download in corso...");
            if (!DownloadFile(rel.ExeUrl, tmpExe))
            {
                Console.Error.WriteLine("[update] ERRORE: download exe fallito.");
                return 1;
            }
            string expected = "";
            if (!string.IsNullOrEmpty(rel.HashUrl))
            {
                if (!DownloadFile(rel.HashUrl, tmpHash))
                {
                    Console.Error.WriteLine("[update] ERRORE: download .sha256 fallito, annullo (fail-closed).");
                    return 1;
                }
                try { expected = (File.ReadAllText(tmpHash) ?? "").Trim().ToLowerInvariant(); }
                catch { expected = ""; }
            }
            else
            {
                Console.Error.WriteLine("[update] ERRORE: asset .sha256 mancante nella release, annullo (fail-closed).");
                return 1;
            }
            string actual;
            try
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var fs = File.OpenRead(tmpExe);
                actual = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
            }
            catch
            {
                Console.Error.WriteLine("[update] ERRORE: hash non calcolabile, annullo.");
                return 1;
            }
            if (expected.Length != 64 || actual != expected)
            {
                Console.Error.WriteLine("[update] ERRORE: hash non corrispondente, annullo (possibile manomissione).");
                return 1;
            }
            var fi = new FileInfo(tmpExe);
            if (fi.Length < 5L * 1024 * 1024)
            {
                Console.Error.WriteLine("[update] ERRORE: exe scaricato sospettosamente piccolo, annullo.");
                return 1;
            }
            Console.WriteLine("[update] hash verificato.");
            string staged = Path.Combine(dir, "OpencodePortable.new.exe");
            try
            {
                File.Copy(tmpExe, staged, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[update] ERRORE: staging fallito ({ex.GetType().Name}). Chiudi altre istanze e riprova.");
                return 1;
            }
            if (!StageReplace(staged, self))
            {
                Console.Error.WriteLine("[update] exe scaricato e verificato, ma replace automatico non avviato.");
                Console.WriteLine($"[update] completa a mano: chiudi tutte le istanze e rinomina '{staged}' in '{self}'.");
                return 1;
            }
            Console.WriteLine("[update] OK: al prossimo avvio partirà la nuova versione. Chiudi e riapri l'app.");
            return 0;
        }
        finally
        {
            try
            {
                var left = PortableRun.RobustDelete(tmpRoot, 5000);
                if (left.Count > 0)
                    Console.Error.WriteLine("[update] ATTENZIONE: tmp non cancellata del tutto (verrà spazzata al prossimo avvio).");
            }
            catch { }
        }
    }

    private static bool DownloadFile(string url, string dest)
    {
        for (int i = 1; i <= 3; i++)
        {
            try
            {
                // Unauthenticated on purpose (see MakeClient): release-asset
                // URLs redirect to CDN hosts and the token must not follow.
                using var c = MakeClient(withAuth: false);
                using var resp = c.GetAsync(url).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                {
                    if (i == 3) return false;
                    Task.Delay(2000 * i).Wait();
                    continue;
                }
                using var fs = File.Create(dest);
                resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
                return true;
            }
            catch
            {
                try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                if (i == 3) return false;
                Task.Delay(2000 * i).Wait();
            }
        }
        return false;
    }

    private static bool StageReplace(string stagedNew, string currentExe)
    {
        return StageReplacePaths(stagedNew, currentExe);
    }

    /// <summary>
    /// Spawns the detached post-exit mover (see StageReplacePaths): the file
    /// swap itself happens after this process exits, so a `true` return only
    /// means the helper started. Completion is verified on the NEXT launch
    /// via StaleNewExe (a leftover `.new.exe` proves the move never landed).
    /// </summary>
    internal static bool StageReplacePaths(string stagedNew, string currentExe)
    {
        try
        {
            string shell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(shell)) return false;
            // Attende l'uscita del processo corrente poi sposta .new sopra .exe.
            // Percorsi in single-quote con doubling (no $ expansion).
            string qNew = "'" + stagedNew.Replace("'", "''") + "'";
            string qCur = "'" + currentExe.Replace("'", "''") + "'";
            var psi = new ProcessStartInfo
            {
                FileName = shell,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add($"Start-Sleep -Seconds 2; Move-Item -LiteralPath {qNew} -Destination {qCur} -Force");
            using var p = Process.Start(psi);
            return p is not null;
        }
        catch { return false; }
    }

    internal const string StagedNewExeName = "OpencodePortable.new.exe";

    /// <summary>
    /// A leftover staged `.new.exe` next to the running exe proves a previous
    /// post-exit replace never landed (permissions, another live instance).
    /// Returns its path, or null when no update is pending.
    /// </summary>
    internal static string? StaleNewExe(string exeDir)
    {
        try
        {
            string cand = Path.Combine(exeDir, StagedNewExeName);
            return File.Exists(cand) ? cand : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Second chance for a pending replace: re-spawns the detached post-exit
    /// mover (idempotent — a concurrent helper that already moved the file
    /// simply finds nothing to move). Called on normal exit paths only.
    /// </summary>
    internal static void RetryPendingReplace(string stagedNew, string currentExe)
    {
        try
        {
            if (StageReplacePaths(stagedNew, currentExe))
            {
                // Console write that never throws (detached processes may
                // have no console); a failed write must not fail the exit.
                try { Console.Error.WriteLine("[update] sostituzione in sospeso riavviata: verrà applicata alla chiusura."); }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Test hook (--test-update-replace): stale detection plus a real
    /// detached-helper replace of two dummy files (no network, ~2-20 s).
    /// </summary>
    public static int TestUpdateReplace()
    {
        string baseDir = Path.Combine(Path.GetTempPath(),
            "opencode-testupdaterep-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(baseDir);
            string cur = Path.Combine(baseDir, "OpencodePortable.exe");
            string nxt = Path.Combine(baseDir, StagedNewExeName);
            File.WriteAllText(cur, "OLD");
            File.WriteAllText(nxt, "NEW");
            bool detected = StaleNewExe(baseDir) == nxt;
            Console.WriteLine($"[test-update-replace] detected={detected}");
            if (!detected) { Console.Error.WriteLine("[test-update-replace] FALLITO (detect)"); return 1; }
            if (!StageReplacePaths(nxt, cur)) { Console.Error.WriteLine("[test-update-replace] FALLITO (spawn)"); return 1; }
            bool done = false;
            for (int i = 0; i < 100; i++)
            {
                Thread.Sleep(200);
                try
                {
                    if (!File.Exists(nxt) && File.ReadAllText(cur) == "NEW") { done = true; break; }
                }
                catch { }
            }
            Console.WriteLine($"[test-update-replace] replaced={done}");
            bool absentNull = !done ? false : StaleNewExe(baseDir) is null;
            Console.WriteLine($"[test-update-replace] absent-null={absentNull}");
            bool ok = done && absentNull;
            Console.WriteLine(ok ? "[test-update-replace] OK" : "[test-update-replace] FALLITO");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[test-update-replace] ERRORE: {ex.Message}");
            return 1;
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }

    internal static int SweepUpdateTemp()
    {
        int removed = 0;
        try
        {
            string tmp = Path.GetTempPath();
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (string d in Directory.GetDirectories(tmp, "opencode-portable-update-*"))
            {
                try
                {
                    if (Directory.GetLastWriteTime(d) < cutoff)
                    {
                        PortableRun.RobustDelete(d, 3000);
                        if (!Directory.Exists(d)) removed++;
                    }
                }
                catch { }
            }
        }
        catch { }
        return removed;
    }

    // ---- Test hooks (gated behind OPENCODE_ENABLE_TESTFLAGS=1) ----

    public static int TestUpdateParse()
    {
        var cases = new (string cur, string latest, bool expect)[]
        {
            ("1.3.6", "1.4.0", true),
            ("1.4.0", "1.3.6", false),
            ("1.3.6", "1.3.6", false),
            ("v1.3.6", "v1.4.0", true),
            ("2.0.29", "2.0.30", true),
            ("1.19.0", "2.0.29", true),
            ("2.0.29", "1.19.0", false),
            ("bogus", "1.4.0", false),
            ("1.3.6", "bogus", false),
            // 2-component input: used to hang forever (loop length never
            // changed); must parse and compare instead.
            ("1.4", "1.5", true),
            ("1.4", "1.4.0", false),
            ("v1.4", "v1.4.1", true),
            ("1.4-beta", "1.4.0", false),
        };
        foreach (var (cur, latest, expect) in cases)
        {
            bool got = IsNewer(cur, latest);
            Console.WriteLine($"[test-update-parse] {cur} -> {latest}: {got} (atteso {expect})");
            if (got != expect)
            {
                Console.Error.WriteLine("[test-update-parse] FALLITO");
                return 1;
            }
        }
        Console.WriteLine("[test-update-parse] OK");
        return 0;
    }

    public static int TestUpdateVerify()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "opencode-testupdate-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(baseDir);
            string f = Path.Combine(baseDir, "fake.exe");
            File.WriteAllBytes(f, new byte[1024]);
            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var fs = File.OpenRead(f))
                hash = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
            Console.WriteLine($"[test-update-verify] hash={hash} len={hash.Length}");
            bool ok = hash.Length == 64;
            Console.WriteLine(ok ? "[test-update-verify] OK" : "[test-update-verify] FALLITO");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[test-update-verify] ERRORE: {ex.Message}");
            return 1;
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }
}
