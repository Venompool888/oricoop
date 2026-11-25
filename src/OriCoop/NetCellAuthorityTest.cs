using System;
using System.Collections;
using UnityEngine;

namespace OriCoop
{
    internal static class NetCellAuthorityTest
    {
        public class Presentation : ActionMethod
        {
            public ActionMethod Native;
            public int Calls;
            public override void Perform(IContext context) { Calls++; if (Native != null) Native.Perform(context); }
        }

        static void Take(SeinPickupProcessor collector, bool energy, int id)
        {
            GameObject go = new GameObject("W cell contest"); go.SetActive(false);
            try
            {
                go.transform.position = new Vector3(9100f + id, 9010f, 0f);
                PickupBase cell = energy ? (PickupBase)go.AddComponent<MaxEnergyContainerPickup>() : go.AddComponent<MaxHealthContainerPickup>();
                cell.MoonGuid = new MoonGuid(0x54323233, 0x43454c4c, id, 1); cell.OnValidate();
                if (energy) { ((MaxEnergyContainerPickup)cell).Amount = 1; collector.OnCollectMaxEnergyContainerPickup((MaxEnergyContainerPickup)cell); }
                else collector.OnCollectMaxHealthContainerPickup((MaxHealthContainerPickup)cell);
            }
            finally { UnityEngine.Object.Destroy(go); }
        }

        public static IEnumerator Run(TestHarness h, Func<int, IEnumerator> barrier, Action dismiss)
        {
            bool host = NetPlay.IsHost;
            SeinCharacter own = P2Context.P1;
            SeinPickupProcessor collector = ProgressSync.PickupCollector(own);
            GameObject record = new GameObject("W native container presentation");
            Presentation health = record.AddComponent<Presentation>(), energy = record.AddComponent<Presentation>();
            ActionMethod healthNative = collector.HeartContainerSequence, energyNative = collector.EnergyContainerSequence;
            int maxHealth = own.Mortality.Health.MaxHealth; float maxEnergy = own.Energy.Max;
            float hp = own.Mortality.Health.Amount, en = own.Energy.Current;
            try
            {
                // Both contenders have this GUID. Hold the existing host queue until both requests have been made,
                // just as it waits during a scene change for the local collector to bind to Sein.
                collector.HeartContainerSequence = health; collector.EnergyContainerSequence = energy;
                float hurt = Mathf.Max(1f, maxHealth - 4f);
                own.Mortality.Health.SetAmount(hurt);
                if (host) collector.Sein = null;
                yield return barrier(22310);
                if (!host) Take(collector, false, 1);
                yield return barrier(22311);
                if (host) { collector.Sein = own; Take(collector, false, 1); }
                float until = Time.realtimeSinceStartup + 4f;
                while (own.Mortality.Health.MaxHealth == maxHealth && Time.realtimeSinceStartup < until) yield return null;
                yield return barrier(22312);
                h.Check(own.Mortality.Health.MaxHealth == maxHealth + 4, "W cell contest: both touches raised the shared maximum exactly once");
                h.Check(own.Mortality.Health.Amount == (host ? maxHealth + 4 : hurt),
                    "W cell contest: only the host who won refilled (health " + own.Mortality.Health.Amount + ")");

                float spent = Mathf.Max(0f, maxEnergy - 1f);
                own.Energy.Current = spent;
                energy.Native = energyNative;
                if (host) collector.Sein = null;
                yield return barrier(22313);
                if (!host) Take(collector, true, 2);
                yield return barrier(22314);
                until = Time.realtimeSinceStartup + 5.7f;
                while (Time.realtimeSinceStartup < until) yield return null;
                if (host) collector.Sein = own;
                until = Time.realtimeSinceStartup + 4f;
                bool modal = false;
                while (Time.realtimeSinceStartup < until)
                {
                    if (SessionRules.PopupOpen || GameController.Instance.LockInputByAction) { modal = true; dismiss(); }
                    yield return null;
                }
                h.Check(own.Energy.Max == maxEnergy + 1f && own.Energy.Current == (host ? spent : own.Energy.Max),
                    "W cell contest: delayed guest winner alone refills (energy " + own.Energy.Current + "/" + own.Energy.Max + ")");
                if (host) h.Check(energy.Calls == 0 && !modal, "W cell contest: a guest energy cell never runs the host pickup pose or message (calls " + energy.Calls + ", modal " + modal + ")");
                else h.Check(energy.Calls == 1, "W cell contest: the winning guest plays its own container presentation once");
                yield return barrier(22315);
            }
            finally
            {
                collector.Sein = own; collector.HeartContainerSequence = healthNative; collector.EnergyContainerSequence = energyNative;
                own.Mortality.Health.MaxHealth = maxHealth; own.Mortality.Health.SetAmount(hp); own.Energy.Max = maxEnergy; own.Energy.Current = en;
                dismiss(); UnityEngine.Object.Destroy(record);
            }
        }
    }
}
