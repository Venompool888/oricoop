using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game;
using HarmonyLib;
using OriCoop.Seams;
using OriCoop.Story;
using UnityEngine;

namespace OriCoop
{
    public static class CutsceneAnimSync
    {
        const byte Message = 90;
        public static readonly bool Off = Environment.GetEnvironmentVariable("ORICOOP_ANIMSYNC_OFF") == "1";
        static readonly AnimFollow Follow = new AnimFollow();
        static readonly Dictionary<string, TextureAnimationWithTransitions> Animations = new Dictionary<string, TextureAnimationWithTransitions>();
        static readonly FieldInfo AnimationField = AccessTools.Field(AccessTools.TypeByName("PlayCharacterAnimation"), "Animation");
        static bool s_applying;
        static string s_localAnim;
        public static int Sent, Received, PuppetPlays, PuppetStops, OwnPlays, OwnStops;
        public static string LastAnim;
        static bool Online { get { return !Off && Services.Net.Enabled && Services.Net.Connected && !P2Context.Inside; } }

        public static void Init() { Services.Net.On(Message, Receive); }
        static string AnimationKey(TextureAnimationWithTransitions anim)
        {
            if (anim.Animation == null || anim.Animation.FrameGuids.Count == 0) return AnimKey.Make(anim.name, null, 0);
            return AnimKey.Make(anim.name, anim.Animation.FrameGuids[0].ToString(), anim.Animation.FrameGuids.Count);
        }
        static TextureAnimationWithTransitions Animation(string name)
        {
            TextureAnimationWithTransitions result;
            if (Animations.TryGetValue(name, out result) && result != null) return result;
            Animations.Remove(name);
            foreach (UnityEngine.Object obj in Resources.FindObjectsOfTypeAll(typeof(TextureAnimationWithTransitions)))
            {
                TextureAnimationWithTransitions anim = obj as TextureAnimationWithTransitions;
                if (anim != null && !Animations.ContainsKey(AnimationKey(anim))) Animations.Add(AnimationKey(anim), anim);
            }
            Animations.TryGetValue(name, out result); return result;
        }
        static void Send(byte op, byte flags, string anim)
        {
            Services.Net.Send(Message, true, delegate(BinaryWriter w) { AnimWire.Write(w, (byte)Players.LocalSeat, op, flags, anim); });
            Sent++; LastAnim = anim;
        }
        static void ApplyPuppet(Puppet puppet, byte op, TextureAnimationWithTransitions anim)
        {
            if (puppet.Sein == null) return;
            P2Context.Enter(puppet);
            try
            {
                if (op == 0) { puppet.Sein.Controller.PlayAnimation(anim); PuppetPlays++; }
                else { puppet.Sein.Controller.StopAnimation(); PuppetStops++; }
            }
            finally { P2Context.Exit(); }
        }
        // T-121: these run inside the game's own story actions (Harmony postfixes): never let an error escape into them.
        static void Guarded(string what, Action action)
        {
            try { action(); }
            catch (Exception e) { Errors++; Services.Log.Warn("cutscene animation " + what + " failed: " + e.Message); }
        }
        public static int Errors;
        static void PlayLocal(object instance)
        {
            if (!Online || s_applying || StorySync.LocalWellSave || Characters.Sein == null || !Characters.Sein.Controller.IsPlayingAnimation) return;
            TextureAnimationWithTransitions anim = (TextureAnimationWithTransitions)AnimationField.GetValue(instance);
            s_localAnim = AnimationKey(anim); Animations[s_localAnim] = anim;
            Send(0, (byte)(StorySync.StoryActive ? 0 : 1), s_localAnim);
            foreach (Puppet puppet in Players.Remotes) ApplyPuppet(puppet, 0, anim);
        }
        static void StopLocal()
        {
            if (!Online || s_applying || StorySync.LocalWellSave || Characters.Sein == null) return;
            if (s_localAnim != null) Send(1, 0, s_localAnim);
            foreach (Puppet puppet in Players.Remotes) ApplyPuppet(puppet, 1, null);
        }
        static void StopOwn()
        {
            if (Characters.Sein == null) return;
            s_applying = true;
            try { Characters.Sein.Controller.StopAnimation(); OwnStops++; }
            finally { s_applying = false; }
        }
        static void Receive(BinaryReader r)
        {
            byte seat, op, flags; string name;
            if (!Online || !AnimWire.TryRead(r, out seat, out op, out flags, out name)) return;
            StoryHost.Current.Rebroadcast(r);
            if (seat == Players.LocalSeat) return;
            Received++; LastAnim = name;
            TextureAnimationWithTransitions anim = op == 0 ? Animation(name) : null;
            if (op == 0 && anim == null) return;
            foreach (Puppet puppet in Players.Remotes) if (puppet.Seat == seat) ApplyPuppet(puppet, op, anim);
            if (op == 1)
            {
                if (Follow.Stop(seat)) StopOwn();
            }
            else if ((flags & 1) != 0 && Characters.Sein != null && NetPlay.LocalInGameplay()
                && !SessionRules.Respawning && !Characters.Sein.Controller.IsPlayingAnimation)
            {
                s_applying = true;
                try
                {
                    Characters.Sein.Controller.PlayAnimation(anim); OwnPlays++;
                    Follow.Start(seat, name, Time.realtimeSinceStartup);
                }
                finally { s_applying = false; }
            }
        }
        public static void Tick()
        {
            if (Online && Follow.Expired(Time.realtimeSinceStartup)) StopOwn();
        }
        [HarmonyPatch]
        static class Play
        {
            static MethodBase TargetMethod() { return AccessTools.Method(AccessTools.TypeByName("PlayCharacterAnimation"), "Perform"); }
            static void Postfix(object __instance) { Guarded("play", delegate { PlayLocal(__instance); }); }
        }
        [HarmonyPatch]
        static class Stop
        {
            static MethodBase TargetMethod() { return AccessTools.Method(AccessTools.TypeByName("PlayCharacterAnimation"), "Stop"); }
            static void Postfix() { Guarded("stop", StopLocal); }
        }
        [HarmonyPatch]
        static class StopAction
        {
            static MethodBase TargetMethod() { return AccessTools.Method(AccessTools.TypeByName("StopCharacterAnimation"), "Perform"); }
            static void Postfix() { Guarded("stop", StopLocal); }
        }
    }
}
