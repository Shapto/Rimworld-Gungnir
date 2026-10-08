using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;

namespace Gungnir
{
    public class CompProperties_AbilityGungnirThrow : CompProperties_AbilityEffect
    {
        public CompProperties_AbilityGungnirThrow() => compClass = typeof(CompAbilityEffect_GungnirThrow);
    }

    /// <summary>
    /// Throws the caster's Gungnir at the target.
    /// </summary>
    public class CompAbilityEffect_GungnirThrow : CompAbilityEffect
    {
        public override void Apply(LocalTargetInfo target, LocalTargetInfo dest)
        {
            base.Apply(target, dest);

            Pawn thrower = parent.pawn;
            ThingWithComps gungnir = thrower?.equipment?.Primary;
            if (gungnir == null || gungnir.def != GungnirDefOf.Gungnir || target.Pawn == null) return;

            GungnirFlight.LaunchThrow(thrower, gungnir, target.Pawn);
        }
    }
}
