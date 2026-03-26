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

    public enum DownedHandling
    {
        Kill,
        StripThenKill,
        Capture,
        StripThenCapture
    }

    public class AutoDraftSettings : ModSettings
    {
        public static bool enabled = true;
        public static bool autoUndraft = true;
        public static bool fleeNonCombatants = true;
        public static bool showAlert = true;
        public static int undraftDelay = 500;
        public static DownedHandling downedHandling = DownedHandling.StripThenCapture;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref autoUndraft, "autoUndraft", true);
            Scribe_Values.Look(ref fleeNonCombatants, "fleeNonCombatants", true);
            Scribe_Values.Look(ref showAlert, "showAlert", true);
            Scribe_Values.Look(ref undraftDelay, "undraftDelay", 500);
            Scribe_Values.Look(ref downedHandling, "downedHandling", DownedHandling.StripThenCapture);
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
            l.Label("AD_DownedHandling".Translate());
            if (l.RadioButton("AD_Downed_Kill".Translate(), downedHandling == DownedHandling.Kill))
                downedHandling = DownedHandling.Kill;
            if (l.RadioButton("AD_Downed_StripKill".Translate(), downedHandling == DownedHandling.StripThenKill))
                downedHandling = DownedHandling.StripThenKill;
            if (l.RadioButton("AD_Downed_Capture".Translate(), downedHandling == DownedHandling.Capture))
                downedHandling = DownedHandling.Capture;
            if (l.RadioButton("AD_Downed_StripCapture".Translate(), downedHandling == DownedHandling.StripThenCapture))
                downedHandling = DownedHandling.StripThenCapture;

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

        public override string SettingsCategory() => "LxO - Garrison";
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

            bool standingThreats = HasHostileThreats();
            bool downedHostiles = HasDownedHostiles();

            if (standingThreats && !threatActive)
            {
                // Threat just appeared -- activate soldiers
                threatActive = true;
                lastThreatTick = Find.TickManager.TicksGame;
                ActivateSoldiers();
                if (AutoDraftSettings.fleeNonCombatants)
                    FleeNonCombatants();
            }
            else if (standingThreats)
            {
                // Active combat -- defend at posts, attack in range
                lastThreatTick = Find.TickManager.TicksGame;
                EnforcePosts();
            }
            else if (!standingThreats && downedHostiles && threatActive)
            {
                // All enemies down but alive -- hunt them down and finish them
                FinishOffDowned();
            }
            else if (!standingThreats && !downedHostiles && threatActive)
            {
                // All enemies dead/gone -- stand down after delay
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
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (pawn.Dead) continue;
                if (!pawn.HostileTo(Faction.OfPlayer)) continue;
                // Standing enemies = active threat
                if (!pawn.Downed) return true;
            }
            return false;
        }

        /// <summary>
        /// Check if there are downed (but alive) hostile pawns that should be finished off.
        /// </summary>
        private bool HasDownedHostiles()
        {
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (pawn.Dead) continue;
                if (!pawn.HostileTo(Faction.OfPlayer)) continue;
                if (pawn.Downed) return true;
            }
            return false;
        }

        /// <summary>
        /// Handle downed enemies based on settings:
        /// Kill, Strip+Kill, Capture, Strip+Capture.
        /// Capture falls back to Strip+Kill if no prison bed.
        /// </summary>
        private void FinishOffDowned()
        {
            // Track which downed enemies are already being handled
            var handledEnemies = new HashSet<int>();

            foreach (Pawn soldier in map.mapPawns.FreeColonistsSpawned.ToList())
            {
                if (soldier.Dead || soldier.Downed) continue;
                var comp = soldier.GetComp<CompSoldier>();
                if (comp == null || !comp.autoDrafted) continue;

                // Already busy with a downed enemy action
                if (soldier.CurJob?.def == JobDefOf.AttackMelee
                    || soldier.CurJob?.def == JobDefOf.Strip
                    || soldier.CurJob?.def == JobDefOf.Capture)
                    continue;

                // Find nearest unhandled downed hostile
                Pawn target = FindNearestDownedEnemy(soldier, handledEnemies);
                if (target == null) continue;
                handledEnemies.Add(target.thingIDNumber);

                HandleDownedEnemy(soldier, target);
            }
        }

        private Pawn FindNearestDownedEnemy(Pawn soldier, HashSet<int> exclude)
        {
            Pawn best = null;
            float bestDist = float.MaxValue;
            foreach (Pawn enemy in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (enemy.Dead || !enemy.Downed) continue;
                if (!enemy.HostileTo(Faction.OfPlayer)) continue;
                if (exclude.Contains(enemy.thingIDNumber)) continue;
                float dist = soldier.Position.DistanceTo(enemy.Position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = enemy;
                }
            }
            return best;
        }

        private void HandleDownedEnemy(Pawn soldier, Pawn target)
        {
            // Animals: different handling (no strip, no prison -- just kill or leave for taming)
            if (target.RaceProps.Animal)
            {
                HandleDownedAnimal(soldier, target);
                return;
            }

            // Humanlike enemies
            var mode = AutoDraftSettings.downedHandling;
            bool wantCapture = mode == DownedHandling.Capture || mode == DownedHandling.StripThenCapture;
            bool wantStrip = mode == DownedHandling.StripThenKill || mode == DownedHandling.StripThenCapture;

            // Check if capture is possible (need a prisoner bed)
            bool canCapture = false;
            if (wantCapture)
            {
                Building_Bed bed = RestUtility.FindBedFor(target, soldier, true, false, GuestStatus.Prisoner);
                canCapture = bed != null;
            }

            // Strip first if enabled and they have apparel
            if (wantStrip && target.apparel != null && target.apparel.WornApparelCount > 0)
            {
                if (!soldier.WorkTagIsDisabled(WorkTags.Hauling))
                {
                    Job stripJob = JobMaker.MakeJob(JobDefOf.Strip, target);
                    soldier.jobs.TryTakeOrderedJob(stripJob, JobTag.Misc);
                    return; // Next tick will capture or kill after stripping
                }
            }

            if (canCapture)
            {
                Building_Bed bed = RestUtility.FindBedFor(target, soldier, true, false, GuestStatus.Prisoner);
                if (bed != null)
                {
                    Job captureJob = JobMaker.MakeJob(JobDefOf.Capture, target, bed);
                    soldier.jobs.TryTakeOrderedJob(captureJob, JobTag.Misc);
                    return;
                }
            }

            // Fallback: kill
            if (!soldier.WorkTagIsDisabled(WorkTags.Violent))
            {
                Job killJob = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
                soldier.jobs.TryTakeOrderedJob(killJob, JobTag.Misc);
            }
        }

        /// <summary>
        /// Handle downed animals: kill them (for meat/leather).
        /// Manhunter animals that go down are a free resource.
        /// </summary>
        private void HandleDownedAnimal(Pawn soldier, Pawn animal)
        {
            if (soldier.WorkTagIsDisabled(WorkTags.Violent)) return;

            // Just kill downed hostile animals -- they'll be butchered later
            Job killJob = JobMaker.MakeJob(JobDefOf.AttackMelee, animal);
            soldier.jobs.TryTakeOrderedJob(killJob, JobTag.Misc);
        }

        private void ActivateSoldiers()
        {
            int activated = 0;

            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned.ToList())
            {
                if (pawn.Dead || pawn.Downed) continue;
                if (pawn.Drafted) continue; // Player drafted manually, don't touch

                var comp = pawn.GetComp<CompSoldier>();
                if (comp == null || !comp.isSoldier) continue;
                if (comp.autoDrafted) continue; // Already activated

                comp.autoDrafted = true;
                activated++;

                // Send to combat post -- undrafted, using AttackMelee/Goto
                SendToPost(pawn, comp);
            }

            if (activated > 0 && AutoDraftSettings.showAlert)
            {
                Messages.Message("AD_ThreatDetected".Translate(activated),
                    MessageTypeDefOf.ThreatBig, false);
            }
        }

        /// <summary>
        /// Send soldier to their post. They go undrafted and will auto-attack
        /// enemies via forced attack jobs from EnforcePosts.
        /// </summary>
        private void SendToPost(Pawn pawn, CompSoldier comp)
        {
            if (!comp.combatPost.IsValid || !comp.combatPost.InBounds(map)) return;

            Job gotoPost = JobMaker.MakeJob(JobDefOf.Goto, comp.combatPost);
            gotoPost.locomotionUrgency = LocomotionUrgency.Sprint;
            pawn.jobs.TryTakeOrderedJob(gotoPost, JobTag.Misc);
        }

        /// <summary>
        /// Keep soldiers at posts and make them attack nearby enemies.
        /// Soldiers stay undrafted -- we give them explicit attack jobs.
        /// </summary>
        private void EnforcePosts()
        {
            // First pass: find if any soldier is being attacked (for mutual aid)
            Pawn soldierUnderAttack = null;
            Thing attackingEnemy = null;
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned.ToList())
            {
                if (pawn.Dead || pawn.Downed) continue;
                var comp = pawn.GetComp<CompSoldier>();
                if (comp == null || !comp.autoDrafted) continue;

                // Check if this soldier is being attacked in melee
                foreach (Pawn enemy in map.mapPawns.AllPawnsSpawned.ToList())
                {
                    if (enemy.Dead || enemy.Downed || !enemy.HostileTo(Faction.OfPlayer)) continue;
                    if (enemy.CurJob?.targetA.Thing == pawn && enemy.Position.DistanceTo(pawn.Position) < 5f)
                    {
                        soldierUnderAttack = pawn;
                        attackingEnemy = enemy;
                        break;
                    }
                }
                if (soldierUnderAttack != null) break;
            }

            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned.ToList())
            {
                if (pawn.Dead || pawn.Downed) continue;
                if (pawn.Drafted) continue; // Player has manual control

                var comp = pawn.GetComp<CompSoldier>();
                if (comp == null || !comp.autoDrafted) continue;

                // Don't interrupt ANY active job -- let current action complete
                // Only give new orders when the pawn is truly idle
                if (pawn.CurJob != null
                    && pawn.CurJob.def != JobDefOf.Wait
                    && pawn.CurJob.def != JobDefOf.Wait_MaintainPosture
                    && pawn.CurJob.def != JobDefOf.GotoWander
                    && pawn.CurJob.def != JobDefOf.Wait_Wander)
                    continue;

                // At post and idle with no nearby enemy? Just stay. Don't wander.
                bool atPost = comp.combatPost.IsValid && pawn.Position.DistanceTo(comp.combatPost) <= 3f;

                // Find nearest enemy
                Thing enemy = FindNearestEnemy(pawn);

                // Mutual aid: if a squadmate is under attack, prioritize that enemy
                if (attackingEnemy != null && soldierUnderAttack != pawn)
                {
                    float aidDist = pawn.Position.DistanceTo(attackingEnemy.Position);
                    if (aidDist < 30f) // Within reasonable aid range
                        enemy = attackingEnemy;
                }

                if (enemy != null)
                {
                    float dist = pawn.Position.DistanceTo(enemy.Position);
                    float weaponRange = pawn.equipment?.PrimaryEq?.PrimaryVerb?.verbProps?.range ?? 10f;
                    bool hasRangedWeapon = pawn.equipment?.Primary?.def?.IsRangedWeapon ?? false;

                    // At post with enemy out of range? Hold position. Don't chase.
                    if (atPost && dist > weaponRange && dist > 1.5f)
                        continue;

                    if (dist <= 1.5f)
                    {
                        // Enemy is in melee range -- always melee, even with ranged weapon
                        // A rifle butt is better than bare hands
                        Job meleeJob = JobMaker.MakeJob(JobDefOf.AttackMelee, enemy);
                        pawn.jobs.TryTakeOrderedJob(meleeJob, JobTag.Misc);
                    }
                    else if (dist <= weaponRange && hasRangedWeapon)
                    {
                        // In weapon range: shoot
                        Job attackJob = JobMaker.MakeJob(JobDefOf.AttackStatic, enemy);
                        pawn.jobs.TryTakeOrderedJob(attackJob, JobTag.Misc);
                    }
                    else if (hasRangedWeapon && dist <= weaponRange + 10f)
                    {
                        // Close but not in range: kite toward firing position
                        IntVec3 kitePos = GetKitePosition(pawn, enemy, weaponRange);
                        if (kitePos.IsValid)
                        {
                            Job moveJob = JobMaker.MakeJob(JobDefOf.Goto, kitePos);
                            moveJob.locomotionUrgency = LocomotionUrgency.Sprint;
                            pawn.jobs.TryTakeOrderedJob(moveJob, JobTag.Misc);
                        }
                    }
                    else if (!hasRangedWeapon)
                    {
                        // Melee soldier: charge
                        Job meleeJob = JobMaker.MakeJob(JobDefOf.AttackMelee, enemy);
                        pawn.jobs.TryTakeOrderedJob(meleeJob, JobTag.Misc);
                    }
                    else
                    {
                        // Enemy too far -- go to post and wait
                        if (comp.combatPost.IsValid && pawn.Position.DistanceTo(comp.combatPost) > 3f)
                            SendToPost(pawn, comp);
                    }
                }
                else
                {
                    // No enemies -- if not at post, go there. If at post, stay put.
                    if (!atPost && comp.combatPost.IsValid)
                        SendToPost(pawn, comp);
                }
            }
        }

        /// <summary>
        /// Find a position at weapon range from the enemy, preferring to stay near
        /// the soldier's current position. This enables "kiting" -- shoot then back up.
        /// </summary>
        private IntVec3 GetKitePosition(Pawn soldier, Thing enemy, float weaponRange)
        {
            // Target a cell that's (weaponRange - 2) tiles from enemy, in the direction away from enemy
            float targetDist = weaponRange - 2f;
            if (targetDist < 3f) targetDist = 3f;

            IntVec3 direction = soldier.Position - enemy.Position;
            float currentDist = soldier.Position.DistanceTo(enemy.Position);
            if (currentDist < 0.1f) return soldier.Position;

            float scale = targetDist / currentDist;
            IntVec3 target = new IntVec3(
                enemy.Position.x + (int)(direction.x * scale),
                0,
                enemy.Position.z + (int)(direction.z * scale));

            if (target.InBounds(map) && target.Standable(map))
                return target;

            return IntVec3.Invalid;
        }

        private Thing FindNearestEnemy(Pawn soldier)
        {
            Thing nearest = null;
            float nearestDist = float.MaxValue;

            foreach (Pawn enemy in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (enemy.Dead || enemy.Downed) continue;
                if (!enemy.HostileTo(Faction.OfPlayer)) continue;
                float dist = soldier.Position.DistanceTo(enemy.Position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = enemy;
                }
            }
            return nearest;
        }

        private void DeactivateSoldiers()
        {
            int deactivated = 0;

            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned.ToList())
            {
                if (pawn.Dead || pawn.Downed) continue;

                var comp = pawn.GetComp<CompSoldier>();
                if (comp == null || !comp.autoDrafted) continue;

                comp.autoDrafted = false;

                // If drafted by us indirectly, undraft
                if (pawn.Drafted)
                    pawn.drafter.Drafted = false;

                // Clear any attack/goto jobs we gave them so they return to work
                if (pawn.CurJob?.def == JobDefOf.AttackStatic || pawn.CurJob?.def == JobDefOf.Goto)
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);

                deactivated++;
            }

            if (deactivated > 0 && AutoDraftSettings.showAlert)
            {
                Messages.Message("AD_ThreatCleared".Translate(deactivated),
                    MessageTypeDefOf.PositiveEvent, false);
            }
        }

        private void FleeNonCombatants()
        {
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned.ToList())
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
