using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using BepInEx.Logging;
using OriCoop.Net;
using UnityEngine;

namespace OriCoop
{
    // Live co-op trace (playtest pain point P6). One file per game process:
    //   BepInEx\OriCoop-trace-<yyyyMMdd-HHmmss>-<pid>.log
    // Every line is flushed to the OS as soon as it is written, so it survives a crash or a force quit.
    // Line format (space separated, values with spaces are quoted):
    //   <local HH:mm:ss.fff> <utc yyyy-MM-ddTHH:mm:ss.fffZ> f=<frame> ff=<fixed frame> t=<game s> sid=<session> role=<P1|P2|..> <subsys> <event> k=v ...
    // `sid` is the NetPeer handshake session id, the same on both ends of one connection ("-" when offline).
    // Every mod/BepInEx log line is mirrored as subsys "log", so old messages get the same stamps.
    // Server/peer IPv4 addresses are masked everywhere in the trace.
    //
    // Use from any file:
    //   CoopTrace.Event("enemy", "death", "id", id, "x", pos.x);          // state change, level 1
    //   if (CoopTrace.SampleDue("player")) CoopTrace.Sample("player", "pos", ...); // periodic, level 2
    //   if (CoopTrace.Changed("cam.lock", locked.ToString())) CoopTrace.Event("cam", locked ? "lock" : "unlock");
    // Subsystems: player phys enemy story cam hud save scene net log test (any short word works).
    public static class CoopTrace
    {
        public const int Off = 0, Events = 1, Samples = 2, Verbose = 3;
        public const string FormatVersion = "1";

        static ConfigEntry<int> s_level, s_sampleFrames, s_maxMB, s_keepFiles;
        static ConfigEntry<string> s_subLevels;
        static readonly Dictionary<string, int> s_sub = new Dictionary<string, int>();
        static readonly object s_lock = new object();
        static StreamWriter s_w;
        static long s_bytes;
        static int s_capState;               // 0 normal, 1 over cap: only level 1 lines, 2 over 2x cap: stopped
        static readonly Dictionary<string, string> s_last = new Dictionary<string, string>();
        static readonly Dictionary<string, float> s_due = new Dictionary<string, float>();

        // Clock cached on the main thread, so other threads can stamp lines safely.
        static int s_frame, s_fixed;
        static float s_gameTime, s_real;
        static string s_sid = "-", s_role = "-", s_scene = "";
        static float s_nextFlush, s_nextPoll;
        static TextWriter s_bepWriter; static bool s_bepLooked;
        static bool s_inLog;                  // no recursion from the log mirror

        static int s_levelOverride = -1;     // ORICOOP_TRACE_LEVEL (tests), never written to the config
        public static int Level { get { return s_levelOverride >= 0 ? s_levelOverride : s_level == null ? Off : s_level.Value; } }
        public static string FilePath { get; private set; }
        public static string SessionId { get { return s_sid; } }
        public static string Role { get { return s_role; } }
        public static int Frame { get { return s_frame; } }
        public static int FixedFrame { get { return s_fixed; } }
        public static int SampleFrames { get { return s_sampleFrames == null ? 30 : Math.Max(1, s_sampleFrames.Value); } }

