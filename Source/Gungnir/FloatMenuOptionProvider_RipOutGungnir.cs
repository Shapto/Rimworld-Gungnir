using RimWorld;
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
    /// Right-click option for colonists: rip Gungnir out of a pawn, or pull it out of a wall, once its wielder is gone (dead, downed, or not here).
    /// While the wielder could still catch it, only they can call it back.
    /// </summary>
    public class FloatMenuOptionProvider_RipOutGungnir : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;
        protected override bool Undrafted => true;
        protected override bool Multiselect => false;
        protected override bool RequiresManipulation => true;

        protected override bool AppliesInt(FloatMenuContext context) => context.FirstSelectedPawn?.IsColonistPlayerControlled == true;

        protected override FloatMenuOption GetSingleOptionFor(Pawn clickedPawn, FloatMenuContext context)
        {
            Hediff_GungnirDroning droning = clickedPawn.health?.hediffSet?.hediffs
                .OfType<Hediff_GungnirDroning>()
                .FirstOrDefault(hediff => hediff.LodgedGungnir != null);
            Pawn puller = context.FirstSelectedPawn;
            if (droning == null || (puller != droning.wielder && GungnirUtility.WielderCanCatch(droning.wielder, context.map))) return null;

            return MakeOption(puller, clickedPawn, "Gungnir_RipOutOfPawn".Translate(clickedPawn.LabelShort));
        }

        protected override FloatMenuOption GetSingleOption(FloatMenuContext context)
        {
            GungnirFlight stuckFlight = context.map.listerThings.ThingsOfDef(GungnirDefOf.Gungnir_Flight)
                .OfType<GungnirFlight>()
                .FirstOrDefault(flight => flight.IsStuck && (flight.Position == context.ClickedCell || flight.StuckWallCell == context.ClickedCell));
            Pawn puller = context.FirstSelectedPawn;
            if (stuckFlight == null || (puller != stuckFlight.Wielder && GungnirUtility.WielderCanCatch(stuckFlight.Wielder, context.map))) return null;

            return MakeOption(puller, stuckFlight, "Gungnir_PullOutOfWall".Translate());
        }

        /// <summary>
        /// The option itself: greyed out with "no path" if the colonist can't reach it.
        /// </summary>
        private static FloatMenuOption MakeOption(Pawn puller, Thing ripTarget, string label)
        {
            if (!puller.CanReach(ripTarget, PathEndMode.Touch, Danger.Deadly)) return new FloatMenuOption(label + ": " + "NoPath".Translate().CapitalizeFirst(), null);

            FloatMenuOption option = new FloatMenuOption(label, () => puller.jobs.TryTakeOrderedJob(JobMaker.MakeJob(GungnirDefOf.Gungnir_RipOut, ripTarget), JobTag.Misc));
            return FloatMenuUtility.DecoratePrioritizedTask(option, puller, ripTarget);
        }
    }
}
