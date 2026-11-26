using System;
using System.Collections.Generic;
using System.Text;

namespace OriCoop.Diagnostics
{
    // T-252 (#340), trace only: decides from one reading of the game's audio state (taken about once a second by
    // CoopTraceAudio.cs) whether the sound looks lost, and when that is worth a trace line. Unity-free (unit tests:
    // src\Tests\AudioWatch). Nothing here changes the sound: it only explains a silence afterwards.
    //
    // Sound went missing once while a player was down and watching the partner, and it is not known what caused it.
    // Each reason below is one thing that would make the game silent, or the partner's sounds inaudible, without any
    // other trace line saying so. A reason counts only after it held for its hold time, so the camera handing the
    // listener over during a scene load, or a short quiet moment, is not reported.
    public sealed class AudioReading
    {
        public bool InGameplay;            // NetPlay.LocalInGameplay(): a Sein of our own is in a level
        public bool Running;               // Time.timeScale above 0.01 (not paused)
        public int Listeners = 1;          // enabled AudioListeners on active objects
        public bool Paused;                // AudioListener.pause
        public float Volume = 1f;          // AudioListener.volume
        public float SettingsMin = -1f;    // the lowest volume in the game's own sound settings, -1 unknown
        public string SettingsMinName;
        public float ListenerToView = -1f; // listener to the camera's target, in the level's plane; -1 unknown
        public bool Spectating;            // all our players are down and the camera follows a teammate (T-042)
        public float ListenerToWatched = -1f; // listener to the watched teammate; -1 unknown
        public int Playing;                // AudioSources playing
        public int NearViewPlaying;        // ... of those, within NearView of the camera's target
        public int NearViewQuiet;          // ... of those, too quiet to hear (muted, volume ~0, or out of linear range)
        public double DspAdvance = -1;     // AudioSettings.dspTime since the last reading, -1 unknown
        public float RealAdvance;          // real seconds since the last reading
    }

    public sealed class AudioWatch
    {
        public const float NearView = 10f;      // units: sounds this close to what the camera shows should be heard
        public const float FarListener = 15f;   // units: a listener this far from the view hears the wrong place
        public const float RepeatSeconds = 30f; // an unchanged suspicion is written again this often
        public const int MaxEpisodes = 100;     // per process: a flapping condition cannot fill the trace

        // Reason names are fixed strings: they are what Yuri / the agents search the log for.
        public const string NoListener = "no_listener", ManyListeners = "many_listeners", Paused = "listener_paused",
            Muted = "listener_volume", SettingsZero = "settings_zero", OffView = "listener_off_view",
            FarWatched = "listener_far_from_watched", ViewQuiet = "view_sounds_quiet", Silent = "silent",
            DspStalled = "dsp_stalled";

        readonly Dictionary<string, float> m_since = new Dictionary<string, float>();
        string m_current = "";
        float m_episodeStart, m_lastReport;
        public int Episodes { get; private set; }
        public int EpisodesDropped { get; private set; }
        public string Current { get { return m_current; } }

        public static float Hold(string reason)
        {
            switch (reason)
            {
                case Silent: return 5f;
                case NoListener: case ManyListeners: case ViewQuiet: case DspStalled: return 2f;
                default: return 1f;
            }
        }

        // What the reading shows right now, before the hold time.
        public static List<string> Seen(AudioReading r)
        {
            List<string> s = new List<string>();
            // Only in a level: while the game starts or loads a save there is no camera (and no listener) yet.
            if (r.InGameplay && r.Listeners == 0) s.Add(NoListener);
            if (r.InGameplay && r.Listeners > 1) s.Add(ManyListeners);
            if (r.Paused && r.Running && r.InGameplay) s.Add(Paused);
            if (r.Volume < 0.05f) s.Add(Muted);
            if (r.SettingsMin >= 0f && r.SettingsMin < 0.01f) s.Add(SettingsZero);
            if (r.InGameplay && r.ListenerToView > FarListener) s.Add(OffView);
            if (r.Spectating && r.ListenerToWatched > FarListener) s.Add(FarWatched);
            if (r.InGameplay && r.NearViewPlaying >= 2 && r.NearViewQuiet >= r.NearViewPlaying) s.Add(ViewQuiet);
            if (r.InGameplay && r.Running && r.Playing == 0) s.Add(Silent);
            if (r.DspAdvance >= 0 && r.RealAdvance >= 0.5f && r.DspAdvance < 0.1 * r.RealAdvance) s.Add(DspStalled);
            return s;
        }

