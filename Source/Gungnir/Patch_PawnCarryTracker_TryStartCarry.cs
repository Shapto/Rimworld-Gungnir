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
    /// Marks the thing a pawn is picking up, so the DeSpawn patch knows a carried pawn isn't leaving and keeps Gungnir in them.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.TryStartCarry), new[] { typeof(Thing), typeof(int), typeof(bool) })]
    public static class Patch_PawnCarryTracker_TryStartCarry
    {
        public static Thing thingBeingPickedUp;

        public static void Prefix(Thing item) => thingBeingPickedUp = item;

        public static void Finalizer() => thingBeingPickedUp = null;
    }
}
