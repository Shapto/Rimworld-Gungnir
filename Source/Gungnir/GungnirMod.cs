using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;
using HarmonyLib;

namespace Gungnir
{
    public class GungnirMod : Mod
    {
        public GungnirMod(ModContentPack content) : base(content)
        {
            new Harmony("shapto.gungnir").PatchAll();
        }
    }
}
