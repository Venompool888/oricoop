using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Game;
using HarmonyLib;
using OriCoop.Diagnostics;
using UnityEngine;

namespace OriCoop
{
    // T-252 (#340), trace subsystem `audio`, observe only: the game's sound once went missing while a player was down
    // and watching the partner, and nothing in the logs said why. This writes what the sound depends on, so the next
    // time it happens the trace shows it. Nothing here changes any sound.
    //
    // Level 1 (default):
    //   audio watch.init       once: which game volume settings were found
    //   audio listener         the AudioListener changed (which object, enabled, how many)
    //   audio pause / volume   AudioListener.pause or AudioListener.volume changed
    //   audio settings         a volume in the game's own sound settings changed
    //   audio focus            the game window gained / lost the focus
    //   audio device.changed   Windows changed the output device or its format (AudioSettings callback)
    //   audio snapshot why=    full state at: spectate start / stop / other teammate, own player down / up, new scene
    //   audio lost.suspect     sound looks lost (reasons=, see Diagnostics\AudioWatch.cs) + full state; also a
    //                          warning in LogOutput.log. Then lost.change / lost.still (every 30 s) / lost.clear.
    //   audio heartbeat        every 120 s: one short line (playing sources, listener, volumes)
    // Level 2: audio sample, the full state every 5 s.
    // The watcher's own lines are capped at 40 a minute (audio budget.dropped says how many were left out).
    public static class CoopTraceAudio
    {
        const string Sub = "audio";
        const float PollSeconds = 1f, HeartbeatSeconds = 120f, SampleSeconds = 5f, ListenerLookSeconds = 5f;

        static bool s_init, s_seeded;
        static float s_nextPoll, s_nextHeartbeat, s_nextSample, s_nextListenerLook, s_lastReal = -1f;
        static double s_lastDsp = -1;
        static UnityEngine.AudioListener s_listener;
        static int s_listenerCount;
        static readonly AudioWatch s_watch = new AudioWatch();
        static readonly AudioLogBudget s_budget = new AudioLogBudget(40f);
        static readonly AudioReading s_r = new AudioReading();
        static volatile int s_deviceChanges, s_deviceChangesSeen;
        static volatile bool s_lastDeviceWasChanged;

        // Game volume settings, found by name (the game's class is not referenced, so a rename cannot break the build).
        static object s_settings;
        static readonly List<MemberInfo> s_volumeMembers = new List<MemberInfo>();
        static string s_settingsText = "-";
        static readonly PropertyInfo s_spatialBlend = typeof(UnityEngine.AudioSource).GetProperty("spatialBlend");

        // Per poll, for the snapshot fields.
        static int s_quiet, s_outOfRange, s_soundPlayers = -1;
        static string s_nearest = "-";
        static Vector3 s_view; static bool s_haveView;
        static string s_scene = "", s_camTarget = "-";

        public static void Tick()
        {
            if (!CoopTrace.On(Sub, CoopTrace.Events)) return;
            float now = Time.realtimeSinceStartup;
            if (!s_init) Init();
            if (s_deviceChanges != s_deviceChangesSeen)
            {
                s_deviceChangesSeen = s_deviceChanges;
                Line(CoopTrace.Events, "device.changed", "device", s_lastDeviceWasChanged, "count", s_deviceChangesSeen,
                    "sample_rate", UnityEngine.AudioSettings.outputSampleRate, "speakers", UnityEngine.AudioSettings.speakerMode);
                Snapshot("snapshot", "why", "device.changed");
            }
            if (now < s_nextPoll) return;
            s_nextPoll = now + PollSeconds;
            try { Poll(now); }
            catch (Exception e) { if (CoopTrace.Every("audio.error", 60f)) Plugin.Log.LogWarning("trace audio: " + e.Message); }
        }

