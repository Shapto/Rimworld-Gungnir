using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;
using Verse.AI;

namespace Gungnir
{
    /// <summary>
    /// Two seconds of tearing Gungnir out of a body - the puller's own, or someone else's if its wielder is gone.
    /// The wielder gets it back in hand; anyone else drops it beside them.
    /// </summary>
    public class JobDriver_RipOutGungnir : JobDriver
    {
        private const int RipOutTicks = 120;

        private Thing RipTarget => job.targetA.Thing;
        private Pawn ImpaledPawn => RipTarget as Pawn;
        private GungnirFlight StuckFlight => RipTarget as GungnirFlight;

        /// <summary>
        /// True while there's still a Gungnir here to rip out: lodged in the target pawn, or stuck in the target wall.
        /// </summary>
        private bool HasGungnirToRip => StuckFlight != null ? !StuckFlight.Destroyed && StuckFlight.IsStuck : FindLodgedDroning() != null;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return RipTarget == pawn || pawn.Reserve(RipTarget, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => !HasGungnirToRip);

            if (RipTarget != pawn) yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);

            Toil ripOut = Toils_General.Wait(RipOutTicks, RipTarget == pawn ? TargetIndex.None : TargetIndex.A);
            ripOut.WithProgressBarToilDelay(TargetIndex.A);
            yield return ripOut;

            yield return Toils_General.Do(FinishRipOut);
        }

        /// <summary>
        /// The Nested Harpoon still holding Gungnir in the target, or null once it's gone (ripped, recalled, or sent home).
        /// </summary>
        private Hediff_GungnirDroning FindLodgedDroning()
        {
            return ImpaledPawn?.health?.hediffSet?.hediffs
                .OfType<Hediff_GungnirDroning>()
                .FirstOrDefault(droning => droning.LodgedGungnir != null);
        }

        private void FinishRipOut()
        {
            Pawn wielder;
            Thing gungnir;

            if (StuckFlight != null)
            {
                wielder = StuckFlight.Wielder;
                gungnir = StuckFlight.PullOutOfWall();
            }
            else
            {
                Hediff_GungnirDroning droning = FindLodgedDroning();
                if (droning == null) return;
                wielder = droning.wielder;
                gungnir = droning.TearOut(false);
            }
            if (gungnir == null) return;

            GungnirUtility.RemoveOpenHand(wielder);

            bool pullerIsWielder = pawn == wielder && !pawn.Dead && pawn.equipment != null && pawn.equipment.Primary == null;
            if (pullerIsWielder) pawn.equipment.AddEquipment((ThingWithComps)gungnir);
            else GenPlace.TryPlaceThing(gungnir, pawn.PositionHeld, pawn.MapHeld, ThingPlaceMode.Near);
        }
    }
}
