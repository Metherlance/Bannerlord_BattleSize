using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BattleSize
{
    [HarmonyPatch(typeof(BannerlordConfig))]
    class BannerlordConfigPatch
    {
        [HarmonyPostfix]
        [HarmonyPatch("GetRealBattleSize")]
        static void GetRealBattleSize(ref int __result)
        {
            // 1000 default max
            __result = Settings.Instance.RealBattleSize;
        }

        [HarmonyPostfix]
        [HarmonyPatch("GetRealBattleSizeForSiege")]
        static void GetRealBattleSizeForSiege(ref int __result)
        {
            // 1000 default max
            __result = Settings.Instance.RealBattleSize;
        }

        [HarmonyPostfix]
        [HarmonyPatch("GetRealBattleSizeForSallyOut")]
        static void GetRealBattleSizeForSallyOut(ref int __result)
        {
            // 1000 default max
            __result = Settings.Instance.RealBattleSize;
        }
    }

}
