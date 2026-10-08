using RimWorld;
using SingularityFramework.Relics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;

namespace Gungnir
{
    public class CompProperties_AbilityGungnirRecall : CompProperties_AbilityEffect
    {
        public CompProperties_AbilityGungnirRecall() => compClass = typeof(CompAbilityEffect_GungnirRecall);
    }

    /// <summary>
    /// "Return to me." Calls Gungnir home: tears it out of whoever it's lodged in, or turns its flight around.
    /// </summary>
    public class CompAbilityEffect_GungnirRecall : CompAbilityEffect
    {
        /// <summary>
        /// Where the caster's Gungnir is right now: its flight, the pawn it's lodged in, or null if it's nowhere to call back from.
        /// </summary>
        private Thing GungnirLocation => GungnirUtility.GetOpenHand(parent.pawn)?.gungnirLocation;

        public override void Apply(LocalTargetInfo target, LocalTargetInfo dest)
        {
            base.Apply(target, dest);

            Pawn wielder = parent.pawn;
            Thing location = GungnirLocation;

            if (location is GungnirFlight flight)
            {
                flight.BeginReturn(wielder);
                return;
            }

            if (location is Pawn impaledPawn)
            {
                Hediff_GungnirDroning droning = impaledPawn.health.hediffSet.hediffs
                    .OfType<Hediff_GungnirDroning>()
                    .FirstOrDefault(hediff => hediff.LodgedGungnir != null);
                if (droning == null) return;

                IntVec3 startCell = impaledPawn.PositionHeld;
                Map map = impaledPawn.MapHeld;
                Thing gungnir = droning.RipOutForRecall();
                GungnirFlight.SendHome(gungnir, wielder, startCell, map, impaledPawn);
            }
        }

        public override bool GizmoDisabled(out string reason)
        {
            Thing location = GungnirLocation;

            if (location == null || location.Destroyed)
            {
                reason = "Gungnir_RecallNowhere".Translate();
                return true;
            }

            if (location.MapHeld != parent.pawn.Map)
            {
                reason = "Gungnir_RecallOtherMap".Translate();
                return true;
            }

            if (location is GungnirFlight flight && flight.IsReturning)
            {
                reason = "Gungnir_RecallAlreadyReturning".Translate();
                return true;
            }
            if (location == parent.pawn)
            {
                reason = "Gungnir_RecallLodgedInSelf".Translate();
                return true;
            }

            return base.GizmoDisabled(out reason);
        }

        public override string ExtraTooltipPart()
        {
            CompProperties_RelicAptitude aptitudeProperties = GungnirDefOf.Gungnir.GetCompProperties<CompProperties_RelicAptitude>();
            if (aptitudeProperties == null) return null;

            return RelicAptitude.DescribeAptitude(parent.pawn, aptitudeProperties, GungnirDefOf.Gungnir);
        }
    }
}
