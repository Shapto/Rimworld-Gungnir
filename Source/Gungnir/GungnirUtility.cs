using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace Gungnir
{
    /// <summary>
    /// Shared Gungnir rules: its damage, the recall's negative-effect bonus, and the wielder's Open hand hediff.
    /// </summary>
    public static class GungnirUtility
    {
        public const float GungnirDamage = 48f;
        public const float GungnirArmorPenetration = 1.6f;
        private const float BonusPerNegativeEffectType = 0.5f;
        private const float MaximumNegativeEffectBonus = 2f;

        /// <summary>
        /// A stab from Gungnir's speartip, credited to its wielder. Optionally aimed at one body part.
        /// </summary>
        public static DamageInfo MakeGungnirDamage(Pawn instigator, float hitAngle, float damageAmount = GungnirDamage, BodyPartRecord hitPart = null)
        {
            return new DamageInfo(DamageDefOf.Stab, damageAmount, GungnirArmorPenetration, hitAngle, instigator, hitPart, GungnirDefOf.Gungnir);
        }

        /// <summary>
        /// How many different kinds of current harm the pawn has. Permanent harm (scars, missing parts, chronic conditions) doesn't count.
        /// </summary>
        public static int CountNegativeEffectTypes(Pawn pawn)
        {
            HashSet<HediffDef> negativeEffectTypes = new HashSet<HediffDef>();
            foreach (Hediff hediff in pawn.health.hediffSet.hediffs)
            {
                if (!hediff.def.isBad || hediff.def.chronic || hediff is Hediff_MissingPart || hediff.IsPermanent()) continue;
                negativeEffectTypes.Add(hediff.def);
            }
            return negativeEffectTypes.Count;
        }

        /// <summary>
        /// The recall's damage multiplier: +50% per kind of current harm on the pawn, up to +200%.
        /// </summary>
        public static float NegativeEffectFactor(Pawn pawn) => 1f + Mathf.Min(BonusPerNegativeEffectType * CountNegativeEffectTypes(pawn), MaximumNegativeEffectBonus);

        public static Hediff_GungnirOpenHand GetOpenHand(Pawn wielder) => wielder?.health?.hediffSet?.GetFirstHediffOfDef(GungnirDefOf.Gungnir_OpenHand) as Hediff_GungnirOpenHand;

        /// <summary>
        /// Tells the wielder's Open hand where Gungnir is now, giving them the hediff if they don't have it yet.
        /// </summary>
        public static void SetGungnirLocation(Pawn wielder, Thing location)
        {
            if (wielder?.health == null) return;

            Hediff_GungnirOpenHand openHand = GetOpenHand(wielder);
            if (openHand == null)
            {
                openHand = (Hediff_GungnirOpenHand)HediffMaker.MakeHediff(GungnirDefOf.Gungnir_OpenHand, wielder);
                wielder.health.AddHediff(openHand);
            }
            openHand.gungnirLocation = location;
        }

        /// <summary>
        /// A partial-aptitude catch: Reverberation goes on an arm that doesn't have it yet.
        /// If every arm already has it, one of them (picked at random) gets its 30 seconds back instead.
        /// </summary>
        public static void ApplyReverberation(Pawn pawn)
        {
            List<BodyPartRecord> arms = pawn.health.hediffSet.GetNotMissingParts()
                .Where(part => part.def == BodyPartDefOf.Arm)
                .ToList();
            if (arms.Count == 0) return;

            List<Hediff> existingReverberations = pawn.health.hediffSet.hediffs
                .Where(hediff => hediff.def == GungnirDefOf.Gungnir_Reverberation)
                .ToList();

            List<BodyPartRecord> freeArms = arms
                .Where(arm => !existingReverberations.Any(hediff => hediff.Part == arm))
                .ToList();
            if (freeArms.Count > 0)
            {
                pawn.health.AddHediff(GungnirDefOf.Gungnir_Reverberation, freeArms.RandomElement());
                return;
            }

            HediffComp_Disappears disappearsComp = existingReverberations.RandomElement().TryGetComp<HediffComp_Disappears>();
            if (disappearsComp != null) disappearsComp.ticksToDisappear = disappearsComp.Props.disappearsAfterTicks.RandomInRange;
        }

        /// <summary>
        /// Gungnir is back in a hand or on the ground: the wielder no longer has an open hand, and loses Recall.
        /// </summary>
        public static void RemoveOpenHand(Pawn wielder)
        {
            Hediff_GungnirOpenHand openHand = GetOpenHand(wielder);
            if (openHand != null) wielder.health.RemoveHediff(openHand);
        }
    }
}
