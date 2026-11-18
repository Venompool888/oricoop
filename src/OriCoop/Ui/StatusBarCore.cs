using System;

namespace OriCoop.Ui
{
    // T-061: the numbers behind one teammate's copy of the game's bottom HUD, with no Unity in it (unit-tested).
    //
    // The game's HUD reads Characters.Sein through small FloatValueProvider components (SeinHealthVisualMinProvider,
    // SeinMaxEnergyValueProvider, ...). A teammate's copy of that HUD asks the same providers, and BottomHud answers
    // them from this model instead of from P1. Health and energy smooth the way the game does it
    // (SeinHealthController / SeinEnergy): a loss sets the low edge at once and the high edge drains at 4 (health) or
    // 1 (energy) per second; a gain sets the high edge at once and the low edge follows.
    // Spirit light (the ring around the centre orb), ability points, level, keystones and map stones are one team pool
    // (ProgressSync / SharedProgress), so the copies keep showing P1's values for those: Provide returns null there.
    public enum StatKind
    {
        None,
        MaxHealth, HealthVisualMin, HealthVisualMax, HealthVisualMinNormalized, HealthVisualMaxNormalized,
        MaxEnergy, EnergyVisualMin, EnergyVisualMax, EnergyVisualMinNormalized, EnergyVisualMaxNormalized,
        SoulFlame, Breath, ChargeJump,
    }

    public struct VitalsSample
    {
        public float Health, Energy, MaxEnergy, SoulFlame;
        public int MaxHealth;
        public bool Downed;

        public static bool SameDisplay(VitalsSample a, VitalsSample b)
        {
            return Math.Abs(a.Health - b.Health) < 0.01f && a.MaxHealth == b.MaxHealth && Math.Abs(a.Energy - b.Energy) < 0.01f
                && Math.Abs(a.MaxEnergy - b.MaxEnergy) < 0.01f && Math.Abs(a.SoulFlame - b.SoulFlame) < 0.02f && a.Downed == b.Downed;
        }
    }

    public sealed class VitalsModel
    {
        public const float HealthRate = 4f, EnergyRate = 1f;
        public VitalsSample Now;
        public float HealthMin, HealthMax, EnergyMin, EnergyMax;
        bool m_have;

        public bool Have { get { return m_have; } }

        public void Set(VitalsSample s)
        {
            if (s.MaxHealth < 0) s.MaxHealth = 0;
            s.Health = Clamp(s.Health, 0f, s.MaxHealth);
            if (s.MaxEnergy < 0f) s.MaxEnergy = 0f;
            s.Energy = Clamp(s.Energy, 0f, s.MaxEnergy);
            s.SoulFlame = Clamp(s.SoulFlame, 0f, 1f);
            if (!m_have)
            {
                HealthMin = HealthMax = s.Health; EnergyMin = EnergyMax = s.Energy;
            }
            else
            {
                if (s.Health < Now.Health) HealthMin = s.Health; else if (s.Health > Now.Health) HealthMax = s.Health;
                if (s.Energy < Now.Energy) EnergyMin = s.Energy; else if (s.Energy > Now.Energy) EnergyMax = s.Energy;
                // A revive or checkpoint can lower the maximum: never draw past it.
                HealthMin = Math.Min(HealthMin, s.MaxHealth); HealthMax = Math.Min(HealthMax, s.MaxHealth);
                EnergyMin = Math.Min(EnergyMin, s.MaxEnergy); EnergyMax = Math.Min(EnergyMax, s.MaxEnergy);
            }
            Now = s;
            m_have = true;
        }

        public void Tick(float dt)
        {
            if (!m_have || dt <= 0f) return;
            HealthMin = Toward(HealthMin, Now.Health, dt * HealthRate); HealthMax = Toward(HealthMax, Now.Health, dt * HealthRate);
            EnergyMin = Toward(EnergyMin, Now.Energy, dt * EnergyRate); EnergyMax = Toward(EnergyMax, Now.Energy, dt * EnergyRate);
        }

