using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;

namespace Gungnir
{
    /// <summary>
    /// Gungnir's sprite on a pawn it's lodged in. Weapon textures are one image, not the four-sided set pawn parts use.
    /// </summary>
    public class PawnRenderNode_LodgedGungnir : PawnRenderNode
    {
        public PawnRenderNode_LodgedGungnir(Pawn pawn, PawnRenderNodeProperties props, PawnRenderTree tree) : base(pawn, props, tree) { }

        public override Graphic GraphicFor(Pawn pawn) => GraphicDatabase.Get<Graphic_Single>(props.texPath, ShaderDatabase.Cutout);
    }
}
