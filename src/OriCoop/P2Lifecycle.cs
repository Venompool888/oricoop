using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // Keeps P2 out of the game's save system and makes P2's ability components match P1's abilities.
    public static class P2Lifecycle
    {
        // True while the P2 prefab is being instantiated (its Awake registers nested prefabs before we know it is P2).
        public static bool SpawningP2;
        public static int BlockedSaveRegistrations;

        static bool BelongsToP2(object o)
        {
            if (SpawningP2) return true;
            Component c = o as Component;
            if (c == null || P2Context.P2 == null) return false;
            return P2Context.BelongsToP2(c);
        }

        // The game registers every SaveSerialize under a fixed GUID and drops any older entry with the same GUID.
        // P2's components carry the same GUIDs as P1's, so registering them would silently replace P1's entries
        // in checkpoints. P2 is never saved.
        [HarmonyPatch(typeof(SaveSceneManager), "AddSaveObject")]
        static class SaveRegistrationPatch
        {
            static bool Prefix(ISerializable saveSerialize)
            {
                if (!BelongsToP2(saveSerialize)) return true;
                BlockedSaveRegistrations++;
                return false;
            }
        }

        // After P2's ability flags change, let the game add/remove the matching ability components (double jump,
        // bash, glide...), as it does for P1 when an ability is learned. Runs in P2 context so the new components
        // see P2 as "the" Sein while they initialise, then their types get the P2 context wrapping.
        public static void EnsureAbilityPrefabs(SeinCharacter p2)
        {
            if (p2 == null || p2.Prefabs == null) return;
            Puppet puppet = Players.OfSein(p2);
            P2Context.Enter(puppet);
            try { p2.Prefabs.EnsureRightPrefabsAreThereForAbilities(); }
            finally { P2Context.Exit(); }
            P2Context.PatchTypesOn(p2.gameObject, Plugin.H);
            P2Context.RefreshIds(puppet);
        }

        // A carried world object (stone, Nightberry, torch...) is parented to the carrier; drop it before P2 is
        // destroyed so it isn't destroyed with P2.
        public static void DropCarried(SeinCharacter p2)
        {
            if (p2 == null || p2.Abilities == null || p2.Abilities.Carry == null) return;
            ICarryable c = p2.Abilities.Carry.CurrentCarryable;
            if (c == null) return;
            P2Context.Enter(Players.OfSein(p2));
            try { c.Drop(); }
            catch (System.Exception e) { Plugin.Log.LogWarning("drop on despawn: " + e.Message); }
            finally { P2Context.Exit(); }
            Component comp = c as Component;
            if (comp != null && comp.transform.IsChildOf(p2.transform)) comp.transform.parent = null;
        }
    }
}
