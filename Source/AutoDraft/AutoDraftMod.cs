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
                Log.Message("[Garrison] BLOCKED flee for soldier " + pawn.LabelShort);
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
        public bool autoDrafted;
        public int pendingKillTarget = -1; // thingID of target to kill after strip completes

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
            if (!(parent is Pawn)) yield break; // Skip corpses
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
            if (!(parent is Pawn)) return null;
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

            // Debug: log state every 300 ticks (~5 seconds) if there are downed hostiles
            if (downedHostiles && Find.TickManager.TicksGame % 300 == 0)
            {
                int soldierCount = 0;
                int activatedCount = 0;
                foreach (var p in map.mapPawns.FreeColonistsSpawned)
                {
                    var c = p.GetComp<CompSoldier>();
                    if (c != null && c.isSoldier) soldierCount++;
                    if (c != null && c.autoDrafted) activatedCount++;
                }
                Log.Message("[Garrison] TICK state: standing=" + standingThreats
                    + " downed=" + downedHostiles + " threatActive=" + threatActive
                    + " soldiers=" + soldierCount + " activated=" + activatedCount);
            }

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
            else if (!standingThreats && downedHostiles)
            {
                // All enemies down but alive -- hunt them down and finish them
                if (!threatActive)
                {
                    threatActive = true;
                    ActivateSoldiers(); // Activate soldiers so autoDrafted=true
                }
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
            // First: collect ALL enemies already being handled by any soldier
            var handledEnemies = new HashSet<int>();
            JobDef skDef = DefDatabase<JobDef>.GetNamedSilentFail("AD_StripThenKill");
            JobDef scDef = DefDatabase<JobDef>.GetNamedSilentFail("AD_StripThenCapture");

            foreach (Pawn soldier in map.mapPawns.FreeColonistsSpawned.ToList())
            {
                if (soldier.Dead || soldier.Downed) continue;
                var curJob = soldier.CurJob;
                if (curJob == null) continue;

                // Mark target as handled if soldier is doing ANY combat/post-combat job on them
                if (curJob.def == JobDefOf.AttackMelee || curJob.def == JobDefOf.Strip
                    || curJob.def == JobDefOf.Capture
                    || curJob.def == skDef || curJob.def == scDef)
                {
                    if (curJob.targetA.Thing is Pawn targetPawn && targetPawn.Downed)
                        handledEnemies.Add(targetPawn.thingIDNumber);
                }
            }

            // Second: assign soldiers to unhandled downed enemies
            // For downed cleanup, soldiers CAN be interrupted from normal work
            foreach (Pawn soldier in map.mapPawns.FreeColonistsSpawned.ToList())
            {
                if (soldier.Dead || soldier.Downed) continue;
                var comp = soldier.GetComp<CompSoldier>();
                if (comp == null || !comp.isSoldier) continue; // Use isSoldier, not autoDrafted

                // Don't interrupt if already handling a downed enemy
                var curJobDef = soldier.CurJob?.def;
                if (curJobDef == JobDefOf.AttackMelee || curJobDef == JobDefOf.Strip
                    || curJobDef == JobDefOf.Capture
                    || curJobDef == skDef || curJobDef == scDef)
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
            // Animals: just kill
            if (target.RaceProps.Animal)
            {
                Log.Message("[Garrison] " + soldier.LabelShort + " -> kill animal " + target.LabelShort);
                HandleDownedAnimal(soldier, target);
                return;
            }

            var mode = AutoDraftSettings.downedHandling;
            bool wantCapture = mode == DownedHandling.Capture || mode == DownedHandling.StripThenCapture;
            bool wantStrip = mode == DownedHandling.StripThenKill || mode == DownedHandling.StripThenCapture;
            bool hasApparel = target.apparel != null && target.apparel.WornApparelCount > 0;

            // Check capture possibility
            Building_Bed prisonBed = null;
            if (wantCapture)
                prisonBed = RestUtility.FindBedFor(target, soldier, true, false, GuestStatus.Prisoner);

            Log.Message("[Garrison] " + soldier.LabelShort + " HandleDowned " + target.LabelShort
                + " mode=" + mode + " strip=" + wantStrip + " capture=" + wantCapture
                + " bed=" + (prisonBed != null) + " apparel=" + hasApparel);

            // Use CUSTOM JOB DRIVERS that handle strip+kill/capture in ONE job
            // This prevents ThinkTree re-evaluation between strip and kill

            JobDef stripKillDef = DefDatabase<JobDef>.GetNamedSilentFail("AD_StripThenKill");
            JobDef stripCaptureDef = DefDatabase<JobDef>.GetNamedSilentFail("AD_StripThenCapture");

            if (wantStrip && hasApparel && wantCapture && prisonBed != null && stripCaptureDef != null)
            {
                // Strip + Capture as single job
                Job job = JobMaker.MakeJob(stripCaptureDef, target, prisonBed);
                soldier.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                Log.Message("[Garrison] " + soldier.LabelShort + " -> StripThenCapture " + target.LabelShort);
                return;
            }

            if (wantStrip && hasApparel && stripKillDef != null && !soldier.WorkTagIsDisabled(WorkTags.Violent))
            {
                // Strip + Kill as single job
                Job job = JobMaker.MakeJob(stripKillDef, target);
                soldier.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                Log.Message("[Garrison] " + soldier.LabelShort + " -> StripThenKill " + target.LabelShort);
                return;
            }

            if (wantCapture && prisonBed != null)
            {
                // Capture only (no strip needed)
                Job captureJob = JobMaker.MakeJob(JobDefOf.Capture, target, prisonBed);
                soldier.jobs.TryTakeOrderedJob(captureJob, JobTag.Misc);
                Log.Message("[Garrison] " + soldier.LabelShort + " -> Capture " + target.LabelShort);
                return;
            }

            // Fallback: kill directly using our custom job (instant kill, not melee swings)
            // AttackMelee only does one swing per job -- downed pawns need multiple hits
            // Our StripThenKill uses Kill(DamageInfo) for instant execution
            if (!soldier.WorkTagIsDisabled(WorkTags.Violent))
            {
                if (stripKillDef != null)
                {
                    Job job = JobMaker.MakeJob(stripKillDef, target);
                    soldier.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    Log.Message("[Garrison] " + soldier.LabelShort + " -> Execute " + target.LabelShort);
                }
                else
                {
                    Job killJob = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
                    soldier.jobs.TryTakeOrderedJob(killJob, JobTag.Misc);
                    Log.Message("[Garrison] " + soldier.LabelShort + " -> Kill(melee) " + target.LabelShort);
                }
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

                // Don't interrupt active COMBAT jobs (attack, goto post, flee block)
                // But DO interrupt normal work (mining, eating, sleeping) during active threat
                var curJobDef = pawn.CurJob?.def;
                if (curJobDef == JobDefOf.AttackStatic || curJobDef == JobDefOf.AttackMelee
                    || curJobDef == JobDefOf.Goto)
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
                    // No enemies visible from here. Check if a squadmate needs help.
                    if (soldierUnderAttack != null && soldierUnderAttack != pawn && attackingEnemy != null)
                    {
                        float aidDist = pawn.Position.DistanceTo(attackingEnemy.Position);
                        if (aidDist < 40f)
                        {
                            // Run to help squadmate
                            float weaponRange = pawn.equipment?.PrimaryEq?.PrimaryVerb?.verbProps?.range ?? 10f;
                            IntVec3 aidPos = GetKitePosition(pawn, attackingEnemy, weaponRange);
                            if (aidPos.IsValid)
                            {
                                Job aidJob = JobMaker.MakeJob(JobDefOf.Goto, aidPos);
                                aidJob.locomotionUrgency = LocomotionUrgency.Sprint;
                                pawn.jobs.TryTakeOrderedJob(aidJob, JobTag.Misc);
                                continue;
                            }
                        }
                    }

                    // Nobody needs help -- go to post or guard
                    if (!atPost && comp.combatPost.IsValid)
                    {
                        SendToPost(pawn, comp);
                    }
                    else if (curJobDef != JobDefOf.Wait_Combat)
                    {
                        // Guard: Wait_Combat keeps them "busy" so vanilla won't
                        // assign mining/eating. Re-evaluates every 5 seconds.
                        Job guardJob = JobMaker.MakeJob(JobDefOf.Wait_Combat);
                        guardJob.expiryInterval = 300;
                        pawn.jobs.TryTakeOrderedJob(guardJob, JobTag.Misc);
                    }
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

    // ==================== CUSTOM JOB DRIVER: STRIP THEN KILL ====================

    /// <summary>
    /// Single job that strips a downed pawn then kills them.
    /// Both actions happen as sequential toils within ONE job.
    /// This prevents ThinkTree re-evaluation between strip and kill.
    /// </summary>
    public class JobDriver_StripThenKill : JobDriver
    {
        private const TargetIndex TargetInd = TargetIndex.A;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedOrNull(TargetInd);
            this.FailOnAggroMentalStateAndHostile(TargetInd);

            // 1. Go to the downed target
            yield return Toils_Goto.GotoThing(TargetInd, PathEndMode.ClosestTouch);

            // 2. Strip all apparel (if any)
            Toil stripToil = new Toil();
            stripToil.initAction = () =>
            {
                Pawn target = job.targetA.Thing as Pawn;
                if (target == null || target.Dead) return;
                if (target.apparel != null && target.apparel.WornApparelCount > 0)
                {
                    target.apparel.DropAll(target.PositionHeld);
                    Log.Message("[Garrison] " + pawn.LabelShort + " stripped " + target.LabelShort);
                }
            };
            stripToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return stripToil;

            // 3. Kill the target (melee attack until dead)
            Toil killToil = new Toil();
            killToil.initAction = () =>
            {
                Pawn target = job.targetA.Thing as Pawn;
                if (target == null || target.Dead) return;

                Log.Message("[Garrison] " + pawn.LabelShort + " executing " + target.LabelShort);

                // Direct kill -- deal lethal damage
                target.Kill(new DamageInfo(DamageDefOf.Blunt, 999f, 0f, -1f, pawn));
            };
            killToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return killToil;
        }
    }

    /// <summary>
    /// Strip then capture: strips apparel, then hauls to prison bed.
    /// TargetA = downed pawn, TargetB = prison bed.
    /// </summary>
    public class JobDriver_StripThenCapture : JobDriver
    {
        private const TargetIndex TargetInd = TargetIndex.A;
        private const TargetIndex BedInd = TargetIndex.B;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed)
                && pawn.Reserve(job.targetB, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedOrNull(TargetInd);
            this.FailOnDestroyedOrNull(BedInd);

            // 1. Go to the downed target
            yield return Toils_Goto.GotoThing(TargetInd, PathEndMode.ClosestTouch);

            // 2. Strip
            Toil stripToil = new Toil();
            stripToil.initAction = () =>
            {
                Pawn target = job.targetA.Thing as Pawn;
                if (target == null || target.Dead) return;
                if (target.apparel != null && target.apparel.WornApparelCount > 0)
                {
                    target.apparel.DropAll(target.PositionHeld);
                    Log.Message("[Garrison] " + pawn.LabelShort + " stripped " + target.LabelShort + " for capture");
                }
            };
            stripToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return stripToil;

            // 3. Pick up the downed pawn
            yield return Toils_Haul.StartCarryThing(TargetInd);

            // 4. Carry to prison bed
            yield return Toils_Goto.GotoThing(BedInd, PathEndMode.InteractionCell);

            // 5. Place in bed
            Toil placeToil = new Toil();
            placeToil.initAction = () =>
            {
                Pawn target = pawn.carryTracker.CarriedThing as Pawn;
                Building_Bed bed = job.targetB.Thing as Building_Bed;
                if (target == null || bed == null) return;

                pawn.carryTracker.TryDropCarriedThing(bed.Position, ThingPlaceMode.Direct, out Thing _);
                target.guest?.SetGuestStatus(Faction.OfPlayer, GuestStatus.Prisoner);
                Log.Message("[Garrison] " + pawn.LabelShort + " captured " + target.LabelShort);
            };
            placeToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return placeToil;
        }
    }
}
