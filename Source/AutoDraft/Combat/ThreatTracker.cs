using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AutoDraft.Combat
{
    public struct EnemyInfo
    {
        public Pawn pawn;
        public IntVec3 position;
        public bool isRanged;
        public bool isMelee;
        public float weaponRange;
        public float dps;
        public bool isSapper;
        public bool isKidnapping;
        public Pawn targetingColonist;  // which colonist this enemy is targeting (or null)
        public Pawn carriedColonist;    // colonist being kidnapped (or null)
        public float healthPct;
        public float threatScore;       // pre-computed
        public bool hasDoomsday;        // carrying doomsday/triple rocket
    }

    public enum RaidType
    {
        Normal,
        Sapper,
        Breach,
        Siege,
        Manhunter
    }

    /// <summary>
    /// Replaces all the scattered enemy scanning in MapComponent with a single
    /// cached scan per tick cycle. Refresh() is idempotent per tick.
    /// </summary>
    public class ThreatTracker
    {
        private Map map;
        private List<EnemyInfo> activeThreats = new List<EnemyInfo>();
        private List<Pawn> downedHostiles = new List<Pawn>();
        private IntVec3 threatCenter;
        private RaidType raidType;
        private int lastRefreshTick = -1;
        private int lastRaidTypeCheckTick = -1;
        private Faction playerFaction;

        // Raw count: ANY hostile alive on map (before ThreatDisabled filter)
        // Used for fast detection -- activate soldiers immediately
        private int rawHostileCount;

        // Public accessors
        public List<EnemyInfo> ActiveThreats { get { return activeThreats; } }
        public List<Pawn> DownedHostiles { get { return downedHostiles; } }
        public bool HasActiveThreats { get { return activeThreats.Count > 0; } }
        public bool HasDownedHostiles { get { return downedHostiles.Count > 0; } }
        public bool HasAnyHostile { get { return rawHostileCount > 0; } }
        public IntVec3 ThreatCenter { get { return threatCenter; } }
        public RaidType CurrentRaidType { get { return raidType; } }

        // Danger detection (replaces the inline code in EnforcePosts)
        public Pawn ColonistInDanger { get; private set; }
        public Thing DangerSource { get; private set; }
        public bool IsKidnapping { get; private set; }

        public ThreatTracker(Map map)
        {
            this.map = map;
        }

        /// <summary>
        /// Refresh threat data. Only scans once per tick (idempotent).
        /// Call at the start of each tick cycle.
        /// </summary>
        public void Refresh()
        {
            int tick = Find.TickManager.TicksGame;
            if (tick == lastRefreshTick) return;
            lastRefreshTick = tick;

            activeThreats.Clear();
            downedHostiles.Clear();
            rawHostileCount = 0;
            ColonistInDanger = null;
            DangerSource = null;
            IsKidnapping = false;

            int sumX = 0, sumZ = 0, threatCount = 0;

            // Use attackTargetsCache for efficient hostile enumeration
            playerFaction = Find.FactionManager?.OfPlayer;
            if (playerFaction == null) return;

            int debugTotal = 0, debugNonPawn = 0, debugDead = 0;
            foreach (IAttackTarget target in map.attackTargetsCache.TargetsHostileToFaction(playerFaction))
            {
                debugTotal++;
                Pawn enemy = target as Pawn;
                if (enemy == null) { debugNonPawn++; continue; }
                if (enemy.Dead) { debugDead++; continue; }

                // Count ALL living hostiles before any filtering (for fast detection)
                rawHostileCount++;

                // Downed enemies: still need processing (strip/kill/capture)
                // Check BEFORE ThreatDisabled since downed counts as threat-disabled
                if (enemy.Downed)
                {
                    downedHostiles.Add(enemy);
                    continue;
                }

                // Skip dormant mechs (ancient danger, mech clusters, inactive entities)
                // Per Research/RimWorldEngine/Pawns/PawnClass.md: CompCanBeDormant
                // Per Research/RimWorldEngine/Utilities/RestUtility.md: IsActivityDormant
                // Per Research/RimWorldEngine/AI/LordSystem.md: raiders always have a Lord
                // Do NOT use ThreatDisabled here -- it's too broad (catches spawning raiders too)
                if (RestUtility.IsActivityDormant(enemy))
                    continue;
                // No lord AND no job = truly inactive (not part of any raid/event)
                if (enemy.GetLord() == null && enemy.CurJob == null)
                    continue;

                // Build EnemyInfo for active standing threats
                EnemyInfo info = BuildEnemyInfo(enemy);
                activeThreats.Add(info);

                // Accumulate for threat center
                sumX += enemy.Position.x;
                sumZ += enemy.Position.z;
                threatCount++;

                // Danger detection: kidnapping > targeting colonist > attacking near colonist
                DetectDanger(enemy, info);
            }

            // Compute threat center
            if (threatCount > 0)
                threatCenter = new IntVec3(sumX / threatCount, 0, sumZ / threatCount);
            else
                threatCenter = IntVec3.Invalid;

            // Debug: dump FULL state of every hostile in cache (before and after filtering)
            if (tick % 300 == 0 && debugTotal > 0)
            {
                GarrisonDebug.Log("[Garrison] ThreatTracker: raw=" + rawHostileCount
                    + " standing=" + activeThreats.Count + " downed=" + downedHostiles.Count
                    + " filtered=" + (rawHostileCount - activeThreats.Count - downedHostiles.Count));

                // Re-iterate cache to log EVERY hostile with full state
                foreach (IAttackTarget tgt in map.attackTargetsCache.TargetsHostileToFaction(playerFaction))
                {
                    Pawn ep = tgt as Pawn;
                    if (ep == null || ep.Dead) continue;
                    GarrisonDebug.Log("[Garrison]   HOSTILE: " + ep.LabelShort
                        + " id=" + ep.thingIDNumber
                        + " pos=" + ep.Position
                        + " job=" + (ep.CurJob?.def?.defName ?? "NONE")
                        + " downed=" + ep.Downed
                        + " threatDisabled=" + ep.ThreatDisabled(null)
                        + " moving=" + (ep.pather?.Moving ?? false)
                        + " faction=" + (ep.Faction?.Name ?? "NONE")
                        + " race=" + ep.def.defName
                        + " mental=" + (ep.InMentalState ? ep.MentalState.def.defName : "none")
                        + " lord=" + (ep.GetLord()?.LordJob?.GetType()?.Name ?? "NONE")
                        + " spawned=" + ep.Spawned);
                }
            }

            // Raid type detection (every 300 ticks)
            if (tick - lastRaidTypeCheckTick > 300)
            {
                raidType = DetectRaidType();
                lastRaidTypeCheckTick = tick;
            }
        }

        private EnemyInfo BuildEnemyInfo(Pawn enemy)
        {
            EnemyInfo info;
            info.pawn = enemy;
            info.position = enemy.Position;

            // Weapon analysis
            ThingDef primaryDef = enemy.equipment?.Primary?.def;
            info.isRanged = primaryDef != null && primaryDef.IsRangedWeapon;
            info.isMelee = !info.isRanged;

            info.weaponRange = 0f;
            info.dps = 0f;

            if (primaryDef != null)
            {
                if (info.isRanged)
                {
                    // Ranged DPS approximation
                    Thing weapon = enemy.equipment.Primary;
                    float dmgMult = weapon.GetStatValue(StatDefOf.RangedWeapon_DamageMultiplier);
                    float cooldown = weapon.GetStatValue(StatDefOf.RangedWeapon_Cooldown);
                    if (cooldown > 0f)
                        info.dps = dmgMult / cooldown;

                    // Range from verb
                    info.weaponRange = enemy.equipment.PrimaryEq?.PrimaryVerb?.verbProps?.range ?? 0f;
                }
                else
                {
                    // Melee DPS
                    Thing weapon = enemy.equipment.Primary;
                    info.dps = weapon.GetStatValue(StatDefOf.MeleeWeapon_AverageDPS);
                    info.weaponRange = 1.5f;
                }
            }
            else
            {
                // Unarmed / animal
                info.isMelee = true;
                info.weaponRange = 1.5f;
            }

            // Sapper detection
            info.isSapper = false;
            if (enemy.CurJob != null && enemy.CurJob.def == JobDefOf.Mine)
                info.isSapper = true;
            if (!info.isSapper)
            {
                Lord lord = enemy.GetLord();
                if (lord != null && lord.CurLordToil != null)
                {
                    string toilName = lord.CurLordToil.GetType().Name;
                    if (toilName.Contains("Sapper") || toilName.Contains("Breach"))
                        info.isSapper = true;
                }
            }

            // Kidnapping detection
            info.isKidnapping = false;
            info.carriedColonist = null;
            Thing carried = enemy.carryTracker?.CarriedThing;
            if (carried is Pawn carriedPawn && carriedPawn.Faction == playerFaction)
            {
                info.isKidnapping = true;
                info.carriedColonist = carriedPawn;
            }

            // Target detection
            info.targetingColonist = null;
            if (enemy.CurJob != null)
            {
                Thing targetThing = enemy.CurJob.targetA.Thing;
                Pawn targetPawn = targetThing as Pawn;
                if (targetPawn != null && targetPawn.Faction == playerFaction && !targetPawn.Dead)
                {
                    info.targetingColonist = targetPawn;
                }
            }

            // Health
            info.healthPct = enemy.health?.summaryHealth?.SummaryHealthPercent ?? 1f;

            // Doomsday / triple rocket detection
            info.hasDoomsday = false;
            if (primaryDef != null)
            {
                string defName = primaryDef.defName;
                if (defName.Contains("Doomsday") || defName.Contains("TripleRocket"))
                {
                    info.hasDoomsday = true;
                }
                else if (primaryDef.Verbs != null)
                {
                    for (int i = 0; i < primaryDef.Verbs.Count; i++)
                    {
                        VerbProperties verb = primaryDef.Verbs[i];
                        if (verb.defaultProjectile?.projectile != null
                            && verb.defaultProjectile.projectile.explosionRadius > 3f)
                        {
                            info.hasDoomsday = true;
                            break;
                        }
                    }
                }
            }

            // Threat score: dps * 3 - distance_to_colony_center + situational bonuses
            // Colony center approximation: use map center for now (will be replaced later)
            IntVec3 colonyCenter = map.Center;
            float distToCenter = enemy.Position.DistanceTo(colonyCenter);
            info.threatScore = info.dps * 3f
                - distToCenter
                + (info.isSapper ? 100f : 0f)
                + (info.hasDoomsday ? 200f : 0f)
                + (info.isKidnapping ? 300f : 0f);

            return info;
        }

        /// <summary>
        /// Detect which colonist is in the most danger.
        /// Priority: kidnapping > pawn targeted > enemy attacking near colonists.
        /// </summary>
        private void DetectDanger(Pawn enemy, EnemyInfo info)
        {
            // Kidnapping: highest priority, overrides everything
            if (info.isKidnapping && info.carriedColonist != null)
            {
                ColonistInDanger = info.carriedColonist;
                DangerSource = enemy;
                IsKidnapping = true;
                GarrisonDebug.Log("[Garrison] DANGER: " + enemy.LabelShort
                    + " KIDNAPPING " + info.carriedColonist.LabelShort);
                return; // Kidnapping is absolute priority
            }

            // Don't overwrite kidnapping with lower priority
            if (IsKidnapping) return;

            // Enemy explicitly targeting a colonist
            if (ColonistInDanger == null && info.targetingColonist != null)
            {
                ColonistInDanger = info.targetingColonist;
                DangerSource = enemy;
                return;
            }

            // Enemy in an attack job but targeting position/building -- find nearest colonist
            if (ColonistInDanger == null)
            {
                JobDef jobDef = enemy.CurJob?.def;
                if (jobDef == JobDefOf.AttackStatic || jobDef == JobDefOf.AttackMelee
                    || (jobDef != null && jobDef.defName.Contains("Attack")))
                {
                    Pawn nearest = null;
                    float nearDist = 30f;
                    foreach (Pawn col in map.mapPawns.FreeColonistsSpawned)
                    {
                        if (col.Dead || col.Downed) continue;
                        float d = col.Position.DistanceTo(enemy.Position);
                        if (d < nearDist)
                        {
                            nearDist = d;
                            nearest = col;
                        }
                    }
                    if (nearest != null)
                    {
                        ColonistInDanger = nearest;
                        DangerSource = enemy;
                    }
                }
            }
        }

        /// <summary>
        /// Detect raid type based on enemy behavior and lord jobs.
        /// </summary>
        private RaidType DetectRaidType()
        {
            bool anySapper = false;
            bool anyBreach = false;
            bool anySiege = false;
            bool allManhunter = true;
            bool anyEnemy = false;

            for (int i = 0; i < activeThreats.Count; i++)
            {
                EnemyInfo info = activeThreats[i];
                Pawn enemy = info.pawn;
                anyEnemy = true;

                // Manhunter check: must be animal with manhunter mental state
                if (!enemy.RaceProps.Animal
                    || enemy.MentalStateDef == null
                    || !enemy.MentalStateDef.defName.Contains("Manhunter"))
                {
                    allManhunter = false;
                }

                // Sapper
                if (info.isSapper)
                    anySapper = true;

                // Siege: check lord job
                Lord lord = enemy.GetLord();
                if (lord != null && lord.LordJob != null)
                {
                    string lordJobName = lord.LordJob.GetType().Name;
                    if (lordJobName.Contains("Siege"))
                        anySiege = true;
                }

                // Breach: enemy attacking walls
                if (enemy.CurJob != null)
                {
                    Thing attackTarget = enemy.CurJob.targetA.Thing;
                    if (attackTarget != null && attackTarget.def != null && attackTarget.def.IsEdifice()
                        && (enemy.CurJob.def == JobDefOf.AttackMelee || enemy.CurJob.def == JobDefOf.AttackStatic))
                    {
                        // Attacking a wall/door
                        if (attackTarget.def.building != null)
                            anyBreach = true;
                    }
                }
            }

            if (!anyEnemy) return RaidType.Normal;
            if (allManhunter) return RaidType.Manhunter;
            if (anySiege) return RaidType.Siege;
            if (anySapper) return RaidType.Sapper;
            if (anyBreach) return RaidType.Breach;
            return RaidType.Normal;
        }

        /// <summary>
        /// Find the nearest active threat to a position.
        /// Returns null if no active threats.
        /// </summary>
        public EnemyInfo? FindNearest(IntVec3 position)
        {
            if (activeThreats.Count == 0) return null;

            EnemyInfo best = activeThreats[0];
            float bestDist = position.DistanceTo(best.position);

            for (int i = 1; i < activeThreats.Count; i++)
            {
                float dist = position.DistanceTo(activeThreats[i].position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = activeThreats[i];
                }
            }
            return best;
        }

        /// <summary>
        /// Find the nearest active threat pawn to a position.
        /// Returns null if no active threats. Convenience method for
        /// code that needs a Thing/Pawn reference.
        /// </summary>
        public Pawn FindNearestPawn(IntVec3 position)
        {
            EnemyInfo? info = FindNearest(position);
            return info.HasValue ? info.Value.pawn : null;
        }
    }
}
