using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;

namespace Gungnir
{
    /// <summary>
    /// Whatever takes a pawn off the map (fleeing, caravans, pods, erasure), Gungnir doesn't go with them
    /// it tears free just before and flies home, or drops if its wielder can't catch it.
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.DeSpawn))]
    public static class Patch_Pawn_DeSpawn
    {
        public static void Prefix(Pawn __instance)
        {
            if (Patch_PawnCarryTracker_TryStartCarry.thingBeingPickedUp == __instance) return;
            if (__instance.health?.hediffSet == null) return;

            foreach (Hediff_GungnirDroning droning in __instance.health.hediffSet.hediffs.OfType<Hediff_GungnirDroning>().ToList())
                droning.ReleaseAndSendHome();
        }
    }
}
