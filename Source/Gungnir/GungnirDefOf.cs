using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;

namespace Gungnir
{
    /// <summary>
    /// Direct references to this mod's defs, filled in by the game on load.
    /// </summary>
    [DefOf]
    public static class GungnirDefOf
    {
        public static ThingDef Gungnir;
        public static ThingDef Gungnir_Flight;
        public static HediffDef Gungnir_Droning;
        public static HediffDef Gungnir_OpenHand;
        public static HediffDef Gungnir_Reverberation;
        public static JobDef Gungnir_RipOut;
        public static SoundDef Gungnir_Impact;
        public static SoundDef Gungnir_Pierce;
        public static SoundDef Gungnir_Leave;
        public static SoundDef Gungnir_Catch;
        public static FleckDef Gungnir_FleckStreak;
        public static FleckDef Gungnir_FleckBurst;
        static GungnirDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(GungnirDefOf));
    }
}
