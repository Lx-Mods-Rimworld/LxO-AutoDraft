using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoDraft
{
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

        // Cached combat level from LearnToSurvive integration
        private int cachedCombatLevel = -1;
        private int combatLevelCacheTick = -1;

        public int CombatLevel
        {
            get
            {
                int tick = Find.TickManager.TicksGame;
                if (tick - combatLevelCacheTick > 2500 || cachedCombatLevel < 0)
                {
                    cachedCombatLevel = ModIntegration.GetCombatLevel(Pawn);
                    combatLevelCacheTick = tick;
                }
                return cachedCombatLevel;
            }
        }

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
                toggleAction = () =>
                    {
                        isSoldier = !isSoldier;
                        if (isSoldier && Pawn.playerSettings != null)
                        {
                            Pawn.playerSettings.hostilityResponse = HostilityResponseMode.Attack;
                        }
                    },
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
}
