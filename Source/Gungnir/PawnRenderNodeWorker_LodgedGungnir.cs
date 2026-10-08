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
    /// Draws Gungnir run through the pawn, behind everything: the tip pokes out one side and the shaft the other.
    /// Facing west, the body mesh mirrors it automatically.
    /// </summary>
    public class PawnRenderNodeWorker_LodgedGungnir : PawnRenderNodeWorker
    {
        /// <summary>
        /// The texture's tip points up-right (45°), the same angle equippedAngleOffset -45 corrects for when held.
        /// </summary>
        private const float TextureTipAngle = 45f;

        /// <summary>
        /// Where the tip should point while lodged: down-left (225°). The west-facing mirror comes from the body mesh.
        /// </summary>
        private const float LodgedTipAngle = 225f;

        public override bool CanDrawNow(PawnRenderNode node, PawnDrawParms parms)
        {
            if (!base.CanDrawNow(node, parms)) return false;
            return node.hediff is Hediff_GungnirDroning droning && droning.LodgedGungnir != null;
        }

        public override Quaternion RotationFor(PawnRenderNode node, PawnDrawParms parms) => base.RotationFor(node, parms) * Quaternion.AngleAxis(LodgedTipAngle - TextureTipAngle, Vector3.up);
    }
}