        public static void Init(ConfigFile cfg, string bepinexDir)
        {
            s_level = cfg.Bind("Trace", "Level", 2, "Co-op trace file (BepInEx\\OriCoop-trace-*.log): 0 = off, 1 = events (state changes), 2 = + periodic samples, 3 = + verbose.");
            s_subLevels = cfg.Bind("Trace", "SubsystemLevels", "", "Per-subsystem override, e.g. \"phys=3,enemy=1,log=1\".");
            s_sampleFrames = cfg.Bind("Trace", "SampleFrames", 30, "Periodic samples (positions, physics objects) every this many frames.");
            s_maxMB = cfg.Bind("Trace", "MaxFileMB", 64, "Above this size only level-1 events are written; at twice this size the file stops.");
            s_keepFiles = cfg.Bind("Trace", "KeepFiles", 12, "Older trace files beyond this count are deleted at start.");
            ParseSub(); s_subLevels.SettingChanged += (a, b) => ParseSub();
            string env = Environment.GetEnvironmentVariable("ORICOOP_TRACE_LEVEL");
            int envLevel; if (!string.IsNullOrEmpty(env) && int.TryParse(env, out envLevel)) s_levelOverride = envLevel;
            StampDiskLog();
            DateTime started = DateTime.Now;
            Plugin.Log.LogInfo(ClockSync.Header(started, started.ToUniversalTime()));
            if (Level <= Off) return;
            try
            {
                string dir = Environment.GetEnvironmentVariable("ORICOOP_TRACE_DIR");
                if (string.IsNullOrEmpty(dir)) dir = bepinexDir;
                Directory.CreateDirectory(dir);
                Prune(dir);
                DateTime now = DateTime.Now;
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                FilePath = Path.Combine(dir, "OriCoop-trace-" + now.ToString("yyyyMMdd-HHmmss") + "-" + pid + ".log");
                var fs = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                s_w = new StreamWriter(fs, new UTF8Encoding(false)); s_w.AutoFlush = true;
                WriteRaw("# OriCoop trace format=" + FormatVersion + " mod=" + Plugin.Version + " proto=" + NetPlay.ModVersion
                    + " build=" + BuildId() + " pid=" + pid + " started_local=" + now.ToString("yyyy-MM-ddTHH:mm:ss.fff")
                    + " started_utc=" + now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + " utc_offset_min=" + (int)(now - now.ToUniversalTime()).TotalMinutes);
                BepInEx.Logging.Logger.Listeners.Add(new Mirror());
                Application.logMessageReceived += OnUnityLog;
                AppDomain.CurrentDomain.UnhandledException += (s, e) => { Event("log", "unhandled", "ex", e.ExceptionObject); Flush(); };
            }
            catch (Exception ex) { s_w = null; Plugin.Log.LogWarning("trace: could not open trace file: " + ex.Message); }
        }

        static void ParseSub()
        {
            lock (s_lock)
            {
                s_sub.Clear();
                foreach (string part in (s_subLevels.Value ?? "").Split(','))
                {
                    string[] kv = part.Split('='); int lv;
                    if (kv.Length == 2 && int.TryParse(kv[1].Trim(), out lv)) s_sub[kv[0].Trim()] = lv;
                }
            }
        }

        static void Prune(string dir)
        {
            try
            {
                var files = new List<FileInfo>(new DirectoryInfo(dir).GetFiles("OriCoop-trace-*.log"));
                files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                int keep = Math.Max(1, s_keepFiles.Value - 1);
                for (int i = keep; i < files.Count; i++) { try { files[i].Delete(); } catch { } }
            }
            catch { }
        }

        static string BuildId()
        {
            try
            {
                string dll = typeof(CoopTrace).Assembly.Location;
                byte[] hash = System.Security.Cryptography.MD5.Create().ComputeHash(File.ReadAllBytes(dll));
                return File.GetLastWriteTime(dll).ToString("yyyyMMdd-HHmm") + "/" + BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
            }
            catch { return "?"; }
        }

        // ---- level checks ----
        public static bool On(string subsys, int level)
        {
            if (s_w == null || s_capState == 2) return false;
            if (s_capState == 1 && level > Events) return false;
            int lv; if (!s_sub.TryGetValue(subsys, out lv)) lv = Level;
            return level <= lv;
        }

        public static void Event(string subsys, string name, params object[] kv) { At(Events, subsys, name, kv); }
        public static void Sample(string subsys, string name, params object[] kv) { At(Samples, subsys, name, kv); }
        public static void Verb(string subsys, string name, params object[] kv) { At(Verbose, subsys, name, kv); }

        // True on frames where a periodic sample for this subsystem should be taken (and level 2 is on).
        public static bool SampleDue(string subsys) { return On(subsys, Samples) && s_frame % SampleFrames == 0; }

        // True when `value` differs from the last value seen for `key` (first call: true). For state-change events.
        public static bool Changed(string key, string value)
        {
            lock (s_lock)
            {
                string old;
                if (s_last.TryGetValue(key, out old) && old == value) return false;
                s_last[key] = value; return true;
            }
        }
        public static void Forget(string key) { lock (s_lock) s_last.Remove(key); }

        // Rate limit: true at most once per `seconds` (real time) for `key`.
        public static bool Every(string key, float seconds)
        {
            lock (s_lock)
            {
                float due;
                if (s_due.TryGetValue(key, out due) && s_real < due) return false;
                s_due[key] = s_real + seconds; return true;
            }
        }

