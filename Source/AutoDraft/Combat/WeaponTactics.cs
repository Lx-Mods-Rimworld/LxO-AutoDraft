using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoDraft.Combat
{
    public enum SwapAction { KeepCurrent, SwapToRanged, SwapToMelee }

    /// <summary>
    /// Extracts weapon swap logic into a clean module. Only active when SmartGear
    /// is NOT loaded -- SmartGear handles its own weapon management.
    /// </summary>
    public static class WeaponTactics
    {
        /// <summary>
        /// Decide whether to swap weapons based on tactical situation.
        /// Returns KeepCurrent immediately if SmartGear is loaded.
        /// </summary>
        public static SwapAction Evaluate(Pawn soldier, EnemyInfo enemy, float dist, int combatLevel)
        {
            if (ModIntegration.IsSmartGearLoaded) return SwapAction.KeepCurrent;

            bool currentIsRanged = soldier.equipment != null
                && soldier.equipment.Primary != null
                && soldier.equipment.Primary.def.IsRangedWeapon;

            ThingWithComps invRanged = FindRangedInInventory(soldier);
            ThingWithComps invMelee = FindMeleeInInventory(soldier);
            bool hasRangedSidearm = invRanged != null;
            bool hasMeleeSidearm = invMelee != null;

            // Primary is melee (or unarmed), has ranged sidearm in inventory
            if (!currentIsRanged && hasRangedSidearm)
            {
                float sidearmRange = 0f;
                List<VerbProperties> verbs = invRanged.def.Verbs;
                if (verbs != null && verbs.Count > 0)
                    sidearmRange = verbs[0].range;

                // Enemy has melee, approaching: shoot while they run at us
                if (enemy.isMelee && dist > 5f && dist <= sidearmRange)
                    return SwapAction.SwapToRanged;

                // Enemy has ranged, far: shoot back
                if (enemy.isRanged && dist > 5f && dist <= sidearmRange)
                    return SwapAction.SwapToRanged;

                // Enemy ranged, close: charge with melee (ranged weak at close range)
                if (enemy.isRanged && dist <= 5f)
                    return SwapAction.KeepCurrent; // keep melee

                // Close: stay melee
                if (dist <= 5f)
                    return SwapAction.KeepCurrent;
            }

            // Primary is ranged, has melee sidearm, enemy very close
            if (currentIsRanged && hasMeleeSidearm && dist <= 3f)
                return SwapAction.SwapToMelee;

            return SwapAction.KeepCurrent;
        }

        /// <summary>
        /// Find the best ranged weapon in the pawn's inventory.
        /// Returns null if none found.
        /// </summary>
        public static ThingWithComps FindRangedInInventory(Pawn pawn)
        {
            if (pawn.inventory == null) return null;

            ThingWithComps best = null;
            float bestRange = 0f;

            foreach (Thing item in pawn.inventory.innerContainer)
            {
                if (item.def.IsRangedWeapon && item is ThingWithComps twc)
                {
                    List<VerbProperties> verbs = item.def.Verbs;
                    float range = (verbs != null && verbs.Count > 0) ? verbs[0].range : 0f;
                    if (range > bestRange)
                    {
                        bestRange = range;
                        best = twc;
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// Find a melee weapon in the pawn's inventory.
        /// Returns null if none found.
        /// </summary>
        public static ThingWithComps FindMeleeInInventory(Pawn pawn)
        {
            if (pawn.inventory == null) return null;

            foreach (Thing item in pawn.inventory.innerContainer)
            {
                if (item.def.IsMeleeWeapon && item is ThingWithComps twc)
                    return twc;
            }
            return null;
        }

        /// <summary>
        /// Swap current weapon to inventory, equip the given ranged weapon from inventory.
        /// </summary>
        public static void SwapToRanged(Pawn pawn, ThingWithComps rangedWeapon)
        {
            if (rangedWeapon == null) return;
            try
            {
                ThingWithComps melee = pawn.equipment != null ? pawn.equipment.Primary : null;
                if (melee != null)
                {
                    pawn.equipment.TryTransferEquipmentToContainer(melee, pawn.inventory.innerContainer);
                }
                pawn.inventory.innerContainer.Remove(rangedWeapon);
                pawn.equipment.AddEquipment(rangedWeapon);
                GarrisonDebug.Log("[Garrison] " + pawn.LabelShort + " swapped to ranged: " + rangedWeapon.def.defName);
            }
            catch (Exception ex)
            {
                GarrisonDebug.Log("[Garrison] SwapToRanged failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Swap ranged weapon to inventory, equip melee weapon from inventory.
        /// </summary>
        public static void SwapToMelee(Pawn pawn)
        {
            try
            {
                ThingWithComps ranged = pawn.equipment != null ? pawn.equipment.Primary : null;
                if (ranged == null || !ranged.def.IsRangedWeapon) return;

                // Find melee weapon in inventory
                ThingWithComps melee = FindMeleeInInventory(pawn);
                if (melee == null) return;

                pawn.equipment.TryTransferEquipmentToContainer(ranged, pawn.inventory.innerContainer);
                pawn.inventory.innerContainer.Remove(melee);
                pawn.equipment.AddEquipment(melee);
                GarrisonDebug.Log("[Garrison] " + pawn.LabelShort + " swapped to melee: " + melee.def.defName);
            }
            catch (Exception ex)
            {
                GarrisonDebug.Log("[Garrison] SwapToMelee failed: " + ex.Message);
            }
        }
    }
}