        // The value the game's provider of this kind would return for this player; null = not per player (use P1's).
        public float? Provide(StatKind kind, float divideBy)
        {
            if (!m_have) return null;
            if (divideBy == 0f) divideBy = 1f;
            switch (kind)
            {
                case StatKind.MaxHealth: return Now.MaxHealth / divideBy;
                case StatKind.HealthVisualMin: return HealthMin / divideBy;
                case StatKind.HealthVisualMax: return HealthMax / divideBy;
                case StatKind.HealthVisualMinNormalized: return Ratio(HealthMin, Now.MaxHealth);
                case StatKind.HealthVisualMaxNormalized: return Ratio(HealthMax, Now.MaxHealth);
                case StatKind.MaxEnergy: return Now.MaxEnergy / divideBy;
                case StatKind.EnergyVisualMin: return EnergyMin / divideBy;
                case StatKind.EnergyVisualMax: return EnergyMax / divideBy;
                case StatKind.EnergyVisualMinNormalized: return Ratio(EnergyMin, Now.MaxEnergy);
                case StatKind.EnergyVisualMaxNormalized: return Ratio(EnergyMax, Now.MaxEnergy);
                case StatKind.SoulFlame: return Now.SoulFlame;
                case StatKind.Breath: return 1f;       // not sent: a teammate's copy shows a full breath bar
                case StatKind.ChargeJump: return 0f;
                default: return null;
            }
        }

        // Game type name of a FloatValueProvider -> what it reads. Spirit light, ability points, level, keystones and
        // map stones are shared, so they map to None.
        public static StatKind KindOf(string typeName)
        {
            switch (typeName)
            {
                case "SeinMaxHealthValueProvider": return StatKind.MaxHealth;
                case "SeinHealthVisualMinProvider": return StatKind.HealthVisualMin;
                case "SeinHealthVisualMaxProvider": return StatKind.HealthVisualMax;
                case "SeinHealthVisualMinNormalizedProvider": return StatKind.HealthVisualMinNormalized;
                case "SeinHealthVisualMaxNormalizedProvider": return StatKind.HealthVisualMaxNormalized;
                case "SeinMaxEnergyValueProvider": return StatKind.MaxEnergy;
                case "SeinEnergyMinVisualProvider": return StatKind.EnergyVisualMin;
                case "SeinEnergyMaxVisualProvider": return StatKind.EnergyVisualMax;
                case "SeinEnergyMinVisualNormalizedProvider": return StatKind.EnergyVisualMinNormalized;
                case "SeinEnergyMaxVisualNormalizedProvider": return StatKind.EnergyVisualMaxNormalized;
                case "SeinSoulFlameCooldownValueProvider": return StatKind.SoulFlame;
                case "SeinBreathRemainingValueProvider": return StatKind.Breath;
                case "ChargeJumpChargeFloatProvider": return StatKind.ChargeJump;
                default: return StatKind.None;
            }
        }

        // Wire format (Vitals, msg 79): seat, health, max health, energy, max energy, soul flame bar, downed.
        public static void Write(System.IO.BinaryWriter w, int seat, VitalsSample s)
        {
            w.Write((byte)seat); w.Write(s.Health); w.Write((short)s.MaxHealth); w.Write(s.Energy); w.Write(s.MaxEnergy);
            w.Write((byte)Math.Round(Clamp(s.SoulFlame, 0f, 1f) * 255f)); w.Write((byte)(s.Downed ? 1 : 0));
        }

        public static VitalsSample Read(System.IO.BinaryReader r, out int seat)
        {
            seat = r.ReadByte();
            VitalsSample s = new VitalsSample();
            s.Health = r.ReadSingle(); s.MaxHealth = r.ReadInt16(); s.Energy = r.ReadSingle(); s.MaxEnergy = r.ReadSingle();
            s.SoulFlame = r.ReadByte() / 255f; s.Downed = (r.ReadByte() & 1) != 0;
            return s;
        }

