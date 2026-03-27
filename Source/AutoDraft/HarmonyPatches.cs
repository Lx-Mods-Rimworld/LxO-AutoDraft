using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoDraft
{
    [StaticConstructorOnStartup]
    public static class HarmonyInit
    {
        static HarmonyInit()
        {
            var harmony = new Harmony("Lexxers.AutoDraft");
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            // Add comp to all humanlike pawns
            foreach (var def in DefDatabase<ThingDef>.AllDefs)
            {
                if (def.race == null || def.race.intelligence != Intelligence.Humanlike) continue;
                if (def.comps == null) continue;
                if (def.comps.Any(c => c is CompProperties_Soldier)) continue;
                def.comps.Add(new CompProperties_Soldier());
            }

            ModIntegration.Init();
            Log.Message("[AutoDraft] Initialized.");
        }
    }

    /// <summary>
    /// Block FleeAndCower for active soldiers. Soldiers fight, not flee.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    public static class Patch_BlockFlee
    {
        public static bool Prefix(Pawn_JobTracker __instance, Job newJob)
        {
            try
            {
                if (newJob?.def != JobDefOf.FleeAndCower) return true;

                Pawn pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
                if (pawn == null) return true;

                var comp = pawn.GetComp<CompSoldier>();
                if (comp == null || !comp.isSoldier || !comp.autoDrafted) return true;

                // Soldier should fight, not flee. Block the flee job.
                GarrisonDebug.Log("[Garrison] BLOCKED flee for soldier " + pawn.LabelShort);
                return false;
            }
            catch { return true; }
        }
    }

    [HarmonyPatch(typeof(Map), nameof(Map.FinalizeInit))]
    public static class Patch_MapInit
    {
        public static void Postfix(Map __instance)
        {
            if (__instance.GetComponent<MapComponent_AutoDraft>() == null)
                __instance.components.Add(new MapComponent_AutoDraft(__instance));
        }
    }
}