        public static void At(int level, string subsys, string name, object[] kv)
        {
            if (!On(subsys, level)) return;
            var sb = new StringBuilder(160);
            DateTime now = DateTime.Now;
            sb.Append(now.ToString("HH:mm:ss.fff")).Append(' ').Append(now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))
              .Append(" f=").Append(s_frame).Append(" ff=").Append(s_fixed).Append(" t=").Append(s_gameTime.ToString("F2"))
              .Append(" sid=").Append(s_sid).Append(" role=").Append(s_role).Append(' ').Append(subsys).Append(' ').Append(name);
            if (kv != null)
                for (int i = 0; i + 1 < kv.Length; i += 2)
                    sb.Append(' ').Append(kv[i]).Append('=').Append(Fmt(kv[i + 1]));
            WriteRaw(Mask(sb.ToString()));
        }

        static string Fmt(object v)
        {
            if (v == null) return "null";
            string s;
            if (v is float) s = ((float)v).ToString("0.###");
            else if (v is double) s = ((double)v).ToString("0.###");
            else if (v is Vector3) { var p = (Vector3)v; s = p.x.ToString("0.##") + "," + p.y.ToString("0.##") + (p.z != 0f ? "," + p.z.ToString("0.##") : ""); }
            else if (v is Vector2) { var p = (Vector2)v; s = p.x.ToString("0.##") + "," + p.y.ToString("0.##"); }
            else if (v is bool) s = (bool)v ? "1" : "0";
            else if (v is UnityEngine.Object) { var o = (UnityEngine.Object)v; s = o == null ? "null" : o.name + "#" + o.GetInstanceID(); }
            else s = v.ToString();
            if (s.Length > 300) s = s.Substring(0, 300) + "...";
            if (s.Length == 0 || s.IndexOf(' ') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0)
                s = "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\"";
            return s;
        }

        static readonly Regex s_ip = new Regex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b");
        // Masks IPv4 addresses except loopback (tests use 127.0.0.1).
        public static string Mask(string s)
        {
            if (s.IndexOf('.') < 0) return s;
            return s_ip.Replace(s, m => m.Value.StartsWith("127.") ? m.Value : "ip#" + ((uint)m.Value.GetHashCode() % 10000).ToString("0000"));
        }

        static void WriteRaw(string line)
        {
            lock (s_lock)
            {
                if (s_w == null || s_capState == 2) return;
                try
                {
                    s_w.WriteLine(line);
                    s_bytes += line.Length + 2;
                    long cap = (long)Math.Max(1, s_maxMB.Value) << 20;
                    if (s_capState == 0 && s_bytes > cap) { s_capState = 1; s_w.WriteLine("# size cap reached: samples and verbose lines stop here"); }
                    else if (s_capState == 1 && s_bytes > 2 * cap) { s_capState = 2; s_w.WriteLine("# 2x size cap reached: trace stops here"); }
                }
                catch { }
            }
        }

        public static void Flush()
        {
            lock (s_lock) { try { if (s_w != null) s_w.Flush(); } catch { } }
            try { if (s_bepWriter != null) s_bepWriter.Flush(); } catch { }
        }

        // ---- main thread hooks (Plugin.Runner) ----
        public static void FixedTick() { s_fixed++; }

        public static void Tick()
        {
            s_frame = Time.frameCount; s_gameTime = Time.time; s_real = Time.realtimeSinceStartup;
            if (s_real >= s_nextFlush) { s_nextFlush = s_real + 0.5f; FlushBepInEx(); }
            try { CoopClock.Tick(); } catch (Exception e) { if (Every("clock.error", 60f)) Plugin.Log.LogWarning("clock: " + e.Message); }
            if (s_w == null) return;
            if (s_real >= s_nextPoll) { s_nextPoll = s_real + 0.25f; PollSession(); }
            CoopTraceHooks.Tick();
        }

        // BepInEx's LogOutput.log only flushes on a 2 s thread-pool timer; flush it from the game loop too.
        static void FlushBepInEx()
        {
            if (!s_bepLooked)
            {
                s_bepLooked = true;
                foreach (ILogListener l in BepInEx.Logging.Logger.Listeners)
                    if (l is DiskLogListener) s_bepWriter = ((DiskLogListener)l).LogWriter;
                    else if (l is UtcDiskLog) s_bepWriter = ((UtcDiskLog)l).Inner.LogWriter;
            }
            try { if (s_bepWriter != null) s_bepWriter.Flush(); } catch { }
        }

