using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AutoDraft
{
    /// <summary>
    /// Detects companion mods (LearnToSurvive, SmartGear) at startup.
    /// Reads CombatInstinct levels from LTS via cached reflection.
    /// </summary>
    public static class ModIntegration
    {
        public static bool IsLTSLoaded { get; private set; }
        public static bool IsSmartGearLoaded { get; private set; }

        // Cached reflection for LTS
        private static Type compIntelligenceType;
        private static MethodInfo getLevelMethod;
        private static object combatInstinctEnumValue; // StatType.CombatInstinct = 3

        /// <summary>
        /// Call from HarmonyInit static constructor.
        /// Detects mods and caches reflection targets.
        /// </summary>
        public static void Init()
        {
            IsLTSLoaded = ModsConfig.ActiveModsInLoadOrder.Any(
                m => m.PackageIdPlayerFacing == "Lexxers.LearnToSurvive");

            IsSmartGearLoaded = ModsConfig.ActiveModsInLoadOrder.Any(
                m => m.PackageIdPlayerFacing == "Lexxers.SmartGear");

            if (IsLTSLoaded)
            {
                try
                {
                    compIntelligenceType = AccessTools.TypeByName("LearnToSurvive.CompIntelligence");
                    if (compIntelligenceType != null)
                    {
                        getLevelMethod = AccessTools.Method(compIntelligenceType, "GetLevel");
                    }

                    Type statTypeEnum = AccessTools.TypeByName("LearnToSurvive.StatType");
                    if (statTypeEnum != null)
                    {
                        // CombatInstinct = 3
                        combatInstinctEnumValue = Enum.ToObject(statTypeEnum, 3);
                    }

                    if (compIntelligenceType == null || getLevelMethod == null || combatInstinctEnumValue == null)
                    {
                        Log.Warning("[Garrison] ModIntegration: LTS detected but reflection setup failed. "
                            + "comp=" + (compIntelligenceType != null) + " method=" + (getLevelMethod != null)
                            + " enum=" + (combatInstinctEnumValue != null));
                        IsLTSLoaded = false;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("[Garrison] ModIntegration: LTS reflection failed: " + ex.Message);
                    IsLTSLoaded = false;
                }
            }

            Log.Message("[Garrison] ModIntegration: LTS=" + IsLTSLoaded + " SmartGear=" + IsSmartGearLoaded);
        }

        /// <summary>
        /// Returns the pawn's CombatInstinct level (0-20).
        /// Returns 20 if LTS is not loaded (max level = all features enabled).
        /// Returns 20 on any error (graceful degradation).
        /// </summary>
        public static int GetCombatLevel(Pawn pawn)
        {
            if (!IsLTSLoaded)
                return 20;

            try
            {
                // Find the CompIntelligence on this pawn
                ThingComp comp = null;
                for (int i = 0; i < pawn.AllComps.Count; i++)
                {
                    if (pawn.AllComps[i].GetType() == compIntelligenceType)
                    {
                        comp = pawn.AllComps[i];
                        break;
                    }
                }

                if (comp == null)
                    return 20;

                object result = getLevelMethod.Invoke(comp, new object[] { combatInstinctEnumValue });
                if (result is int level)
                    return level;

                return 20;
            }
            catch
            {
                return 20;
            }
        }
    }
}
