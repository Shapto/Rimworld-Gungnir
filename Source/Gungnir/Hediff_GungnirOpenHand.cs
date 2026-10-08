using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;

namespace Gungnir
{
    /// <summary>
    /// The wielder's side of a thrown Gungnir. Grants Recall (through its GiveAbility comp) and remembers where Gungnir is:
    /// its flight, in the air or stuck in a wall, or the pawn it's lodged in.
    /// </summary>
    public class Hediff_GungnirOpenHand : HediffWithComps
    {
        public Thing gungnirLocation;

        public override bool ShouldRemove => false;

        public override string TipStringExtra
        {
            get
            {
                StringBuilder builder = new StringBuilder();
                string baseTip = base.TipStringExtra;
                if (!baseTip.NullOrEmpty()) builder.AppendLine(baseTip);

                if (gungnirLocation is Pawn impaledPawn) builder.AppendLine("Gungnir_LocationLodged".Translate(impaledPawn.LabelShort));
                else if (gungnirLocation is GungnirFlight flight && flight.IsStuck) builder.AppendLine("Gungnir_LocationStuck".Translate());
                else if (gungnirLocation is GungnirFlight returningFlight && returningFlight.IsReturning) builder.AppendLine("Gungnir_LocationReturning".Translate());
                else if (gungnirLocation is GungnirFlight) builder.AppendLine("Gungnir_LocationInFlight".Translate());

                return builder.ToString().TrimEndNewlines();
            }
        }
        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref gungnirLocation, "gungnirLocation");
        }
    }
}