        // T-067: every line this mod writes to BepInEx\LogOutput.log starts with its UTC time
        // ("[Info   :Ori Local Co-op] 2026-11-18T10:44:35.123Z net: ..."). BepInEx's DiskLogListener is swapped for a
        // wrapper that stamps our source's lines and passes everything else through unchanged.
        public const string StampedSource = "Ori Local Co-op";
        static void StampDiskLog()
        {
            try
            {
                var listeners = BepInEx.Logging.Logger.Listeners;
                foreach (ILogListener l in new List<ILogListener>(listeners))
                {
                    DiskLogListener disk = l as DiskLogListener;
                    if (disk == null) continue;
                    listeners.Remove(disk);
                    listeners.Add(new UtcDiskLog(disk));
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("log: could not add UTC stamps to LogOutput.log: " + ex.Message); }
        }

        sealed class UtcDiskLog : ILogListener
        {
            public readonly DiskLogListener Inner;
            public UtcDiskLog(DiskLogListener inner) { Inner = inner; }
            public void LogEvent(object sender, LogEventArgs e)
            {
                if (e.Source != null && e.Source.SourceName == StampedSource)
                    e = new LogEventArgs(ClockSync.Stamp(DateTime.UtcNow, e.Data), e.Level, e.Source);
                Inner.LogEvent(sender, e);
            }
            public void Dispose() { Inner.Dispose(); }
        }

        static FieldInfo s_sessionField;
        static void PollSession()
        {
            string sid = "-";
            try
            {
                NetPeer peer = NetPlay.Peer;
                if (peer != null && NetPlay.Connected)
                {
                    if (s_sessionField == null) s_sessionField = typeof(NetPeer).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic);
                    uint id = s_sessionField == null ? 0u : (uint)s_sessionField.GetValue(peer);
                    if (id != 0) sid = id.ToString("x8");
                }
            }
            catch { }
            string role = NetPlay.Enabled ? Roles.Tag(Roles.Mine) : "local";
            string scene = "";
            try { if (Core.Scenes.Manager != null && Core.Scenes.Manager.CurrentScene != null) scene = Core.Scenes.Manager.CurrentScene.Scene ?? ""; } catch { }
            bool sessionChanged = sid != s_sid;
            if (sessionChanged && s_sid != "-") Event("net", "session.end", "sid", s_sid);
            s_sid = sid; s_role = role;
            if (sessionChanged && sid != "-")
                Event("net", "session.start", "transport", NetPlay.TransportName, "host", NetPlay.IsHost, "mod", Plugin.Version,
                    "proto", NetPlay.ModVersion, "remote_proto", NetPlay.RemoteVersion, "scene", scene);
            if (Changed("trace.role", role)) Event("net", "role", "role", role, "mode", NetPlay.IsHost ? "host" : NetPlay.Enabled ? "join" : "off");
            if (scene != s_scene) { Event("scene", "current", "from", s_scene, "to", scene); s_scene = scene; }
        }

        static void OnUnityLog(string condition, string stack, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error && type != LogType.Assert) return;
            if (s_inLog) return;
            s_inLog = true;
            try
            {
                string top = stack ?? ""; int nl = top.IndexOf('\n'); if (nl > 0) top = top.Substring(0, nl);
                Event("log", "unity." + type.ToString().ToLowerInvariant(), "msg", condition, "at", top);
            }
            finally { s_inLog = false; }
        }

        sealed class Mirror : ILogListener
        {
            public void LogEvent(object sender, LogEventArgs e)
            {
                if (s_inLog) return;
                s_inLog = true;
                try
                {
                    string src = e.Source == null ? "?" : e.Source.SourceName;
                    if (src == "Unity Log") return;   // Unity errors/exceptions come through OnUnityLog with the stack top
                    At(Events, "log", e.Level.ToString().ToLowerInvariant(), new object[] { "src", src, "msg", e.Data });
                }
                finally { s_inLog = false; }
            }
            public void Dispose() { Flush(); }
        }
    }
}