        static float Ratio(float a, float max) { return max <= 0f ? 0f : a / max; }
        static float Clamp(float v, float lo, float hi) { return v < lo ? lo : v > hi ? hi : v; }
        static float Toward(float v, float target, float step)
        {
            if (Math.Abs(target - v) <= step) return target;
            return v + Math.Sign(target - v) * step;
        }
    }

    // When to send our own vitals: at once when what a teammate's bar shows changes (at most every MinInterval),
    // and a heartbeat every Heartbeat seconds so a late joiner or a lost packet catches up.
    public sealed class VitalsSender
    {
        public const float MinInterval = 0.1f, Heartbeat = 1f;
        VitalsSample m_last;
        float m_lastAt = float.NegativeInfinity;
        bool m_sent;

        public bool Due(VitalsSample now, float time)
        {
            if (!m_sent) return true;
            float since = time - m_lastAt;
            if (since < 0f) return true; // clock went back (new session): resend
            if (since >= Heartbeat - 0.001f) return true;
            return since >= MinInterval - 0.001f && !VitalsSample.SameDisplay(now, m_last); // float clock: 5.1f - 5f < 0.1f
        }

        public void MarkSent(VitalsSample s, float time) { m_last = s; m_lastAt = time; m_sent = true; }
        public void Reset() { m_sent = false; m_lastAt = float.NegativeInfinity; }
    }

    // Where each copy goes, in screen fractions (0..1, origin bottom-left), so 1080p and 4K at 16:9 lay out the same.
    // The game's own HUD stays centred at the bottom. Teammates' copies are smaller and sit in the free space beside
    // it: P2 (order 0) left of it, P3 (order 1) right of it, P4 (order 2) above P2, never over the native HUD or each
    // other. The space above each copy holds its "P2" label. Native extents are measured at runtime.
    public struct SlotRect
    {
        public float CenterX, Bottom, Width, Height, Scale;
        public float Left { get { return CenterX - Width / 2f; } }
        public float Right { get { return CenterX + Width / 2f; } }
        public float Top { get { return Bottom + Height; } }
    }

    public static class StatusBarLayout
    {
        public const float Gap = 0.012f, Edge = 0.01f, MaxScale = 0.6f, MinScale = 0.2f, LabelSpace = 0.045f;

        public static SlotRect Place(int order, int count, float nativeLeft, float nativeRight, float nativeBottom, float nativeHeight)
        {
            float nativeW = Math.Max(0.01f, nativeRight - nativeLeft);
            float leftFree = Math.Max(0f, nativeLeft - Edge - Gap), rightFree = Math.Max(0f, 1f - Edge - nativeRight - Gap);
            float scale = Clamp(Math.Min(leftFree, rightFree) / nativeW, MinScale, MaxScale);
            SlotRect r = new SlotRect();
            r.Scale = scale; r.Width = nativeW * scale; r.Height = Math.Max(0f, nativeHeight) * scale;
            bool right = order % 2 == 1;
            int row = order / 2;
            r.CenterX = right ? nativeRight + Gap + r.Width / 2f : nativeLeft - Gap - r.Width / 2f;
            r.Bottom = nativeBottom + row * (r.Height + LabelSpace + Gap);
            return r;
        }

        public static bool Overlaps(SlotRect a, SlotRect b)
        {
            // The label above each copy counts as part of it.
            return a.Left < b.Right && b.Left < a.Right && a.Bottom < b.Top + LabelSpace && b.Bottom < a.Top + LabelSpace;
        }

        public static bool OverlapsNative(SlotRect a, float nativeLeft, float nativeRight, float nativeBottom, float nativeHeight)
        {
            return a.Left < nativeRight && nativeLeft < a.Right && a.Bottom < nativeBottom + nativeHeight && nativeBottom < a.Top + LabelSpace;
        }

        static float Clamp(float v, float lo, float hi) { return v < lo ? lo : v > hi ? hi : v; }
    }
}
