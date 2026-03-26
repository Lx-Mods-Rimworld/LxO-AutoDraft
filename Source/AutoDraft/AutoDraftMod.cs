using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AutoDraft
{
    // ==================== SETTINGS ====================

    public class AutoDraftSettings : ModSettings
    {
        public static bool enabled = true;
        public static bool autoUndraft = true;
        public static bool sendToRallyPoint = false;
        public static bool fleeNonCombatants = true;
        public static bool showAlert = true;

        // Who to draft: by minimum combat skill
        public static int minShootingOrMelee = 4;
        // Delay before undrafting after last threat (ticks). 500 = ~8 seconds
        public static int undraftDelay = 500;

        // Per-pawn override stored in MapComponent
        public override void ExposeData()
        {
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref autoUndraft, "autoUndraft", true);
            Scribe_Values.Look(ref sendToRallyPoint, "sendToRallyPoint", false);
            Scribe_Values.Look(ref fleeNonCombatants, "fleeNonCombatants", true);
            Scribe_Values.Look(ref showAlert, "showAlert", true);
            Scribe_Values.Look(ref minShootingOrMelee, "minShootingOrMelee", 4);
            Scribe_Values.Look(ref undraftDelay, "undraftDelay", 500);
            base.ExposeData();
        }

        public static void DrawSettings(Rect inRect)
        {
            Listing_Standard l = new Listing_Standard();
            l.Begin(inRect);

            l.CheckboxLabeled("AD_Enabled".Translate(), ref enabled);
            if (!enabled) { l.End(); return; }

            l.GapLine();
            l.CheckboxLabeled("AD_AutoUndraft".Translate(), ref autoUndraft,
                "AD_AutoUndraft_Desc".Translate());
            l.CheckboxLabeled("AD_FleeNonCombatants".Translate(), ref fleeNonCombatants,
                "AD_FleeNonCombatants_Desc".Translate());
            l.CheckboxLabeled("AD_ShowAlert".Translate(), ref showAlert);

            l.GapLine();
            l.Label("AD_MinSkill".Translate() + ": " + minShootingOrMelee);
            minShootingOrMelee = (int)l.Slider(minShootingOrMelee, 0, 15);

            if (autoUndraft)
            {
                l.Label("AD_UndraftDelay".Translate() + ": " + (undraftDelay / 60f).ToString("F1") + "s");
                undraftDelay = (int)l.Slider(undraftDelay, 60, 3000);
            }

            l.End();
        }
    }

    public class AutoDraftMod : Mod
    {
        public static AutoDraftSettings settings;

        public AutoDraftMod(ModContentPack content) : base(content)
        {
            settings = GetSettings<AutoDraftSettings>();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            AutoDraftSettings.DrawSettings(inRect);
        }

        public override string SettingsCategory() => "LxO - Auto Draft";
    }

    // ==================== HARMONY ====================

    [StaticConstructorOnStartup]
    public static class HarmonyInit
    {
        static HarmonyInit()
        {
            var harmony = new Harmony("Lexxers.AutoDraft");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            Log.Message("[AutoDraft] Initialized.");
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

    // ==================== CORE LOGIC ====================

    public class MapComponent_AutoDraft : MapComponent
    {
        // State tracking
        private bool threatActive;
        private int lastThreatTick = -9999;

        // Track which pawns WE drafted (so we only undraft those)
        private HashSet<int> autoDraftedPawnIDs = new HashSet<int>();

        // Track pawns the player manually undrafted during auto-draft
        // (don't re-draft them)
        private HashSet<int> playerUndraftedIDs = new HashSet<int>();

        // Per-pawn exclude from auto-draft
        public HashSet<int> excludedPawnIDs = new HashSet<int>();

        public MapComponent_AutoDraft(Map map) : base(map) { }

        public override void MapComponentTick()
        {
            if (!AutoDraftSettings.enabled) return;
            if (Find.TickManager.TicksGame % 60 != 0) return; // Check every second

            bool threatsNow = HasHostileThreats();

            if (threatsNow && !threatActive)
            {
                // Threat just appeared -- DRAFT
                threatActive = true;
                lastThreatTick = Find.TickManager.TicksGame;
                playerUndraftedIDs.Clear();
                DraftSoldiers();
                if (AutoDraftSettings.fleeNonCombatants)
                    FleeNonCombatants();
            }
            else if (threatsNow)
            {
                // Threat ongoing -- track timing
                lastThreatTick = Find.TickManager.TicksGame;

                // Check if player manually undrafted any of our auto-drafted pawns
                TrackPlayerUndrafts();
            }
            else if (!threatsNow && threatActive)
            {
                // Threats gone -- start undraft timer
                int ticksSinceThreat = Find.TickManager.TicksGame - lastThreatTick;
                if (ticksSinceThreat >= AutoDraftSettings.undraftDelay)
                {
                    threatActive = false;
                    if (AutoDraftSettings.autoUndraft)
                        UndraftSoldiers();
                }
            }
        }

        private bool HasHostileThreats()
        {
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn.Dead || pawn.Downed) continue;
                if (pawn.HostileTo(Faction.OfPlayer))
                    return true;
            }
            return false;
        }

        private void DraftSoldiers()
        {
            int drafted = 0;

            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn.Dead || pawn.Downed) continue;
                if (pawn.Drafted) continue; // Already drafted by player
                if (excludedPawnIDs.Contains(pawn.thingIDNumber)) continue;
                if (!IsSoldier(pawn)) continue;

                pawn.drafter.Drafted = true;
                autoDraftedPawnIDs.Add(pawn.thingIDNumber);
                drafted++;
            }

            if (drafted > 0 && AutoDraftSettings.showAlert)
            {
                Messages.Message("AD_ThreatDetected".Translate(drafted),
                    MessageTypeDefOf.ThreatBig, false);
            }
        }

        private void UndraftSoldiers()
        {
            int undrafted = 0;

            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn.Dead || pawn.Downed) continue;
                if (!pawn.Drafted) continue;
                if (!autoDraftedPawnIDs.Contains(pawn.thingIDNumber)) continue;

                // Don't undraft if player already took manual control and re-drafted
                pawn.drafter.Drafted = false;
                undrafted++;
            }

            autoDraftedPawnIDs.Clear();
            playerUndraftedIDs.Clear();

            if (undrafted > 0 && AutoDraftSettings.showAlert)
            {
                Messages.Message("AD_ThreatCleared".Translate(undrafted),
                    MessageTypeDefOf.PositiveEvent, false);
            }
        }

        /// <summary>
        /// Track if the player manually undrafted a pawn we auto-drafted.
        /// Don't re-draft those pawns.
        /// </summary>
        private void TrackPlayerUndrafts()
        {
            var toRemove = new List<int>();
            foreach (int id in autoDraftedPawnIDs)
            {
                Pawn pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(
                    p => p.thingIDNumber == id);
                if (pawn == null) continue;

                // If we drafted them but they're no longer drafted, player undrafted them
                if (!pawn.Drafted && !pawn.Downed && !pawn.Dead)
                {
                    playerUndraftedIDs.Add(id);
                    toRemove.Add(id);
                }
            }
            foreach (int id in toRemove)
                autoDraftedPawnIDs.Remove(id);
        }

        private void FleeNonCombatants()
        {
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn.Dead || pawn.Downed || pawn.Drafted) continue;
                if (IsSoldier(pawn)) continue; // Soldiers fight, not flee
                if (pawn.WorkTagIsDisabled(WorkTags.Violent)) // Pacifists always flee
                {
                    // Find safe spot and send there
                    IntVec3 safeSpot = FindSafeSpot(pawn);
                    if (safeSpot.IsValid)
                    {
                        Job fleeJob = JobMaker.MakeJob(JobDefOf.Goto, safeSpot);
                        fleeJob.locomotionUrgency = LocomotionUrgency.Sprint;
                        pawn.jobs.TryTakeOrderedJob(fleeJob, JobTag.Misc);
                    }
                }
            }
        }

        private IntVec3 FindSafeSpot(Pawn pawn)
        {
            // Find a roofed cell in home zone far from threats
            IntVec3 bestCell = IntVec3.Invalid;
            float bestDist = 0f;

            // Find average threat position
            IntVec3 threatCenter = IntVec3.Invalid;
            int threatCount = 0;
            foreach (Pawn threat in map.mapPawns.AllPawnsSpawned)
            {
                if (threat.Dead || threat.Downed) continue;
                if (!threat.HostileTo(Faction.OfPlayer)) continue;
                if (!threatCenter.IsValid)
                    threatCenter = threat.Position;
                else
                    threatCenter = new IntVec3(
                        (threatCenter.x * threatCount + threat.Position.x) / (threatCount + 1),
                        0,
                        (threatCenter.z * threatCount + threat.Position.z) / (threatCount + 1));
                threatCount++;
            }

            if (!threatCenter.IsValid) return pawn.Position;

            // Search for roofed cell in home zone far from threats
            foreach (IntVec3 cell in map.areaManager.Home.ActiveCells)
            {
                if (!cell.Roofed(map)) continue;
                if (!cell.Standable(map)) continue;
                if (!pawn.CanReach(cell, PathEndMode.OnCell, Danger.Some)) continue;

                float distToThreat = cell.DistanceTo(threatCenter);
                if (distToThreat > bestDist)
                {
                    bestDist = distToThreat;
                    bestCell = cell;
                }
            }

            return bestCell;
        }

        private bool IsSoldier(Pawn pawn)
        {
            if (pawn.WorkTagIsDisabled(WorkTags.Violent)) return false;
            if (pawn.skills == null) return false;

            int shooting = pawn.skills.GetSkill(SkillDefOf.Shooting)?.Level ?? 0;
            int melee = pawn.skills.GetSkill(SkillDefOf.Melee)?.Level ?? 0;

            return Math.Max(shooting, melee) >= AutoDraftSettings.minShootingOrMelee;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref threatActive, "ad_threatActive", false);
            Scribe_Values.Look(ref lastThreatTick, "ad_lastThreatTick", -9999);
            Scribe_Collections.Look(ref autoDraftedPawnIDs, "ad_autoDrafted", LookMode.Value);
            Scribe_Collections.Look(ref excludedPawnIDs, "ad_excluded", LookMode.Value);
            Scribe_Collections.Look(ref playerUndraftedIDs, "ad_playerUndrafted", LookMode.Value);
            if (autoDraftedPawnIDs == null) autoDraftedPawnIDs = new HashSet<int>();
            if (excludedPawnIDs == null) excludedPawnIDs = new HashSet<int>();
            if (playerUndraftedIDs == null) playerUndraftedIDs = new HashSet<int>();
        }
    }
}
