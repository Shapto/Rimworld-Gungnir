using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Gungnir
{
    /// <summary>
    /// Gungnir lodged in a body part. Holds the weapon itself, so it saves and travels with the pawn.
    /// Severity is the droning - every hit the pawn takes adds to it, and the pawn takes that much more damage.
    /// </summary>
    public class Hediff_GungnirDroning : HediffWithComps, IThingHolder
    {
        private const float DroningPerHit = 0.01f;

        /// <summary>
        /// Whoever threw Gungnir. Recall and auto-return send it back to them.
        /// </summary>
        public Pawn wielder;

        private ThingOwner<Thing> heldGungnir;

        /// <summary>
        /// The XML stage plus a live IncomingDamageFactor, rebuilt once and updated as droning grows.
        /// </summary>
        private HediffStage droningStage;
        private StatModifier incomingDamageModifier;

        public Hediff_GungnirDroning()
        {
            heldGungnir = new ThingOwner<Thing>(this, true);
        }

        /// <summary>
        /// The Gungnir lodged in this pawn, or null if it has none (e.g. added through dev mode).
        /// </summary>
        public Thing LodgedGungnir => heldGungnir.Count > 0 ? heldGungnir[0] : null;

        /// <summary>
        /// Puts Gungnir inside this hediff and remembers who threw it.
        /// </summary>
        public void LodgeGungnir(Thing gungnir, Pawn thrower)
        {
            wielder = thrower;

            if (gungnir.ParentHolder is Pawn_EquipmentTracker equipmentTracker) equipmentTracker.Remove((ThingWithComps)gungnir);
            else if (gungnir.Spawned) gungnir.DeSpawn();
            else gungnir.holdingOwner?.Remove(gungnir);

            heldGungnir.TryAdd(gungnir);
        }

        public override bool ShouldRemove => false;

        public override string LabelInBrackets => Severity.ToStringPercent();

        /// <summary>
        /// An unworthy wielder impaled by their own catch gets a button to tear Gungnir out of themselves.
        /// </summary>
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo baseGizmo in base.GetGizmos())
                yield return baseGizmo;

            if (pawn != wielder || LodgedGungnir == null || !pawn.IsColonistPlayerControlled) yield break;

            Command_Action ripOutCommand = new Command_Action
            {
                defaultLabel = "Gungnir_RipOutLabel".Translate(),
                defaultDesc = "Gungnir_RipOutDescription".Translate(),
                icon = GungnirTextures.RipOutIcon,
                action = () => pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(GungnirDefOf.Gungnir_RipOut, pawn), JobTag.Misc)
            };
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) ripOutCommand.Disable("Gungnir_RipOutNoHands".Translate());
            else if (pawn.Downed) ripOutCommand.Disable("Gungnir_RipOutDowned".Translate());
            else if (pawn.CurJobDef == GungnirDefOf.Gungnir_RipOut) ripOutCommand.Disable("Gungnir_RipOutAlready".Translate());

            yield return ripOutCommand;
        }

        public override HediffStage CurStage
        {
            get
            {
                if (droningStage == null)
                {
                    droningStage = new HediffStage();
                    droningStage.statFactors = new List<StatModifier>(def.stages[0].statFactors);
                    incomingDamageModifier = new StatModifier { stat = StatDefOf.IncomingDamageFactor, value = 1f };
                    droningStage.statFactors.Add(incomingDamageModifier);
                }
                incomingDamageModifier.value = 1f + Severity;
                return droningStage;
            }
        }

        public override void Notify_PawnPostApplyDamage(DamageInfo dinfo, float totalDamageDealt)
        {
            base.Notify_PawnPostApplyDamage(dinfo, totalDamageDealt);
            if (!pawn.Dead && dinfo.Def.harmsHealth && totalDamageDealt > 0f)
            {
                Severity += DroningPerHit;
            }
        }

        public IThingHolder ParentHolder => pawn;

        public ThingOwner GetDirectlyHeldThings() => heldGungnir;

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref wielder, "wielder");
            Scribe_Deep.Look(ref heldGungnir, "heldGungnir", this);
        }

        /// <summary>
        /// Tears Gungnir out of this pawn. The lodged part takes the leaving spike, amplified by droning (still present while the hit lands);
        /// a recall also adds +50% per kind of current harm. Droning ends with it. Returns Gungnir, held by nobody.
        /// </summary>
        public Thing TearOut(bool isRecall)
        {
            Thing gungnir = LodgedGungnir;
            if (gungnir == null) return null;

            heldGungnir.Remove(gungnir);

            float leavingDamage = isRecall ? GungnirUtility.GungnirDamage * GungnirUtility.NegativeEffectFactor(pawn) : GungnirUtility.GungnirDamage;
            BodyPartRecord lodgedPart = Part != null && !pawn.health.hediffSet.PartIsMissing(Part) ? Part : null;
            float leavingAngle = wielder != null ? (wielder.Position - pawn.Position).AngleFlat : 0f;
            pawn.TakeDamage(GungnirUtility.MakeGungnirDamage(wielder, leavingAngle, leavingDamage, lodgedPart));

            if (pawn.health.hediffSet.hediffs.Contains(this)) pawn.health.RemoveHediff(this);
            return gungnir;
        }

        /// <summary>
        /// The pawn died with Gungnir inside: it tears free and flies home on its own.
        /// </summary>
        public override void Notify_PawnDied(DamageInfo? dinfo, Hediff culprit = null)
        {
            base.Notify_PawnDied(dinfo, culprit);
            ReleaseAndSendHome();
        }

        /// <summary>
        /// Removed while Gungnir is still inside (the lodged part was destroyed, or dev mode): it flies home with no leaving damage.
        /// </summary>
        public override void PostRemoved()
        {
            base.PostRemoved();
            ReleaseAndSendHome();
        }

        /// <summary>
        /// Takes Gungnir out and sends it back to the wielder, or drops it if nobody can catch it.
        /// </summary>
        public void ReleaseAndSendHome()
        {
            Thing gungnir = LodgedGungnir;
            if (gungnir == null || pawn.MapHeld == null) return;

            heldGungnir.Remove(gungnir);

            if (pawn == wielder)
            {
                GenPlace.TryPlaceThing(gungnir, pawn.PositionHeld, pawn.MapHeld, ThingPlaceMode.Near);
                GungnirUtility.RemoveOpenHand(wielder);
                return;
            }

            GungnirFlight.SendHome(gungnir, wielder, pawn.PositionHeld, pawn.MapHeld, pawn);
        }
    }
}
