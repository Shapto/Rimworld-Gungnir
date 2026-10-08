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
    /// Every texture this mod's code uses, loaded once at startup.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class GungnirTextures
    {
        public static readonly Texture2D RipOutIcon = ContentFinder<Texture2D>.Get("UI/Gungnir_RipOut");
    }
}
