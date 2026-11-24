using System;
using System.Collections.Generic;

namespace OriCoop
{
    public static partial class L10n
    {
        static L10n()
        {
            LanguageCode = ReadLanguage;
            EnsureHook = Hook;
            Warning = delegate(string message) { Plugin.Log.LogWarning(message); };
        }

        static string ReadLanguage()
        {
            try { return GameSettings.Instance != null && GameSettings.Instance.Language == Language.Chinese ? "zh" : "en"; }
            catch (Exception) { return "en"; }
        }

        public static event Action Changed;

        public static void Hook()
        {
            if (s_hooked) return;
            try
            {
                Game.Events.Scheduler.OnGameLanguageChange.Add(OnLanguageChange);
                s_hooked = true;
            }
            catch (Exception) { }
        }

        static void OnLanguageChange()
        {
            Action a = Changed;
            if (a == null) return;
            foreach (Action d in a.GetInvocationList())
            {
                try { ((Action)d)(); }
                catch (Exception e) { Plugin.Log.LogWarning("l10n: language change handler: " + e.Message); }
            }
        }

        internal static string SessionText(string key) { return T(ModeKey(key, NetPlay.UsesCServer)); }
        internal static string SessionFormat(string key, params object[] args) { return F(ModeKey(key, NetPlay.UsesCServer), args); }

        public static MessageProvider Provider(string key) { return Provider(delegate { return T(key); }); }

        public static MessageProvider Provider(Func<string> text)
        {
            L10nProvider p = UnityEngine.ScriptableObject.CreateInstance<L10nProvider>();
            p.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
            p.Text = text;
            return p;
        }

        public static bool Set(MessageBox box, string key)
        {
            if (box == null) return false;
            return Set(delegate(Func<string> text) { box.SetMessageProvider(Provider(text)); }, key);
        }

        public static bool Set(MessageBox box, Func<string> text)
        {
            if (box == null) return false;
            return Set(delegate(Func<string> value) { box.SetMessageProvider(Provider(value)); }, text);
        }
    }

    public class L10nProvider : MessageProvider
    {
        public Func<string> Text;

        public override IEnumerable<MessageDescriptor> GetMessages()
        {
            string s;
            try { s = Text != null ? Text() : ""; }
            catch (Exception) { s = ""; }
            return new[] { new MessageDescriptor(s ?? "") };
        }
    }
}