        static void Init()
        {
            s_init = true;
            try
            {
                // AudioSettings.OnAudioConfigurationChanged (Unity 5+), bound by reflection: an older Unity just skips it.
                EventInfo evt = typeof(UnityEngine.AudioSettings).GetEvent("OnAudioConfigurationChanged", BindingFlags.Public | BindingFlags.Static);
                MethodInfo handler = typeof(CoopTraceAudio).GetMethod("OnAudioConfigurationChanged", BindingFlags.NonPublic | BindingFlags.Static);
                if (evt != null && handler != null) evt.AddEventHandler(null, Delegate.CreateDelegate(evt.EventHandlerType, handler));
            }
            catch (Exception) { }
            FindSettings();
            Line(CoopTrace.Events, "watch.init", "settings", s_settingsText, "sample_rate", UnityEngine.AudioSettings.outputSampleRate,
                "speakers", UnityEngine.AudioSettings.speakerMode, "blend", s_spatialBlend != null);
        }

        // Called by Unity, possibly off the main thread: only counts.
        static void OnAudioConfigurationChanged(bool deviceWasChanged)
        {
            s_lastDeviceWasChanged = deviceWasChanged;
            s_deviceChanges++;
        }

        static void FindSettings()
        {
            List<string> names = new List<string>();
            try
            {
                foreach (string typeName in new[] { "GameSettings", "SoundSettings", "AudioSettingsController" })
                {
                    Type t = AccessTools.TypeByName(typeName);
                    if (t == null) continue;
                    object inst = null;
                    PropertyInfo ip = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (ip != null) inst = ip.GetValue(null, null);
                    if (inst == null)
                    {
                        FieldInfo f = t.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (f != null) inst = f.GetValue(null);
                    }
                    if (inst == null) continue;
                    const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    foreach (PropertyInfo p in t.GetProperties(all))
                        if (p.PropertyType == typeof(float) && p.GetIndexParameters().Length == 0 && p.CanRead
                            && p.Name.IndexOf("volume", StringComparison.OrdinalIgnoreCase) >= 0)
                        { s_volumeMembers.Add(p); names.Add(t.Name + "." + p.Name); }
                    if (s_volumeMembers.Count == 0)
                        foreach (FieldInfo f in t.GetFields(all))
                            if (f.FieldType == typeof(float) && f.Name.IndexOf("volume", StringComparison.OrdinalIgnoreCase) >= 0)
                            { s_volumeMembers.Add(f); names.Add(t.Name + "." + f.Name); }
                    if (s_volumeMembers.Count != 0) { s_settings = inst; break; }
                }
            }
            catch (Exception) { s_volumeMembers.Clear(); s_settings = null; }
            s_settingsText = names.Count == 0 ? "none" : string.Join(",", names.ToArray());
        }

        static string ReadSettings(out float min, out string minName)
        {
            min = -1f; minName = null;
            if (s_settings == null || s_volumeMembers.Count == 0) return "-";
            StringBuilder sb = new StringBuilder();
            foreach (MemberInfo m in s_volumeMembers)
            {
                float v;
                try
                {
                    PropertyInfo p = m as PropertyInfo;
                    v = p != null ? (float)p.GetValue(s_settings, null) : (float)((FieldInfo)m).GetValue(s_settings);
                }
                catch (Exception) { continue; }
                if (sb.Length != 0) sb.Append(',');
                sb.Append(m.Name).Append(':').Append(v.ToString("0.##"));
                if (min < 0f || v < min) { min = v; minName = m.Name; }
            }
            return sb.Length == 0 ? "-" : sb.ToString();
        }

