using System.Collections.Generic;
using Verse;

namespace AutoDraft.Combat
{
    public enum SoldierRole { Hold, RangedAttack, MeleeAdvance, Retreat, Aid }

    /// <summary>
    /// Manages team-level tactics: focus fire, suppress+advance, role assignment.
    /// Runs once per tick cycle before individual soldier decisions.
    /// </summary>
    public class SquadCoordination
    {
        private Map map;

        // Track which soldiers are targeting which enemies (soldierID -> enemyID)
        private Dictionary<int, int> soldierTargets = new Dictionary<int, int>();

        // The current focus fire target (highest-threat enemy that 2+ soldiers should attack)
        private int focusFireTargetId = -1;

        public SquadCoordination(Map map)
        {
            this.map = map;
        }

        /// <summary>
        /// Main coordination pass: called once per tick cycle before individual soldier decisions.
        /// Assigns targets and roles for the squad based on max combat level.
        /// </summary>
        public void CoordinateSquad(List<SoldierEntry> soldiers, List<EnemyInfo> threats, int maxCombatLevel)
        {
            soldierTargets.Clear();
            focusFireTargetId = -1;

            // No coordination below level 5 or without threats
            if (maxCombatLevel < 5 || threats.Count == 0 || soldiers.Count == 0)
                return;

            // Sort threats by threatScore descending -- find highest threat
            EnemyInfo topThreat = threats[0];
            for (int i = 1; i < threats.Count; i++)
            {
                if (threats[i].threatScore > topThreat.threatScore)
                    topThreat = threats[i];
            }

            // Skip dead/despawned top threat
            if (topThreat.pawn == null || topThreat.pawn.Dead || topThreat.pawn.Destroyed || !topThreat.pawn.Spawned)
                return;

            focusFireTargetId = topThreat.pawn.thingIDNumber;

            // Count soldiers who can see the top threat (LOS check)
            List<int> soldiersWithLOS = new List<int>();
            for (int i = 0; i < soldiers.Count; i++)
            {
                Pawn pawn = soldiers[i].pawn;
                if (pawn == null || pawn.Dead || pawn.Downed || !pawn.Spawned) continue;

                if (GenSight.LineOfSight(pawn.Position, topThreat.position, map))
                    soldiersWithLOS.Add(pawn.thingIDNumber);
            }

            if (soldiersWithLOS.Count == 0) return;

            // Determine how many soldiers to assign based on combat level
            int assignCount;
            if (maxCombatLevel >= 15)
            {
                // Level 15: full focus fire -- ALL available soldiers on single target
                assignCount = soldiersWithLOS.Count;
            }
            else if (maxCombatLevel >= 5)
            {
                // Level 5: basic focus fire -- 2 soldiers on the top threat
                assignCount = soldiersWithLOS.Count < 2 ? soldiersWithLOS.Count : 2;
            }
            else
            {
                return;
            }

            // Assign soldiers to the focus fire target
            for (int i = 0; i < assignCount && i < soldiersWithLOS.Count; i++)
            {
                soldierTargets[soldiersWithLOS[i]] = focusFireTargetId;
            }

            GarrisonDebug.Log("[Garrison] SquadCoord: focusFire on " + topThreat.pawn.LabelShort
                + " (score=" + topThreat.threatScore.ToString("F0")
                + ") assigned=" + assignCount + "/" + soldiersWithLOS.Count
                + " lvl=" + maxCombatLevel);
        }

        /// <summary>
        /// Check if a specific enemy is the focus fire target.
        /// Returns true if 2+ soldiers are assigned to this enemy.
        /// </summary>
        public bool IsFocusFireTarget(Thing enemy)
        {
            if (enemy == null) return false;
            return enemy.thingIDNumber == focusFireTargetId;
        }

        /// <summary>
        /// Get the assigned focus fire target for a soldier (from coordination).
        /// Returns null if the soldier has no assigned target.
        /// </summary>
        public Thing GetAssignedTarget(Pawn soldier)
        {
            if (soldier == null) return null;

            int enemyId;
            if (!soldierTargets.TryGetValue(soldier.thingIDNumber, out enemyId))
                return null;

            // Look up the enemy pawn on the map
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Pawn))
            {
                if (t.thingIDNumber == enemyId && !t.Destroyed)
                    return t;
            }
            return null;
        }

        /// <summary>
        /// Determine role for a soldier based on weapon, health, and combat level.
        /// </summary>
        public SoldierRole DetermineRole(Pawn soldier, CompSoldier comp,
            List<EnemyInfo> threats, int combatLevel)
        {
            bool hasRanged = soldier.equipment != null
                && soldier.equipment.Primary != null
                && soldier.equipment.Primary.def.IsRangedWeapon;
            float healthPct = soldier.health.summaryHealth.SummaryHealthPercent;

            // Health-based retreat (level 9+)
            if (combatLevel >= 9)
            {
                float retreatThreshold = combatLevel >= 12 ? 0.25f : 0.40f;
                if (healthPct < retreatThreshold)
                    return SoldierRole.Retreat;
            }

            // Level 10+: suppress+advance
            // Ranged soldiers suppress (keep shooting their target)
            // Melee soldiers advance (charge the target ranged soldiers are suppressing)
            if (combatLevel >= 10)
            {
                if (hasRanged) return SoldierRole.RangedAttack; // suppress
                return SoldierRole.MeleeAdvance; // advance on suppressed targets
            }

            // Default roles
            if (hasRanged) return SoldierRole.RangedAttack;
            return SoldierRole.MeleeAdvance;
        }
    }

    /// <summary>
    /// Entry for soldier list passed to SquadCoordination.
    /// Avoids ValueTuple which requires System.ValueTuple in C# 7.3.
    /// </summary>
    public struct SoldierEntry
    {
        public Pawn pawn;
        public CompSoldier comp;

        public SoldierEntry(Pawn pawn, CompSoldier comp)
        {
            this.pawn = pawn;
            this.comp = comp;
        }
    }
}
