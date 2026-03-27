using System.Collections.Generic;
using System.Linq;
using AutoDraft.Combat;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoDraft
{
    public class MapComponent_AutoDraft : MapComponent
    {
        private bool threatActive;
        private int lastThreatTick = -9999;
        private ThreatTracker threatTracker;
        private SquadCoordination squadCoord;

        public MapComponent_AutoDraft(Map map) : base(map)
        {
            threatTracker = new ThreatTracker(map);
            squadCoord = new SquadCoordination(map);
        }

        public override void MapComponentTick()
        {
            if (!AutoDraftSettings.enabled) return;
            if (Find.TickManager.TicksGame % 60 != 0) return;

            threatTracker.Refresh();
            bool downedHostiles = threatTracker.HasDownedHostiles;

            // Filter threats that don't warrant garrison mobilization
            bool standingThreats = false;
            if (threatTracker.HasActiveThreats)
            {
                var threats = threatTracker.ActiveThreats;

                // Single small manhunter animal -- vanilla handles it
                if (threats.Count == 1 && threats[0].pawn.RaceProps.Animal
                    && threats[0].pawn.RaceProps.baseBodySize < 1f)
                {
                    standingThreats = false;
                }
                else
                {
                    // Check if ALL remaining enemies are fleeing far from colony
                    // (walking to map edge, >50 tiles from any soldier post)
                    bool allFleeing = true;
                    for (int i = 0; i < threats.Count; i++)
                    {
                        Pawn enemy = threats[i].pawn;
                        // Not fleeing if: in combat job, or close to colony
                        bool isFleeing = enemy.CurJob != null && (
                            enemy.CurJob.def == JobDefOf.Goto ||
                            enemy.CurJob.def == JobDefOf.FleeAndCower ||
                            (enemy.CurJob.def.defName.Contains("Exit") || enemy.CurJob.def.defName.Contains("Leave")));

                        if (!isFleeing)
                        {
                            // Check distance: if any enemy is within 50 tiles of threat center, not all fleeing
                            float distToCenter = enemy.Position.DistanceTo(threatTracker.ThreatCenter.IsValid
                                ? threatTracker.ThreatCenter : map.Center);
                            // If close or attacking, real threat
                            if (distToCenter < 50f || enemy.CurJob?.def == JobDefOf.AttackMelee
                                || enemy.CurJob?.def == JobDefOf.AttackStatic)
                            {
                                allFleeing = false;
                                break;
                            }
                        }
                    }

                    standingThreats = !allFleeing;
                }
            }

            // Safety timeout: if threatActive but no threats for 5 minutes, force stand-down
            // Handles edge cases where threats disappear but state doesn't reset
            if (threatActive && !standingThreats && !downedHostiles)
            {
                int ticksSince = Find.TickManager.TicksGame - lastThreatTick;
                if (ticksSince >= AutoDraftSettings.undraftDelay)
                {
                    threatActive = false;
                    if (AutoDraftSettings.autoUndraft)
                        DeactivateSoldiers();
                }
            }

            // Debug: log state every 300 ticks when threat is active
            if (threatActive && Find.TickManager.TicksGame % 300 == 0)
            {
                int soldierCount = 0;
                int activatedCount = 0;
                foreach (var p in map.mapPawns.FreeColonistsSpawned)
                {
                    var c = p.GetComp<CompSoldier>();
                    if (c != null && c.isSoldier) soldierCount++;
                    if (c != null && c.autoDrafted) activatedCount++;
                }
                GarrisonDebug.Log("[Garrison] TICK state: standing=" + standingThreats
                    + " (raw=" + threatTracker.ActiveThreats.Count + ")"
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

                // Safety: re-activate soldiers if threatActive but none are autoDrafted
                // This handles save/load mid-combat where threatActive=true but soldiers=deactivated
                bool anyActivated = false;
                foreach (var p in map.mapPawns.FreeColonistsSpawned)
                {
                    var c = p.GetComp<CompSoldier>();
                    if (c != null && c.autoDrafted) { anyActivated = true; break; }
                }
                if (!anyActivated)
                    ActivateSoldiers();

                EnforcePosts();

                // Also handle downed enemies during active combat
                // Soldiers not engaged with standing threats can finish off downed enemies
                if (downedHostiles)
                    FinishOffDowned();
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

        // [Phase 2] Replaced by ThreatTracker -- kept for reference
        // private bool HasHostileThreats()
        // {
        //     foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned.ToList())
        //     {
        //         if (pawn.Dead) continue;
        //         if (!pawn.HostileTo(Faction.OfPlayer)) continue;
        //         if (!pawn.Downed) return true;
        //     }
        //     return false;
        // }
        //
        // private bool HasDownedHostiles()
        // {
        //     foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned.ToList())
        //     {
        //         if (pawn.Dead) continue;
        //         if (!pawn.HostileTo(Faction.OfPlayer)) continue;
        //         if (pawn.Downed) return true;
        //     }
        //     return false;
        // }

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
            foreach (Pawn enemy in threatTracker.DownedHostiles)
            {
                if (enemy.Dead) continue;
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
                GarrisonDebug.Log("[Garrison] " + soldier.LabelShort + " -> kill animal " + target.LabelShort);
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

            GarrisonDebug.Log("[Garrison] " + soldier.LabelShort + " HandleDowned " + target.LabelShort
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
                GarrisonDebug.Log("[Garrison] " + soldier.LabelShort + " -> StripThenCapture " + target.LabelShort);
                return;
            }

            if (wantStrip && hasApparel && stripKillDef != null && !soldier.WorkTagIsDisabled(WorkTags.Violent))
            {
                // Strip + Kill as single job
                Job job = JobMaker.MakeJob(stripKillDef, target);
                soldier.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                GarrisonDebug.Log("[Garrison] " + soldier.LabelShort + " -> StripThenKill " + target.LabelShort);
                return;
            }

            if (wantCapture && prisonBed != null)
            {
                // Capture only (no strip needed)
                Job captureJob = JobMaker.MakeJob(JobDefOf.Capture, target, prisonBed);
                soldier.jobs.TryTakeOrderedJob(captureJob, JobTag.Misc);
                GarrisonDebug.Log("[Garrison] " + soldier.LabelShort + " -> Capture " + target.LabelShort);
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
                    GarrisonDebug.Log("[Garrison] " + soldier.LabelShort + " -> Execute " + target.LabelShort);
                }
                else
                {
                    Job killJob = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
                    soldier.jobs.TryTakeOrderedJob(killJob, JobTag.Misc);
                    GarrisonDebug.Log("[Garrison] " + soldier.LabelShort + " -> Kill(melee) " + target.LabelShort);
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

                // Soldiers must be set to Attack, not Flee/Ignore
                if (pawn.playerSettings != null)
                    pawn.playerSettings.hostilityResponse = HostilityResponseMode.Attack;

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
            // Danger state comes from ThreatTracker (already computed in Refresh)
            Pawn colonistInDanger = threatTracker.ColonistInDanger;
            Thing dangerSource = threatTracker.DangerSource;
            bool isKidnapping = threatTracker.IsKidnapping;

            // Collect soldiers for squad coordination
            var soldierList = new List<SoldierEntry>();
            int maxCombatLevel = 0;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                var c = p.GetComp<CompSoldier>();
                if (c != null && c.autoDrafted && !p.Dead && !p.Downed && !p.Drafted)
                {
                    soldierList.Add(new SoldierEntry(p, c));
                    if (c.CombatLevel > maxCombatLevel) maxCombatLevel = c.CombatLevel;
                }
            }

            squadCoord.CoordinateSquad(soldierList, threatTracker.ActiveThreats, maxCombatLevel);

            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned.ToList())
            {
                if (pawn.Dead || pawn.Downed) continue;
                if (pawn.Drafted) continue; // Player has manual control

                var comp = pawn.GetComp<CompSoldier>();
                if (comp == null || !comp.autoDrafted) continue;

                // Wake sleeping soldiers -- raid is happening, get up!
                if (pawn.CurJob?.def == JobDefOf.LayDown && !pawn.health.InPainShock)
                {
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    GarrisonDebug.Log("[Garrison] WOKE " + pawn.LabelShort + " (sleeping during raid)");
                }

                // Enforce Attack response -- catches soldiers from saves or manual changes
                if (pawn.playerSettings != null
                    && pawn.playerSettings.hostilityResponse != HostilityResponseMode.Attack)
                    pawn.playerSettings.hostilityResponse = HostilityResponseMode.Attack;

                var curJobDef = pawn.CurJob?.def;
                string curJobName = curJobDef?.defName ?? "NONE";
                bool atPost = comp.combatPost.IsValid && pawn.Position.DistanceTo(comp.combatPost) <= 3f;
                float weaponRange = pawn.equipment?.PrimaryEq?.PrimaryVerb?.verbProps?.range ?? 10f;
                bool hasRangedWeapon = pawn.equipment?.Primary?.def?.IsRangedWeapon ?? false;

                // Phase 3: intelligent target selection via TargetSelector
                int combatLevel = comp.CombatLevel;
                Thing focusTarget = squadCoord.GetAssignedTarget(pawn);
                EnemyInfo? targetInfo = TargetSelector.SelectBestTarget(pawn, threatTracker.ActiveThreats,
                    combatLevel, focusTarget);
                Thing enemy = targetInfo.HasValue ? targetInfo.Value.pawn : null;
                float enemyDist = enemy != null ? pawn.Position.DistanceTo(enemy.Position) : -1f;

                // Mutual aid: if colonist in danger, prioritize that threat
                if (dangerSource != null && colonistInDanger != pawn)
                {
                    float aidDist = pawn.Position.DistanceTo(dangerSource.Position);
                    float aidRange = isKidnapping ? 999f : 30f; // Chase kidnappers anywhere
                    if (aidDist < aidRange)
                    {
                        enemy = dangerSource;
                        enemyDist = aidDist;
                    }
                }

                // LOG: full state for this soldier
                GarrisonDebug.Log("[Garrison] ENFORCE " + pawn.LabelShort
                    + " pos=" + pawn.Position + " atPost=" + atPost
                    + " job=" + curJobName
                    + " enemy=" + (enemy?.LabelShort ?? "NONE")
                    + " dist=" + enemyDist.ToString("F0")
                    + " range=" + weaponRange.ToString("F0")
                    + " ranged=" + hasRangedWeapon
                    + " inDanger=" + (colonistInDanger?.LabelShort ?? "NONE")
                    + (isKidnapping ? " KIDNAP!" : ""));

                // Don't interrupt active combat jobs -- UNLESS target is out of weapon range
                if (curJobDef == JobDefOf.AttackStatic || curJobDef == JobDefOf.AttackMelee)
                {
                    Thing jobTarget = pawn.CurJob?.targetA.Thing;
                    if (jobTarget != null)
                    {
                        float targetDist = pawn.Position.DistanceTo(jobTarget.Position);
                        bool inRange = curJobDef == JobDefOf.AttackMelee
                            ? targetDist <= 1.5f
                            : targetDist <= weaponRange + 0.5f; // Small buffer for float precision

                        if (inRange)
                        {
                            GarrisonDebug.Log("[Garrison]   -> SKIP (combat, target in range)");
                            continue;
                        }

                        // AttackMelee: don't cancel if pawn is closing distance to the right target
                        // The pawn is actively moving toward the enemy -- let them finish approaching
                        if (curJobDef == JobDefOf.AttackMelee && jobTarget == enemy && targetDist <= 15f)
                        {
                            GarrisonDebug.Log("[Garrison]   -> SKIP (melee closing, dist=" + targetDist.ToString("F0") + ")");
                            continue;
                        }

                        // Target moved out of range -- cancel useless attack
                        GarrisonDebug.Log("[Garrison]   -> CANCEL " + curJobName
                            + " on " + jobTarget.LabelShort
                            + " (out of range: " + targetDist.ToString("F0")
                            + " > " + weaponRange.ToString("F0") + ")");
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                        // Fall through to reassignment below
                    }
                    else
                    {
                        GarrisonDebug.Log("[Garrison]   -> SKIP (combat job)");
                        continue;
                    }
                }
                else if (curJobDef == JobDefOf.Rescue || curJobDef == JobDefOf.TendPatient)
                {
                    // Rescue and tending are valid combat-adjacent jobs -- don't interrupt
                    GarrisonDebug.Log("[Garrison]   -> SKIP (rescue/tend)");
                    continue;
                }
                else if (curJobDef == JobDefOf.Goto)
                {
                    GarrisonDebug.Log("[Garrison]   -> SKIP (moving)");
                    continue;
                }

                // Soldiers off-post doing non-combat jobs (eating, recreation, socializing)
                // should return to post during active combat
                if (!atPost && comp.combatPost.IsValid && curJobDef != null
                    && curJobDef != JobDefOf.AttackStatic && curJobDef != JobDefOf.AttackMelee
                    && curJobDef != JobDefOf.Goto && curJobDef != JobDefOf.Rescue
                    && curJobDef != JobDefOf.TendPatient && curJobDef != JobDefOf.Wait_Combat)
                {
                    float distToPost = pawn.Position.DistanceTo(comp.combatPost);
                    if (distToPost > 5f)
                    {
                        GarrisonDebug.Log("[Garrison]   -> RETURN to post (was " + curJobName + " off-post)");
                        SendToPost(pawn, comp);
                        continue;
                    }
                }

                // Phase 4: retreat when badly hurt (Level 9+)
                if (combatLevel >= 9)
                {
                    float healthPct = pawn.health.summaryHealth.SummaryHealthPercent;
                    float retreatThreshold = combatLevel >= 12 ? 0.25f : 0.40f;
                    if (healthPct < retreatThreshold)
                    {
                        IntVec3 retreatPos = PositionEvaluator.FindRetreatPosition(pawn,
                            threatTracker.ThreatCenter, comp.combatPost, map);
                        if (retreatPos.IsValid)
                        {
                            GarrisonDebug.Log("[Garrison]   -> RETREAT (health=" + healthPct.ToString("P0") + ")");
                            Job retreatJob = JobMaker.MakeJob(JobDefOf.Goto, retreatPos);
                            retreatJob.locomotionUrgency = LocomotionUrgency.Sprint;
                            pawn.jobs.TryTakeOrderedJob(retreatJob, JobTag.Misc);
                            continue;
                        }
                    }
                }

                if (enemy != null)
                {
                    float dist = enemyDist;

                    // At post with enemy out of range? Hold position.
                    // Melee soldiers: hold at post until enemy breaches the ranged line.
                    // Do NOT charge just because a ranged soldier is "in danger" at range --
                    // the ranged guys handle ranged threats. Melee intercepts close threats only.
                    // Ranged: hold at 80% of max range
                    float holdRange = hasRangedWeapon ? weaponRange * 0.8f : 15f;
                    if (atPost && dist > holdRange && dist > 1.5f)
                    {
                        if (curJobDef != JobDefOf.Wait_Combat)
                        {
                            GarrisonDebug.Log("[Garrison]   -> GUARD at post (enemy out of range, was " + curJobName + ")");
                            Job guardJob = JobMaker.MakeJob(JobDefOf.Wait_Combat);
                            guardJob.expiryInterval = 600;
                            pawn.jobs.TryTakeOrderedJob(guardJob, JobTag.Misc);
                        }
                        else
                        {
                            GarrisonDebug.Log("[Garrison]   -> HOLD at post (enemy out of range)");
                        }
                        continue;
                    }

                    // Phase 5: weapon swap via WeaponTactics module
                    if (!ModIntegration.IsSmartGearLoaded && targetInfo.HasValue)
                    {
                        SwapAction swap = WeaponTactics.Evaluate(pawn, targetInfo.Value, dist, combatLevel);
                        if (swap == SwapAction.SwapToRanged)
                            WeaponTactics.SwapToRanged(pawn, WeaponTactics.FindRangedInInventory(pawn));
                        else if (swap == SwapAction.SwapToMelee)
                            WeaponTactics.SwapToMelee(pawn);
                    }

                    // Re-check weapon state after potential swap
                    bool currentlyRanged = pawn.equipment != null
                        && pawn.equipment.Primary != null
                        && pawn.equipment.Primary.def.IsRangedWeapon;

                    // === MELEE SOLDIER (primary melee or swapped to melee) ===
                    if (!currentlyRanged && currentlyRanged != hasRangedWeapon)
                    {
                        // Just swapped to melee -- charge
                        GarrisonDebug.Log("[Garrison]   -> CHARGE (swapped to melee) " + enemy.LabelShort);
                        Job meleeJob = JobMaker.MakeJob(JobDefOf.AttackMelee, enemy);
                        pawn.jobs.TryTakeOrderedJob(meleeJob, JobTag.Misc);
                    }
                    else if (currentlyRanged && currentlyRanged != hasRangedWeapon)
                    {
                        // Just swapped to ranged -- shoot
                        GarrisonDebug.Log("[Garrison]   -> SHOOT (swapped to ranged) "
                            + enemy.LabelShort + " dist=" + dist.ToString("F0"));
                        Job attackJob = JobMaker.MakeJob(JobDefOf.AttackStatic, enemy);
                        pawn.jobs.TryTakeOrderedJob(attackJob, JobTag.Misc);
                    }
                    // === MELEE SOLDIER: BODYGUARD ROLE ===
                    // Melee soldiers protect the ranged line. They do NOT charge past shooters.
                    // They intercept enemies that get close to the ranged soldiers.
                    // They hunt fleeing enemies only when no active threat to the line.
                    else if (!hasRangedWeapon)
                    {
                        Pawn enemyPawn = enemy as Pawn;
                        bool enemyFleeing = enemyPawn != null && enemyPawn.CurJob?.def == JobDefOf.FleeAndCower;

                        // Check if enemy is close to ANY ranged soldier (breaching the line)
                        bool enemyBreachingLine = false;
                        foreach (var entry in soldierList)
                        {
                            if (entry.pawn == pawn) continue;
                            bool entryRanged = entry.pawn.equipment?.Primary?.def?.IsRangedWeapon ?? false;
                            if (!entryRanged) continue;
                            float enemyToShooter = entry.pawn.Position.DistanceTo(enemy.Position);
                            if (enemyToShooter <= 8f)
                            {
                                enemyBreachingLine = true;
                                break;
                            }
                        }

                        // Check if melee soldier is between ranged soldiers and enemy
                        // (would cause friendly fire)
                        bool inFriendlyFireZone = false;
                        if (comp.combatPost.IsValid)
                        {
                            // If soldier is further from post than the enemy, they're in the fire zone
                            float soldierToPost = pawn.Position.DistanceTo(comp.combatPost);
                            float enemyToPost = enemy.Position.DistanceTo(comp.combatPost);
                            if (soldierToPost > enemyToPost + 3f)
                                inFriendlyFireZone = true;
                        }

                        if (dist <= 1.5f)
                        {
                            // Already in melee -- fight
                            if (pawn.CurJob?.def == JobDefOf.AttackMelee && pawn.CurJob?.targetA.Thing == enemy)
                            {
                                GarrisonDebug.Log("[Garrison]   -> SKIP (already melee " + enemy.LabelShort + ")");
                            }
                            else
                            {
                                GarrisonDebug.Log("[Garrison]   -> MELEE " + enemy.LabelShort);
                                Job meleeJob = JobMaker.MakeJob(JobDefOf.AttackMelee, enemy);
                                pawn.jobs.TryTakeOrderedJob(meleeJob, JobTag.Misc);
                            }
                        }
                        else if (inFriendlyFireZone && !enemyBreachingLine)
                        {
                            // In friendly fire zone and enemy isn't close to shooters -- fall back to post
                            GarrisonDebug.Log("[Garrison]   -> FALLBACK (in friendly fire zone)");
                            if (comp.combatPost.IsValid)
                                SendToPost(pawn, comp);
                        }
                        else if (enemyBreachingLine || dist <= 8f)
                        {
                            if (pawn.CurJob?.def == JobDefOf.AttackMelee && pawn.CurJob?.targetA.Thing == enemy)
                            {
                                GarrisonDebug.Log("[Garrison]   -> SKIP (already intercepting " + enemy.LabelShort + ")");
                            }
                            else
                            {
                                // Enemy is close to ranged soldiers or close to us -- intercept!
                                GarrisonDebug.Log("[Garrison]   -> INTERCEPT " + enemy.LabelShort
                                    + (enemyBreachingLine ? " (protecting shooters)" : " (close)"));
                                Job meleeJob = JobMaker.MakeJob(JobDefOf.AttackMelee, enemy);
                                pawn.jobs.TryTakeOrderedJob(meleeJob, JobTag.Misc);
                            }
                        }
                        else if (colonistInDanger != null && dist <= 12f)
                        {
                            if (pawn.CurJob?.def == JobDefOf.AttackMelee && pawn.CurJob?.targetA.Thing == enemy)
                            {
                                GarrisonDebug.Log("[Garrison]   -> SKIP (already protecting " + colonistInDanger.LabelShort + ")");
                            }
                            else
                            {
                                // Colonist in danger AND enemy is close enough to intercept -- go help
                                GarrisonDebug.Log("[Garrison]   -> INTERCEPT " + enemy.LabelShort
                                    + " (protecting " + colonistInDanger.LabelShort + ", dist=" + dist.ToString("F0") + ")");
                                Job meleeJob = JobMaker.MakeJob(JobDefOf.AttackMelee, enemy);
                                pawn.jobs.TryTakeOrderedJob(meleeJob, JobTag.Misc);
                            }
                        }
                        else if (enemyFleeing && !threatTracker.HasActiveThreats)
                        {
                            if (pawn.CurJob?.def == JobDefOf.AttackMelee && pawn.CurJob?.targetA.Thing == enemy)
                            {
                                GarrisonDebug.Log("[Garrison]   -> SKIP (already hunting " + enemy.LabelShort + ")");
                            }
                            else
                            {
                                // All threats gone, hunt the fleeing ones
                                GarrisonDebug.Log("[Garrison]   -> HUNT " + enemy.LabelShort + " (fleeing, no other threats)");
                                Job meleeJob = JobMaker.MakeJob(JobDefOf.AttackMelee, enemy);
                                pawn.jobs.TryTakeOrderedJob(meleeJob, JobTag.Misc);
                            }
                        }
                        else if (comp.combatPost.IsValid && pawn.Position.DistanceTo(comp.combatPost) > 3f)
                        {
                            // Hold at post -- wait for enemy to come to us
                            GarrisonDebug.Log("[Garrison]   -> HOLD near post (bodyguard)");
                            SendToPost(pawn, comp);
                        }
                    }
                    // === RANGED SOLDIER (primary ranged) ===
                    else if (dist <= 1.5f)
                    {
                        if (pawn.CurJob?.def == JobDefOf.AttackMelee && pawn.CurJob?.targetA.Thing == enemy)
                        {
                            GarrisonDebug.Log("[Garrison]   -> SKIP (already melee " + enemy.LabelShort + ")");
                        }
                        else
                        {
                            GarrisonDebug.Log("[Garrison]   -> MELEE " + enemy.LabelShort);
                            Job meleeJob = JobMaker.MakeJob(JobDefOf.AttackMelee, enemy);
                            pawn.jobs.TryTakeOrderedJob(meleeJob, JobTag.Misc);
                        }
                    }
                    else if (dist <= weaponRange)
                    {
                        // Don't interrupt verb warmup or burst -- let them finish the shot
                        var primaryVerb = pawn.equipment?.PrimaryEq?.PrimaryVerb;
                        if (primaryVerb != null && primaryVerb.WarmingUp)
                        {
                            GarrisonDebug.Log("[Garrison]   -> SKIP (verb warming up)");
                            continue;
                        }

                        // Phase 4: seek better position if Level 3+ and at/near post
                        if (combatLevel >= 3 && comp.combatPost.IsValid)
                        {
                            IntVec3 combatPos = PositionEvaluator.FindCombatPosition(pawn, enemy, comp.combatPost, combatLevel, map);
                            if (combatPos != pawn.Position && combatPos.IsValid
                                && pawn.Position.DistanceTo(combatPos) > 2f)
                            {
                                GarrisonDebug.Log("[Garrison]   -> REPOSITION to " + combatPos
                                    + " (cover-seeking, lvl=" + combatLevel + ")");
                                Job moveJob = JobMaker.MakeJob(JobDefOf.Goto, combatPos);
                                moveJob.locomotionUrgency = LocomotionUrgency.Sprint;
                                pawn.jobs.TryTakeOrderedJob(moveJob, JobTag.Misc);
                                continue;
                            }
                        }

                        // Don't re-issue AttackStatic if already shooting this target
                        if (pawn.CurJob?.def == JobDefOf.AttackStatic && pawn.CurJob?.targetA.Thing == enemy)
                        {
                            GarrisonDebug.Log("[Garrison]   -> SKIP (already shooting " + enemy.LabelShort + ")");
                        }
                        else
                        {
                            GarrisonDebug.Log("[Garrison]   -> SHOOT " + enemy.LabelShort + " dist=" + dist.ToString("F0"));
                            Job attackJob = JobMaker.MakeJob(JobDefOf.AttackStatic, enemy);
                            pawn.jobs.TryTakeOrderedJob(attackJob, JobTag.Misc);
                        }
                    }
                    else if (dist <= weaponRange + 15f)
                    {
                        // Move to 80% of weapon range -- not the edge where any movement causes miss
                        float optimalRange = weaponRange * 0.8f;
                        if (optimalRange < 5f) optimalRange = 5f;
                        IntVec3 kitePos = GetKitePosition(pawn, enemy, optimalRange + 2f);
                        if (kitePos.IsValid)
                        {
                            GarrisonDebug.Log("[Garrison]   -> ADVANCE to " + kitePos
                                + " (optimal range " + optimalRange.ToString("F0")
                                + ", enemy at " + dist.ToString("F0") + ")");
                            Job moveJob = JobMaker.MakeJob(JobDefOf.Goto, kitePos);
                            moveJob.locomotionUrgency = LocomotionUrgency.Sprint;
                            pawn.jobs.TryTakeOrderedJob(moveJob, JobTag.Misc);
                        }
                    }
                    else
                    {
                        // Enemy too far -- return to post
                        GarrisonDebug.Log("[Garrison]   -> RETURN to post (enemy too far: " + dist.ToString("F0") + ")");
                        if (comp.combatPost.IsValid && pawn.Position.DistanceTo(comp.combatPost) > 3f)
                            SendToPost(pawn, comp);
                    }
                }
                else
                {
                    // No enemies visible. Check if colonist needs help.
                    if (colonistInDanger != null && colonistInDanger != pawn && dangerSource != null)
                    {
                        float aidDist = pawn.Position.DistanceTo(dangerSource.Position);
                        float aidRange = isKidnapping ? 999f : 40f;
                        if (aidDist < aidRange)
                        {
                            if (hasRangedWeapon)
                            {
                                IntVec3 aidPos = GetKitePosition(pawn, dangerSource, weaponRange);
                                if (aidPos.IsValid)
                                {
                                    GarrisonDebug.Log("[Garrison]   -> AID " + colonistInDanger.LabelShort
                                        + " against " + dangerSource.LabelShort + " move to " + aidPos
                                        + (isKidnapping ? " (RESCUE)" : ""));
                                    Job aidJob = JobMaker.MakeJob(JobDefOf.Goto, aidPos);
                                    aidJob.locomotionUrgency = LocomotionUrgency.Sprint;
                                    pawn.jobs.TryTakeOrderedJob(aidJob, JobTag.Misc);
                                    continue;
                                }
                            }
                            // Melee or no kite position: charge directly
                            if (pawn.CurJob?.def == JobDefOf.AttackMelee && pawn.CurJob?.targetA.Thing == dangerSource)
                            {
                                GarrisonDebug.Log("[Garrison]   -> SKIP (already charging " + dangerSource.LabelShort + ")");
                            }
                            else
                            {
                                GarrisonDebug.Log("[Garrison]   -> AID (charge) " + colonistInDanger.LabelShort
                                    + " against " + dangerSource.LabelShort
                                    + (isKidnapping ? " (RESCUE)" : ""));
                                Job chargeJob = JobMaker.MakeJob(JobDefOf.AttackMelee, dangerSource);
                                pawn.jobs.TryTakeOrderedJob(chargeJob, JobTag.Misc);
                            }
                            continue;
                        }
                    }

                    // Nobody needs help
                    if (!atPost && comp.combatPost.IsValid)
                    {
                        GarrisonDebug.Log("[Garrison]   -> GOTO post");
                        SendToPost(pawn, comp);
                    }
                    else if (curJobDef != JobDefOf.Wait_Combat)
                    {
                        GarrisonDebug.Log("[Garrison]   -> GUARD at post");
                        Job guardJob = JobMaker.MakeJob(JobDefOf.Wait_Combat);
                        guardJob.expiryInterval = 600;
                        pawn.jobs.TryTakeOrderedJob(guardJob, JobTag.Misc);
                    }
                    else
                    {
                        GarrisonDebug.Log("[Garrison]   -> HOLDING (already guarding)");
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

        // [Phase 5] SwapToRanged and SwapToMelee moved to Combat.WeaponTactics

        // [Phase 2] Replaced by ThreatTracker.FindNearestPawn -- kept for reference
        // private Thing FindNearestEnemy(Pawn soldier)
        // {
        //     Thing nearest = null;
        //     float nearestDist = float.MaxValue;
        //     foreach (Pawn enemy in map.mapPawns.AllPawnsSpawned.ToList())
        //     {
        //         if (enemy.Dead || enemy.Downed) continue;
        //         if (!enemy.HostileTo(Faction.OfPlayer)) continue;
        //         float dist = soldier.Position.DistanceTo(enemy.Position);
        //         if (dist < nearestDist)
        //         {
        //             nearestDist = dist;
        //             nearest = enemy;
        //         }
        //     }
        //     return nearest;
        // }

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
            IntVec3 threatCenter = threatTracker.ThreatCenter;
            if (!threatCenter.IsValid) return pawn.Position;

            IntVec3 bestCell = IntVec3.Invalid;
            float bestDist = 0f;
            int reachChecks = 0;

            foreach (IntVec3 cell in map.areaManager.Home.ActiveCells)
            {
                if (!cell.Roofed(map)) continue;
                if (!cell.Standable(map)) continue;

                float dist = cell.DistanceTo(threatCenter);
                if (dist <= bestDist) continue; // Only check cells further from threat

                if (reachChecks >= 30) break; // Limit expensive CanReach calls
                if (!pawn.CanReach(cell, PathEndMode.OnCell, Danger.Some))
                {
                    reachChecks++;
                    continue;
                }

                bestDist = dist;
                bestCell = cell;
            }
            return bestCell;
        }

        // [Phase 2] Replaced by ThreatTracker.ThreatCenter -- kept for reference
        // private IntVec3 GetThreatCenter()
        // {
        //     int x = 0, z = 0, count = 0;
        //     foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
        //     {
        //         if (p.Dead || p.Downed) continue;
        //         if (!p.HostileTo(Faction.OfPlayer)) continue;
        //         x += p.Position.x;
        //         z += p.Position.z;
        //         count++;
        //     }
        //     if (count == 0) return IntVec3.Invalid;
        //     return new IntVec3(x / count, 0, z / count);
        // }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref threatActive, "ad_threatActive", false);
            Scribe_Values.Look(ref lastThreatTick, "ad_lastThreatTick", -9999);
        }
    }
}