        static void Poll(float now)
        {
            AudioReading r = s_r;
            float real = s_lastReal < 0f ? 0f : now - s_lastReal;
            double dsp = UnityEngine.AudioSettings.dspTime;
            r.RealAdvance = real;
            r.DspAdvance = s_lastDsp < 0 ? -1 : dsp - s_lastDsp;
            s_lastReal = now; s_lastDsp = dsp;

            // Listener: the cached one, looked up again every few seconds or once it is gone / off.
            if (s_listener == null || !s_listener.enabled || !s_listener.gameObject.activeInHierarchy || now >= s_nextListenerLook)
            {
                s_nextListenerLook = now + ListenerLookSeconds;
                s_listenerCount = 0; s_listener = null;
                foreach (UnityEngine.AudioListener l in UnityEngine.Object.FindObjectsOfType<UnityEngine.AudioListener>())
                {
                    if (l == null || !l.enabled || !l.gameObject.activeInHierarchy) continue;
                    s_listenerCount++;
                    if (s_listener == null) s_listener = l;
                }
            }
            r.Listeners = s_listenerCount;
            r.Paused = UnityEngine.AudioListener.pause;
            r.Volume = UnityEngine.AudioListener.volume;
            r.InGameplay = NetPlay.LocalInGameplay();
            r.Running = Time.timeScale > 0.01f;
            string settings = ReadSettings(out r.SettingsMin, out r.SettingsMinName);

            // What the camera shows, and whom we watch while down.
            s_haveView = false; s_camTarget = "-";
            try
            {
                var cam = UI.Cameras.Current;
                if (cam != null && cam.Target != null) { s_view = cam.Target.position; s_haveView = true; s_camTarget = PathOf(cam.Target); }
                else if (cam != null && cam.Camera != null) { s_view = cam.Camera.transform.position; s_haveView = true; s_camTarget = "camera"; }
            }
            catch (Exception) { }
            SeinCharacter watched = null;
            try { watched = Revive.SpectateSein; } catch (Exception) { }
            r.Spectating = watched != null;
            Vector3 listenerPos = s_listener != null ? s_listener.transform.position : Vector3.zero;
            r.ListenerToView = s_listener != null && s_haveView ? Flat(listenerPos, s_view) : -1f;
            r.ListenerToWatched = s_listener != null && watched != null ? Flat(listenerPos, watched.Position) : -1f;

            CountSources(r, listenerPos);

            // State changes.
            string listenerText = s_listener == null ? "none" : PathOf(s_listener.transform) + "#" + s_listener.GetInstanceID();
            if (CoopTrace.Changed("audio.listener", listenerText + "|" + s_listenerCount))
                Line(CoopTrace.Events, "listener", "listener", listenerText, "count", s_listenerCount, "pos", listenerPos,
                    "to_view", r.ListenerToView, "cam_target", s_camTarget);
            if (CoopTrace.Changed("audio.pause", r.Paused ? "1" : "0"))
                Line(CoopTrace.Events, "pause", "paused", r.Paused, "timescale", Time.timeScale);
            if (CoopTrace.Changed("audio.volume", r.Volume.ToString("0.00")))
                Line(CoopTrace.Events, "volume", "volume", r.Volume);
            if (CoopTrace.Changed("audio.settings", settings))
                Line(CoopTrace.Events, "settings", "volumes", settings);
            bool focused = GameController.IsFocused;
            if (CoopTrace.Changed("audio.focus", focused ? "1" : "0"))
                Line(CoopTrace.Events, "focus", "focused", focused, "run_in_bg", Application.runInBackground);

            // Moments around which the sound went missing before: a full picture each time.
            int watchedSeat = -1;
            try { if (watched != null) watchedSeat = Revive.SpectateTarget; } catch (Exception) { }
            if (!s_seeded)
            {
                // The first reading is the baseline: no "spectate end" / "own up" snapshot at start-up.
                s_seeded = true;
                CoopTrace.Changed("audio.spectate", watchedSeat.ToString());
                CoopTrace.Changed("audio.down", OwnDown() ? "1" : "0");
            }
            if (CoopTrace.Changed("audio.spectate", watchedSeat.ToString()))
                Snapshot("snapshot", "why", watchedSeat < 0 ? "spectate.end" : "spectate", "watched", watchedSeat < 0 ? "-" : "P" + watchedSeat);
            bool ownDown = OwnDown();
            if (CoopTrace.Changed("audio.down", ownDown ? "1" : "0"))
                Snapshot("snapshot", "why", ownDown ? "own.down" : "own.up");
            string scene = "";
            try { if (Core.Scenes.Manager != null && Core.Scenes.Manager.CurrentScene != null) scene = Core.Scenes.Manager.CurrentScene.Scene ?? ""; }
            catch (Exception) { }
            s_scene = scene;
            if (scene.Length != 0 && CoopTrace.Changed("audio.scene", scene))
                Snapshot("snapshot", "why", "scene");

            // Does the sound look lost?
            string reasons = s_watch.Reasons(r, now);
            float duration;
            switch (s_watch.Next(reasons, now, out duration))
            {
                case AudioWatch.Step.Start:
                    Snapshot("lost.suspect", "reasons", reasons, "episode", s_watch.Episodes);
                    Plugin.Log.LogWarning("audio: sound may be lost (" + reasons + "): listener=" + listenerText
                        + " playing=" + r.Playing + " near_view=" + r.NearViewPlaying + "/" + r.NearViewQuiet + " quiet"
                        + " watching=" + (watchedSeat < 0 ? "-" : "P" + watchedSeat) + " (trace: audio lost.suspect)");
                    break;
                case AudioWatch.Step.Change:
                    Snapshot("lost.change", "reasons", reasons, "for_s", duration);
                    break;
                case AudioWatch.Step.Still:
                    Line(CoopTrace.Events, "lost.still", "reasons", reasons, "for_s", duration, "playing", r.Playing,
                        "near_view", r.NearViewPlaying, "near_view_quiet", r.NearViewQuiet, "to_view", r.ListenerToView);
                    break;
                case AudioWatch.Step.Clear:
                    Line(CoopTrace.Events, "lost.clear", "for_s", duration, "playing", r.Playing);
                    Plugin.Log.LogInfo("audio: sound looks normal again after " + duration.ToString("0") + " s");
                    break;
            }

            if (now >= s_nextHeartbeat)
            {
                s_nextHeartbeat = now + HeartbeatSeconds;
                Line(CoopTrace.Events, "heartbeat", "playing", r.Playing, "quiet", s_quiet, "listeners", s_listenerCount,
                    "paused", r.Paused, "volume", r.Volume, "to_view", r.ListenerToView, "settings", settings,
                    "suspect", reasons.Length == 0 ? "-" : reasons);
            }
            if (now >= s_nextSample && CoopTrace.On(Sub, CoopTrace.Samples))
            {
                s_nextSample = now + SampleSeconds;
                Snapshot("sample", "why", "periodic");
            }
        }

