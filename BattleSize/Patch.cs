using HarmonyLib;
using System;
using TaleWorlds.MountAndBlade;

namespace BattleSize
{
    [HarmonyPatch(typeof(MissionAgentSpawnLogic))]
    [HarmonyPatch("get_MaxNumberOfTroopsForMission")]
    class MissionAgentSpawnLogicPatchBS
    {
        static void Postfix(ref int __result)
        {
            // default is 1024 troops ( 2048 agents max / 2 (for cav) )
            __result = Math.Max(__result, Settings.Instance.RealBattleSize);
        }
    }


    [HarmonyPatch(typeof(BannerlordConfig))]
    [HarmonyPatch("GetRealBattleSize")]
    class BannerlordConfigPatchBS
    {
        static void Postfix(ref int __result)
        {
            __result = Settings.Instance.RealBattleSize;
        }
    }
}
