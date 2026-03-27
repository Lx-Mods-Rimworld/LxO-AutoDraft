using System.Collections.Generic;
using Verse;

namespace AutoDraft.Combat
{
    /// <summary>
    /// Scores and selects targets for soldiers. Replaces "nearest enemy" with
    /// intelligent threat-based targeting that scales with CombatInstinct level.
    /// </summary>
    public static class TargetSelector
    {
        /// <summary>
        /// Score a single enemy for a specific soldier.
        /// Higher score = higher priority target.
        /// </summary>
        public static float ScoreTarget(Pawn soldier, EnemyInfo enemy, int combatLevel)
        {
            float score = 0f;
            float dist = soldier.Position.DistanceTo(enemy.position);

            // Always: distance penalty (prefer closer)
            score -= dist * 1.5f;

            // Always: rescue kidnapped colonists
            if (enemy.isKidnapping) score += 300f;

            // Level 0+: "aiming at me" bonus (mirrors raider AI +10)
            if (enemy.targetingColonist == soldier) score += 15f;

            // Level 0+: defend allies
            if (enemy.targetingColonist != null && enemy.targetingColonist != soldier) score += 8f;

            // Level 5+: DPS-based priority (dangerous enemies first)
            if (combatLevel >= 5)
            {
                score += enemy.dps * 3f;
                if (enemy.isSapper) score += 100f;
                if (enemy.hasDoomsday) score += 200f;
            }

            // Level 8+: don't waste shots on dying enemies
            if (combatLevel >= 8 && enemy.healthPct < 0.15f) score -= 40f;

            // Level 8+: LOS check -- penalize targets we can't see (but don't exclude)
            if (combatLevel >= 8)
            {
                if (!GenSight.LineOfSight(soldier.Position, enemy.position, soldier.Map))
                    score -= 50f;
            }

            return score;
        }

        /// <summary>
        /// Select best target for a soldier from the threat list.
        /// focusFireTarget: if set by SquadCoordination, add bonus for that target (Level 15+).
        /// Returns null if no valid target exists.
        /// </summary>
        public static EnemyInfo? SelectBestTarget(Pawn soldier, List<EnemyInfo> threats,
            int combatLevel, Thing focusFireTarget = null)
        {
            if (threats.Count == 0) return null;

            EnemyInfo? best = null;
            float bestScore = float.MinValue;

            for (int i = 0; i < threats.Count; i++)
            {
                EnemyInfo enemy = threats[i];

                // Skip dead/despawned pawns
                if (enemy.pawn == null || enemy.pawn.Dead || enemy.pawn.Destroyed || !enemy.pawn.Spawned)
                    continue;

                float score = ScoreTarget(soldier, enemy, combatLevel);

                // Level 15+: focus fire bonus from squad coordination
                if (focusFireTarget != null && combatLevel >= 15 && enemy.pawn == focusFireTarget)
                    score += 30f;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = enemy;
                }
            }

            return best;
        }
    }
}
