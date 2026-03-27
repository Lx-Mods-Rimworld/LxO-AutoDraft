using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoDraft
{
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
                    GarrisonDebug.Log("[Garrison] " + pawn.LabelShort + " stripped " + target.LabelShort);
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

                GarrisonDebug.Log("[Garrison] " + pawn.LabelShort + " executing " + target.LabelShort);

                // Direct kill -- deal lethal damage
                target.Kill(new DamageInfo(DamageDefOf.Blunt, 999f, 999f, -1f, pawn));
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
                    GarrisonDebug.Log("[Garrison] " + pawn.LabelShort + " stripped " + target.LabelShort + " for capture");
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
                GarrisonDebug.Log("[Garrison] " + pawn.LabelShort + " captured " + target.LabelShort);
            };
            placeToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return placeToil;
        }
    }
}