        // The reasons that held for their hold time, sorted and comma separated ("" = sound looks fine).
        public string Reasons(AudioReading r, float now)
        {
            List<string> seen = Seen(r);
            List<string> gone = new List<string>();
            foreach (string k in m_since.Keys) if (!seen.Contains(k)) gone.Add(k);
            foreach (string k in gone) m_since.Remove(k);
            List<string> held = new List<string>();
            foreach (string k in seen)
            {
                float since;
                if (!m_since.TryGetValue(k, out since)) { m_since[k] = now; since = now; }
                if (now - since >= Hold(k) - 0.001f) held.Add(k);
            }
            held.Sort(StringComparer.Ordinal);
            return string.Join(",", held.ToArray());
        }

        public enum Step { None, Start, Change, Still, Clear }

        // What to write for the reasons now. `duration` = seconds since the episode began (Change / Still / Clear).
        public Step Next(string reasons, float now, out float duration)
        {
            duration = 0f;
            reasons = reasons ?? "";
            string was = m_current;
            if (reasons == was)
            {
                if (was.Length == 0 || now - m_lastReport < RepeatSeconds) return Step.None;
                m_lastReport = now; duration = now - m_episodeStart;
                return Dropping() ? Step.None : Step.Still;
            }
            m_current = reasons;
            if (was.Length == 0)
            {
                m_episodeStart = now; m_lastReport = now; Episodes++;
                if (Dropping()) { EpisodesDropped++; return Step.None; }
                return Step.Start;
            }
            duration = now - m_episodeStart; m_lastReport = now;
            if (Dropping()) return Step.None;
            return reasons.Length == 0 ? Step.Clear : Step.Change;
        }

        bool Dropping() { return Episodes > MaxEpisodes; }
    }

    // A cap on the audio watcher's own lines: at most `perMinute` in any minute (a token bucket that refills
    // continuously). Lines over the cap are counted, and the count goes into the next line that is allowed.
    public sealed class AudioLogBudget
    {
        readonly float m_perMinute;
        float m_tokens, m_last = -1f;
        public int Suppressed { get; private set; }
        public AudioLogBudget(float perMinute) { m_perMinute = perMinute; m_tokens = perMinute; }

        public bool Take(float now)
        {
            if (m_last >= 0f) m_tokens = Math.Min(m_perMinute, m_tokens + (now - m_last) * m_perMinute / 60f);
            m_last = now;
            if (m_tokens < 1f) { Suppressed++; return false; }
            m_tokens -= 1f;
            return true;
        }

        // The number of lines dropped since the last call (0 when none).
        public int TakeSuppressed() { int n = Suppressed; Suppressed = 0; return n; }
    }

    // Compact text of a few sounds for a trace field: "name@dist/vol/max/rolloff/blend;..." (blend 0 = 2D, 1 = 3D) (fields with spaces quoted by
    // CoopTrace itself). Names lose spaces and separators so the list stays one value.
    public static class AudioText
    {
        public static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name)) return "-";
            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name) sb.Append(c == ' ' || c == ';' || c == '@' || c == '/' || c == '=' || c == '"' ? '_' : c);
            return sb.Length > 48 ? sb.ToString(0, 48) : sb.ToString();
        }

        public static string Sound(string name, float distance, float volume, float maxDistance, string rolloff, float blend)
        {
            return Clean(name) + "@" + distance.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                + "/" + volume.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                + "/" + maxDistance.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                + "/" + (rolloff ?? "-")
                + "/" + blend.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