        // Every AudioSource that plays: how many, how many too quiet to hear, and the ones nearest to what the camera
        // shows (where the watched partner is). Out of range = beyond maxDistance from the listener with a linear or
        // custom rolloff (a logarithmic one stays at its far level instead of going silent).
        static void CountSources(AudioReading r, Vector3 listenerPos)
        {
            int playing = 0, quiet = 0, outOfRange = 0, nearPlaying = 0, nearQuiet = 0;
            List<KeyValuePair<float, string>> near = new List<KeyValuePair<float, string>>();
            foreach (UnityEngine.AudioSource src in UnityEngine.Object.FindObjectsOfType<UnityEngine.AudioSource>())
            {
                if (src == null || !src.isPlaying) continue;
                playing++;
                Vector3 pos = src.transform.position;
                float blend = 1f;
                if (s_spatialBlend != null) { try { blend = (float)s_spatialBlend.GetValue(src, null); } catch (Exception) { } }
                bool far = s_listener != null && blend > 0.5f && src.rolloffMode != UnityEngine.AudioRolloffMode.Logarithmic
                    && Vector3.Distance(listenerPos, pos) > src.maxDistance;
                bool isQuiet = src.mute || !src.enabled || src.volume < 0.02f || far;
                if (isQuiet) quiet++;
                if (far) outOfRange++;
                if (s_haveView && blend > 0.5f)
                {
                    float d = Flat(pos, s_view);
                    if (d <= AudioWatch.NearView)
                    {
                        nearPlaying++;
                        if (isQuiet) nearQuiet++;
                        if (near.Count < 32)
                            near.Add(new KeyValuePair<float, string>(d, AudioText.Sound(src.clip != null ? src.clip.name : src.name, d,
                                src.mute ? 0f : src.volume, src.maxDistance, src.rolloffMode.ToString())));
                    }
                }
            }
            near.Sort((a, b) => a.Key.CompareTo(b.Key));
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < near.Count && i < 4; i++) { if (sb.Length != 0) sb.Append(';'); sb.Append(near[i].Value); }
            s_nearest = sb.Length == 0 ? "-" : sb.ToString();
            r.Playing = playing; r.NearViewPlaying = nearPlaying; r.NearViewQuiet = nearQuiet;
            s_quiet = quiet; s_outOfRange = outOfRange;
            try { int n = 0; foreach (SoundPlayer sp in SoundPlayer.All) if (sp != null) n++; s_soundPlayers = n; } catch (Exception) { s_soundPlayers = -1; }
        }

