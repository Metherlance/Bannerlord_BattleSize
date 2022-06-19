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
   
    [HarmonyPatch(typeof(Agent))]
    class AgentPatch
    {
        private static Random rand = new Random();

        [HarmonyPostfix]
        [HarmonyPatch("Die")]
        static void Die(Agent __instance, Blow b, Agent.KillInfo overrideKillInfo = Agent.KillInfo.Invalid)
        {
            if (__instance.MountAgent!=null && __instance.Mission.AllAgents.Count>1024)
            {
                if (Settings.Instance.RiderDieMountFleeDie <= rand.NextFloat())
                {
                    __instance.MountAgent.Retreat();
                }
                else
                {
                    __instance.MountAgent.Die(b, overrideKillInfo);
                }
            }
        }
    }

}
