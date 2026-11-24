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
            // A value that is not a number (msg 79 from a broken sender) keeps the last one: NaN would stick in the edges
            // for good and Tick's Math.Sign throws on it every frame.
            s.Health = Finite(s.Health, m_have ? Now.Health : 0f);
            s.Energy = Finite(s.Energy, m_have ? Now.Energy : 0f);
            s.MaxEnergy = Finite(s.MaxEnergy, m_have ? Now.MaxEnergy : 0f);
            s.SoulFlame = Finite(s.SoulFlame, m_have ? Now.SoulFlame : 0f);
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

        static float Finite(float v, float fallback) { return float.IsNaN(v) || float.IsInfinity(v) ? fallback : v; }
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
    // The game's own HUD stays centred at the bottom. The teammates' copies are stacked in the bottom-left corner,
    // P2 (order 0) on the native HUD's bottom line, P3 above it, P4 above P3, each at up to 70 % of the native size
    // and with its "P2" label to the left of its cells. A strip's meshes always span 15 cells on each side of the orb,
    // but only as many cells as the player's maximum are drawn, so what must not collide is the VISIBLE part (orb
    // and cells), measured from the cell counts. Native extents are measured at runtime.
    public struct SlotRect
    {
        // The whole copy including its empty trail (may stick out past the screen edge: nothing is drawn there).
        public float CenterX, Bottom, Width, Height, Scale;
        // The part with cells and orb, and where the label's right edge is.
        public float VisLeft, VisRight, LabelRight;
        public float Left { get { return CenterX - Width / 2f; } }
        public float Right { get { return CenterX + Width / 2f; } }
        public float Top { get { return Bottom + Height; } }
        // What a player sees of this copy: label, cells and orb.
        public float SeenLeft { get { return LabelRight - StatusBarLayout.LabelWidth; } }
    }

    public static class StatusBarLayout
    {
        public const float Gap = 0.008f, Edge = 0.012f, RowGap = 0.006f, MaxScale = 0.7f, MinScale = 0.12f;
        public const float LabelWidth = 0.035f, LabelGap = 0.004f;
        public const float MaxCells = 15f;

        // How far the cells and glow reach from the orb's centre, as a share of the strip's width, for this many
        // cells (measured at 960x540: 30 px orb and glow, 15.2 px per cell, of a 516 px strip).
        public static float Reach(float cells)
        {
            return Clamp((38f + 15.2f * Clamp(cells, 0f, MaxCells)) / 516f, 0.07f, 0.5f);
        }

        // The native strip's visible span: energy cells to the left of its centre, health cells to the right.
        public static void NativeSeen(float nativeLeft, float nativeRight, float energyCells, float healthCells, out float seenLeft, out float seenRight)
        {
            float w = Math.Max(0.01f, nativeRight - nativeLeft), c = (nativeLeft + nativeRight) / 2f;
            seenLeft = c - w * Reach(energyCells); seenRight = c + w * Reach(healthCells);
        }

        // copyEnergyCells / copyHealthCells: the most cells any copy shows, so all copies share one scale and one column.
        public static SlotRect Place(int order, float nativeLeft, float nativeRight, float nativeBottom, float nativeHeight,
            float nativeEnergyCells, float copyEnergyCells, float copyHealthCells)
        {
            float nativeW = Math.Max(0.01f, nativeRight - nativeLeft);
            float seenL, seenR;
            NativeSeen(nativeLeft, nativeRight, nativeEnergyCells, 4f, out seenL, out seenR);
            float reachL = Reach(copyEnergyCells), reachR = Reach(copyHealthCells);
            float free = seenL - Gap - Edge - LabelWidth - LabelGap;
            float scale = Clamp(free / (nativeW * (reachL + reachR)), MinScale, MaxScale);
            SlotRect r = new SlotRect();
            r.Scale = scale; r.Width = nativeW * scale; r.Height = Math.Max(0f, nativeHeight) * scale;
            r.VisLeft = Edge + LabelWidth + LabelGap;
            r.CenterX = r.VisLeft + r.Width * reachL;
            r.VisRight = r.CenterX + r.Width * reachR;
            r.LabelRight = r.VisLeft - LabelGap;
            r.Bottom = nativeBottom + order * (r.Height + RowGap);
            return r;
        }

        public static bool Overlaps(SlotRect a, SlotRect b)
        {
            return a.SeenLeft < b.VisRight && b.SeenLeft < a.VisRight && a.Bottom < b.Top && b.Bottom < a.Top;
        }

        // nativeHealthCells matters for the right half only: copies sit on the left, so it is not needed here.
        public static bool OverlapsNative(SlotRect a, float nativeLeft, float nativeRight, float nativeBottom, float nativeHeight, float nativeEnergyCells)
        {
            float seenL, seenR;
            NativeSeen(nativeLeft, nativeRight, nativeEnergyCells, 4f, out seenL, out seenR);
            return a.VisRight > seenL && a.SeenLeft < seenR && a.Bottom < nativeBottom + nativeHeight && nativeBottom < a.Top;
        }

        // NaN-safe: anything that is not a number comes out as the lower bound (a layout must never turn into NaN).
        static float Clamp(float v, float lo, float hi) { return !(v >= lo) ? lo : v > hi ? hi : v; }
    }
}