        // The full picture: listener, volumes, focus, pause, who is down / watched, and the sounds near the view.
        static void Snapshot(string name, params object[] lead)
        {
            AudioReading r = s_r;
            List<object> kv = new List<object>(lead);
            float min; string minName;
            string settings = ReadSettings(out min, out minName);
            kv.AddRange(new object[] {
                "listener", s_listener == null ? "none" : PathOf(s_listener.transform), "listeners", s_listenerCount,
                "listener_pos", s_listener != null ? s_listener.transform.position : Vector3.zero,
                "paused", UnityEngine.AudioListener.pause, "volume", UnityEngine.AudioListener.volume, "settings", settings,
                "focused", GameController.IsFocused, "timescale", Time.timeScale, "in_game", NetPlay.NotInGameplayReason() ?? "yes",
                "scene", s_scene.Length == 0 ? "-" : s_scene, "cam_target", s_camTarget, "view", s_haveView ? (object)s_view : "-",
                "to_view", r.ListenerToView, "spectating", r.Spectating, "to_watched", r.ListenerToWatched,
                "playing", r.Playing, "quiet", s_quiet, "out_of_range", s_outOfRange, "sound_players", s_soundPlayers,
                "near_view", r.NearViewPlaying, "near_view_quiet", r.NearViewQuiet, "nearest", s_nearest,
                "dsp_advance", r.DspAdvance < 0 ? "-" : (object)(float)r.DspAdvance, "real_advance", r.RealAdvance,
                "sample_rate", UnityEngine.AudioSettings.outputSampleRate });
            SeinCharacter own = null;
            try { own = P2Context.P1 != null ? P2Context.P1 : Characters.Sein; } catch (Exception) { }
            if (own != null) kv.AddRange(new object[] { "own_pos", own.Position, "own_down", SafeDowned(own),
                "listener_to_own", s_listener != null ? Flat(s_listener.transform.position, own.Position) : -1f });
            foreach (Puppet p in Players.Remotes)
                if (p != null && p.Sein != null)
                    kv.AddRange(new object[] { "P" + p.Seat + "_pos", p.Sein.Position, "P" + p.Seat + "_down", SafeDowned(p.Sein),
                        "P" + p.Seat + "_active", p.Sein.gameObject.activeInHierarchy });
            Line(name == "sample" ? CoopTrace.Samples : CoopTrace.Events, name, kv.ToArray());
        }

        static bool OwnDown()
        {
            try { SeinCharacter own = P2Context.P1 != null ? P2Context.P1 : Characters.Sein; return own != null && Revive.IsDowned(own); }
            catch (Exception) { return false; }
        }

        static bool SafeDowned(SeinCharacter s) { try { return Revive.IsDowned(s); } catch (Exception) { return false; } }

        static void Line(int level, string name, params object[] kv)
        {
            if (!CoopTrace.On(Sub, level)) return;
            if (!s_budget.Take(Time.realtimeSinceStartup)) return;
            int dropped = s_budget.TakeSuppressed();
            if (dropped > 0) CoopTrace.At(CoopTrace.Events, Sub, "budget.dropped", new object[] { "lines", dropped });
            CoopTrace.At(level, Sub, name, kv);
        }

        // Distance in the level's plane (the camera and its listener sit at another depth than the characters).
        static float Flat(Vector3 a, Vector3 b) { return new Vector2(a.x - b.x, a.y - b.y).magnitude; }

        static string PathOf(Transform t)
        {
            if (t == null) return "-";
            string path = t.name;
            int depth = 0;
            for (Transform p = t.parent; p != null && depth < 4; p = p.parent, depth++) path = p.name + "/" + path;
            return path;
        }
    }
}
