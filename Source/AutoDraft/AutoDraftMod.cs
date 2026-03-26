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
        public static bool fleeNonCombatants = true;
        public static bool showAlert = true;
        public static int undraftDelay = 500;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref autoUndraft, "autoUndraft", true);
            Scribe_Values.Look(ref fleeNonCombatants, "fleeNonCombatants", true);
            Scribe_Values.Look(ref showAlert, "showAlert", true);
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

            if (autoUndraft)
            {
                l.Label("AD_UndraftDelay".Translate() + ": " + (undraftDelay / 60f).ToString("F1") + "s");
                undraftDelay = (int)l.Slider(undraftDelay, 60, 3000);
            }

            l.GapLine();
            l.Label("AD_AssignHint".Translate());

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

            // Add comp to all humanlike pawns
            foreach (var def in DefDatabase<ThingDef>.AllDefs)
            {
                if (def.race == null || def.race.intelligence != Intelligence.Humanlike) continue;
                if (def.comps == null) continue;
                if (def.comps.Any(c => c is CompProperties_Soldier)) continue;
                def.comps.Add(new CompProperties_Soldier());
            }

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

    // ==================== PER-PAWN SOLDIER COMP ====================

    public class CompProperties_Soldier : CompProperties
    {
        public CompProperties_Soldier() { compClass = typeof(CompSoldier); }
    }

    /// <summary>
    /// Per-pawn component: marks a pawn as a soldier with an assigned combat post.
    /// </summary>
    public class CompSoldier : ThingComp
    {
        public bool isSoldier;
        public IntVec3 combatPost = IntVec3.Invalid;
        public bool autoDrafted; // Was this pawn drafted by us?

        public Pawn Pawn => (Pawn)parent;

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref isSoldier, "ad_isSoldier", false);
            Scribe_Values.Look(ref combatPost, "ad_combatPost", IntVec3.Invalid);
            Scribe_Values.Look(ref autoDrafted, "ad_autoDrafted", false);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (!AutoDraftSettings.enabled) yield break;
            if (Pawn.Faction != Faction.OfPlayer) yield break;
            if (Pawn.WorkTagIsDisabled(WorkTags.Violent)) yield break;

            // Toggle soldier status
            yield return new Command_Toggle
            {
                defaultLabel = "AD_ToggleSoldier".Translate(),
                defaultDesc = "AD_ToggleSoldier_Desc".Translate(),
                isActive = () => isSoldier,
                toggleAction = () => isSoldier = !isSoldier,
                icon = ContentFinder<Texture2D>.Get("UI/Commands/Draft", true)
            };

            // Set combat post (only if soldier)
            if (isSoldier)
            {
                yield return new Command_Action
                {
                    defaultLabel = "AD_SetPost".Translate(),
                    defaultDesc = combatPost.IsValid
                        ? "AD_SetPost_Current".Translate(combatPost.x, combatPost.z)
                        : "AD_SetPost_Desc".Translate(),
                    action = () =>
                    {
                        Find.Targeter.BeginTargeting(
                            new TargetingParameters { canTargetLocations = true },
                            (LocalTargetInfo target) =>
                            {
                                combatPost = target.Cell;
                                Messages.Message("AD_PostSet".Translate(Pawn.LabelShort, combatPost.x, combatPost.z),
                                    Pawn, MessageTypeDefOf.NeutralEvent, false);
                            });
                    },
                    icon = ContentFinder<Texture2D>.Get("UI/Commands/Attack", true)
                };

                // Clear combat post
                if (combatPost.IsValid)
                {
                    yield return new Command_Action
                    {
                        defaultLabel = "AD_ClearPost".Translate(),
                        action = () => combatPost = IntVec3.Invalid,
                        icon = ContentFinder<Texture2D>.Get("UI/Commands/Halt", true)
                    };
                }
            }
        }

        public override string CompInspectStringExtra()
        {
            if (!isSoldier) return null;
            string post = combatPost.IsValid
                ? "(" + combatPost.x + ", " + combatPost.z + ")"
                : (string)"AD_NoPost".Translate();
            return "AD_SoldierStatus".Translate(post);
        }
    }

    // ==================== MAP COMPONENT ====================

    public class MapComponent_AutoDraft : MapComponent
    {
        private bool threatActive;
        private int lastThreatTick = -9999;

        public MapComponent_AutoDraft(Map map) : base(map) { }

        public override void MapComponentTick()
        {
            if (!AutoDraftSettings.enabled) return;
            if (Find.TickManager.TicksGame % 60 != 0) return;

            bool threatsNow = HasHostileThreats();

            if (threatsNow && !threatActive)
            {
                // Threat just appeared
                threatActive = true;
                lastThreatTick = Find.TickManager.TicksGame;
                ActivateSoldiers();
                if (AutoDraftSettings.fleeNonCombatants)
                    FleeNonCombatants();
            }
            else if (threatsNow)
            {
                lastThreatTick = Find.TickManager.TicksGame;
                // Keep soldiers at posts if they wandered
                EnforcePosts();
            }
            else if (!threatsNow && threatActive)
            {
                int ticksSince = Find.TickManager.TicksGame - lastThreatTick;
                if (ticksSince >= AutoDraftSettings.undraftDelay)
                {
                    threatActive = false;
                    if (AutoDraftSettings.autoUndraft)
                        DeactivateSoldiers();
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

        private void ActivateSoldiers()
        {
            int drafted = 0;

            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn.Dead || pawn.Downed) continue;
                if (pawn.Drafted) continue; // Player already drafted them

                var comp = pawn.GetComp<CompSoldier>();
                if (comp == null || !comp.isSoldier) continue;

                // Draft the soldier
                pawn.drafter.Drafted = true;
                comp.autoDrafted = true;
                drafted++;

                // Send to combat post if assigned
                if (comp.combatPost.IsValid && comp.combatPost.InBounds(map))
                {
                    Job gotoPost = JobMaker.MakeJob(JobDefOf.Goto, comp.combatPost);
                    gotoPost.locomotionUrgency = LocomotionUrgency.Sprint;
                    pawn.jobs.TryTakeOrderedJob(gotoPost, JobTag.DraftedOrder);
                }
            }

            if (drafted > 0 && AutoDraftSettings.showAlert)
            {
                Messages.Message("AD_ThreatDetected".Translate(drafted),
                    MessageTypeDefOf.ThreatBig, false);
            }
        }

        /// <summary>
        /// Keep soldiers at their posts. If they finished their goto and are idle,
        /// they'll auto-attack enemies in range (vanilla drafted behavior).
        /// If they wandered off, send them back.
        /// </summary>
        private void EnforcePosts()
        {
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn.Dead || pawn.Downed || !pawn.Drafted) continue;

                var comp = pawn.GetComp<CompSoldier>();
                if (comp == null || !comp.autoDrafted) continue;
                if (!comp.combatPost.IsValid) continue;

                // If pawn has no job (idle at post), they auto-attack. Good.
                if (pawn.CurJob == null || pawn.CurJob.def == JobDefOf.Wait_Combat)
                    continue;

                // If pawn is too far from post, send them back
                if (pawn.Position.DistanceTo(comp.combatPost) > 5f)
                {
                    // Only re-send if they're not already going there
                    if (pawn.CurJob?.def != JobDefOf.Goto
                        || pawn.CurJob?.targetA.Cell != comp.combatPost)
                    {
                        Job gotoPost = JobMaker.MakeJob(JobDefOf.Goto, comp.combatPost);
                        gotoPost.locomotionUrgency = LocomotionUrgency.Sprint;
                        pawn.jobs.TryTakeOrderedJob(gotoPost, JobTag.DraftedOrder);
                    }
                }
            }
        }

        private void DeactivateSoldiers()
        {
            int undrafted = 0;

            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn.Dead || pawn.Downed || !pawn.Drafted) continue;

                var comp = pawn.GetComp<CompSoldier>();
                if (comp == null || !comp.autoDrafted) continue;

                pawn.drafter.Drafted = false;
                comp.autoDrafted = false;
                undrafted++;
            }

            if (undrafted > 0 && AutoDraftSettings.showAlert)
            {
                Messages.Message("AD_ThreatCleared".Translate(undrafted),
                    MessageTypeDefOf.PositiveEvent, false);
            }
        }

        private void FleeNonCombatants()
        {
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn.Dead || pawn.Downed || pawn.Drafted) continue;

                var comp = pawn.GetComp<CompSoldier>();
                if (comp != null && comp.isSoldier) continue; // Soldiers fight

                if (!pawn.WorkTagIsDisabled(WorkTags.Violent)) continue; // Only flee pacifists

                IntVec3 safeSpot = FindSafeSpot(pawn);
                if (safeSpot.IsValid)
                {
                    Job flee = JobMaker.MakeJob(JobDefOf.Goto, safeSpot);
                    flee.locomotionUrgency = LocomotionUrgency.Sprint;
                    pawn.jobs.TryTakeOrderedJob(flee, JobTag.Misc);
                }
            }
        }

        private IntVec3 FindSafeSpot(Pawn pawn)
        {
            IntVec3 threatCenter = GetThreatCenter();
            if (!threatCenter.IsValid) return pawn.Position;

            IntVec3 bestCell = IntVec3.Invalid;
            float bestDist = 0f;

            foreach (IntVec3 cell in map.areaManager.Home.ActiveCells)
            {
                if (!cell.Roofed(map)) continue;
                if (!cell.Standable(map)) continue;
                if (!pawn.CanReach(cell, PathEndMode.OnCell, Danger.Some)) continue;

                float dist = cell.DistanceTo(threatCenter);
                if (dist > bestDist)
                {
                    bestDist = dist;
                    bestCell = cell;
                }
            }
            return bestCell;
        }

        private IntVec3 GetThreatCenter()
        {
            int x = 0, z = 0, count = 0;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (p.Dead || p.Downed) continue;
                if (!p.HostileTo(Faction.OfPlayer)) continue;
                x += p.Position.x;
                z += p.Position.z;
                count++;
            }
            if (count == 0) return IntVec3.Invalid;
            return new IntVec3(x / count, 0, z / count);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref threatActive, "ad_threatActive", false);
            Scribe_Values.Look(ref lastThreatTick, "ad_lastThreatTick", -9999);
        }
    }
}
