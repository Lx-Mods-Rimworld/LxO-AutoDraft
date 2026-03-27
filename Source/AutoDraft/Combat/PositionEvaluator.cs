using RimWorld;
using Verse;
using Verse.AI;

namespace AutoDraft.Combat
{
    /// <summary>
    /// Finds optimal combat positions using RimWorld's built-in CastPositionFinder
    /// and cover system. Scales positioning intelligence with CombatInstinct level.
    /// </summary>
    public static class PositionEvaluator
    {
        /// <summary>
        /// Find the best combat position for a soldier near their post.
        /// Uses CastPositionFinder at level 3+, basic post-staying otherwise.
        /// </summary>
        public static IntVec3 FindCombatPosition(Pawn soldier, Thing enemy, IntVec3 combatPost,
            int combatLevel, Map map)
        {
            // Level 0-2: just stay at post or current position
            if (combatLevel < 3 || !combatPost.IsValid)
                return combatPost.IsValid ? combatPost : soldier.Position;

            // Level 3+: use CastPositionFinder with cover seeking
            Verb verb = soldier.equipment != null && soldier.equipment.PrimaryEq != null
                ? soldier.equipment.PrimaryEq.PrimaryVerb
                : null;
            if (verb == null) return combatPost;

            CastPositionRequest req = new CastPositionRequest();
            req.caster = soldier;
            req.target = enemy;
            req.verb = verb;
            req.wantCoverFromTarget = true;
            req.locus = combatPost;
            req.maxRangeFromLocus = 8f; // don't wander far from post
            req.maxRangeFromTarget = verb.verbProps.range;

            IntVec3 result;
            if (CastPositionFinder.TryFindCastPosition(req, out result))
                return result;

            return combatPost;
        }

        /// <summary>
        /// Check if a cell is a chokepoint (1-2 standable cardinal neighbors).
        /// Useful for positioning melee soldiers to block enemy flow.
        /// </summary>
        public static bool IsChokepoint(IntVec3 cell, Map map)
        {
            int standable = 0;
            for (int i = 0; i < 4; i++)
            {
                IntVec3 adj = cell + GenAdj.CardinalDirections[i];
                if (adj.InBounds(map) && adj.Standable(map))
                    standable++;
            }
            return standable <= 2;
        }

        /// <summary>
        /// Get cover score at a position from a specific direction.
        /// Returns 0-1 representing block chance.
        /// </summary>
        public static float GetCoverScore(IntVec3 position, IntVec3 enemyPos, Map map)
        {
            return CoverUtility.CalculateOverallBlockChance(position, enemyPos, map);
        }

        /// <summary>
        /// Find a retreat position behind cover, away from enemies.
        /// Used at Level 9+ when health drops below threshold.
        /// </summary>
        public static IntVec3 FindRetreatPosition(Pawn soldier, IntVec3 threatDir,
            IntVec3 combatPost, Map map)
        {
            // Use combatPost as anchor, or soldier position if no post
            IntVec3 anchor = combatPost.IsValid ? combatPost : soldier.Position;

            // Direction away from threat
            IntVec3 awayDir;
            if (threatDir.IsValid)
            {
                awayDir = anchor - threatDir;
            }
            else
            {
                // No threat direction known -- retreat toward map center
                awayDir = map.Center - anchor;
            }

            IntVec3 bestCell = IntVec3.Invalid;
            float bestScore = float.MinValue;

            // Search cells within 10 tiles of anchor
            int radius = 10;
            int anchorX = anchor.x;
            int anchorZ = anchor.z;

            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    IntVec3 cell = new IntVec3(anchorX + dx, 0, anchorZ + dz);
                    if (!cell.InBounds(map)) continue;
                    if (!cell.Standable(map)) continue;

                    // Must be reachable quickly
                    float distFromSoldier = soldier.Position.DistanceTo(cell);
                    if (distFromSoldier > 15f) continue;

                    // Score: prefer cells in the "away from threat" direction
                    float awayScore = 0f;
                    if (awayDir.x != 0 || awayDir.z != 0)
                    {
                        // Dot product with away direction (normalized-ish)
                        float awayLen = UnityEngine.Mathf.Sqrt(awayDir.x * awayDir.x + awayDir.z * awayDir.z);
                        if (awayLen > 0.1f)
                        {
                            awayScore = (dx * awayDir.x + dz * awayDir.z) / awayLen;
                        }
                    }

                    // Cover score from threat direction
                    float coverScore = 0f;
                    if (threatDir.IsValid)
                    {
                        coverScore = CoverUtility.CalculateOverallBlockChance(cell, threatDir, map) * 20f;
                    }

                    // Prefer staying near anchor (don't run to the far side of the map)
                    float anchorDist = anchor.DistanceTo(cell);
                    float anchorPenalty = anchorDist * 0.5f;

                    float score = awayScore * 3f + coverScore - anchorPenalty;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestCell = cell;
                    }
                }
            }

            return bestCell;
        }
    }
}
