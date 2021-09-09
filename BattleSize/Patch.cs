using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Missions.Handlers;

namespace BattleSize
{
    [HarmonyPatch(typeof(BannerlordConfig))]
    [HarmonyPatch("GetRealBattleSize")]
    class BannerlordConfigPatchBS
    {
        static void Postfix(ref int __result)
        {
            // 1000 default max
            __result = Settings.Instance.RealBattleSize;
        }
    }

    [HarmonyPatch(typeof(MissionAgentSpawnLogic))]
    class MissionAgentSpawnLogicPatchBS
    {
        static readonly Assembly assem = typeof(MissionAgentSpawnLogic).Assembly;
        static readonly Type typeMissionSide = assem.GetType("TaleWorlds.MountAndBlade.MissionAgentSpawnLogic+MissionSide");
        static readonly MethodInfo methodMissionSideSpawnTroops = typeMissionSide.GetMethod("SpawnTroops");


        [HarmonyPostfix]
        [HarmonyPatch("get_MaxNumberOfTroopsForMission")]
        static void Postfix(ref int __result)
        {
            // default is 1024 troops ( 2048 agents max / 2 (for cav) )
            __result = Math.Max(__result, Settings.Instance.RealBattleSize);
        }


        private class SpawnPhase
        {
            public int TotalSpawnNumber;

            public int InitialSpawnedNumber;

            public int InitialSpawnNumber;

            public int RemainingSpawnNumber;

            public int NumberActiveTroops;

            public void OnInitialTroopsSpawned()
            {
                InitialSpawnedNumber = InitialSpawnNumber;
                InitialSpawnNumber = 0;
            }
        }

        private class MissionSide
        {
            private readonly BattleSideEnum _side;
            private readonly IMissionTroopSupplier _troopSupplier;
            private bool _spawnWithHorses;
            private readonly MBList<Formation> _spawnedFormations;
            private List<IAgentOriginBase> _preSuppliedTroops;
            public bool TroopSpawningActive { get; private set; }
            public bool IsPlayerSide { get; }

            public int SpawnTroops(int number, bool isReinforcement, bool enforceSpawningOnInitialPoint = false)
            {
                if (number <= 0 || Mission.Current.AllAgents.Count >= Settings.ENTITY_ENGINE_MAX)
                {
                    return 0;
                }

                int nbSpawnFromPreSupplied = MathF.Min(_preSuppliedTroops.Count, number);
                List<IAgentOriginBase> listAgentBase = new List<IAgentOriginBase>(number);
                if (nbSpawnFromPreSupplied > 0)
                {
                    for (int i = 0; i < nbSpawnFromPreSupplied; i++)
                    {
                        listAgentBase.Add(_preSuppliedTroops[i]);
                    }

                    _preSuppliedTroops.RemoveRange(0, nbSpawnFromPreSupplied);
                }

                int numberToAllocate = number - nbSpawnFromPreSupplied;
                listAgentBase.AddRange(SupplyTroopsCustom(numberToAllocate, Settings.ENTITY_ENGINE_MAX - Mission.Current.AllAgents.Count).Item1);

                int nbSpawned = 0;
                List<IAgentOriginBase> listAgentInFormationToSpawn = new List<IAgentOriginBase>();
                int nbTroopMount = 0;
                int nbTroopNoMount = 0;
                List<(IAgentOriginBase, FormationClass)> troopFormationAssignments = MissionGameModels.Current.BattleInitializationModel.GetTroopFormationAssignments(listAgentBase, isReinforcement);
                for (int j = 0; j < 8; j++)
                {
                    listAgentInFormationToSpawn.Clear();
                    IAgentOriginBase agentOriginBase = null;
                    FormationClass formationClass = (FormationClass)j;
                    foreach (var troopFormationAssignment in troopFormationAssignments)
                    {
                        IAgentOriginBase troopAssigned = troopFormationAssignment.Item1;
                        FormationClass formationAssigned = troopFormationAssignment.Item2;
                        if (formationClass != formationAssigned)
                        {
                            continue;
                        }

                        if (troopAssigned.Troop == Game.Current.PlayerTroop)
                        {
                            agentOriginBase = troopAssigned;
                            continue;
                        }

                        if (troopAssigned.Troop.HasMount())
                        {
                            nbTroopMount++;
                        }
                        else
                        {
                            nbTroopNoMount++;
                        }

                        listAgentInFormationToSpawn.Add(troopAssigned);
                    }

                    if (agentOriginBase != null)
                    {
                        if (agentOriginBase.Troop.HasMount())
                        {
                            nbTroopMount++;
                        }
                        else
                        {
                            nbTroopNoMount++;
                        }

                        listAgentInFormationToSpawn.Add(agentOriginBase);
                    }

                    int nbAgentInFormationToSpawn = listAgentInFormationToSpawn.Count;
                    if (nbAgentInFormationToSpawn <= 0)
                    {
                        continue;
                    }

                    bool isMounted = _spawnWithHorses && MissionDeploymentPlan.HasSignificantMountedTroops(nbTroopNoMount, nbTroopMount);
                    Formation formation = Mission.GetAgentTeam(listAgentInFormationToSpawn[0], IsPlayerSide).GetFormation(formationClass);
                    foreach (IAgentOriginBase agentBaseToSpawn in listAgentInFormationToSpawn)
                    {
                        if (formation != null && !formation.HasBeenPositioned)
                        {
                            formation.BeginSpawn(nbAgentInFormationToSpawn, isMounted);
                            Mission.Current.SpawnFormation(formation);
                            _spawnedFormations.Add(formation);
                        }

                        Mission.Current.SpawnTroop(agentBaseToSpawn, IsPlayerSide, hasFormation: true, _spawnWithHorses, isReinforcement, enforceSpawningOnInitialPoint, nbAgentInFormationToSpawn, nbSpawned, isAlarmed: true, wieldInitialWeapons: true, forceDismounted: false, null, null);

                    }
                    nbSpawned += listAgentInFormationToSpawn.Count;
                }

                if (nbSpawned > 0)
                {
                    foreach (Team team in Mission.Current.Teams)
                    {
                        team.QuerySystem.Expire();
                    }

                    Debug.Print(string.Concat(nbSpawned, " troops spawned on  side."), 0, Debug.DebugColor.DarkGreen, 64uL);
                }

                foreach (Team team2 in Mission.Current.Teams)
                {
                    foreach (Formation formation2 in team2.Formations)
                    {
                        formation2.GroupSpawnIndex = 0;
                    }
                }

                return nbSpawned;
            }
            public void GetFormationSpawnData(MissionAgentSpawnLogic.FormationSpawnData[] formationSpawnData)
            {
                if (formationSpawnData == null || formationSpawnData.Length != 11)
                    return;
                for (int index = 0; index < formationSpawnData.Length; ++index)
                {
                    formationSpawnData[index].FootTroopCount = 0;
                    formationSpawnData[index].MountedTroopCount = 0;
                }
                foreach (IAgentOriginBase preSuppliedTroop in this._preSuppliedTroops)
                {
                    FormationClass formationClass = preSuppliedTroop.Troop.GetFormationClass(preSuppliedTroop.BattleCombatant);
                    if (preSuppliedTroop.Troop.HasMount())
                        ++formationSpawnData[(int)formationClass].MountedTroopCount;
                    else
                        ++formationSpawnData[(int)formationClass].FootTroopCount;
                }
            }

            public void OnInitialSpawnOver()
            {
                foreach (Formation spawnedFormation in this._spawnedFormations)
                    spawnedFormation.EndSpawn();
            }

            public (IEnumerable<IAgentOriginBase>,int) SupplyTroopsCustom(int nbAgentToSpawn, int nbEntitySpawnableLeft)
            {
                List<IAgentOriginBase> retListAgentSupply = new List<IAgentOriginBase>(nbAgentToSpawn);
                if (nbAgentToSpawn <= 0)
                    return (retListAgentSupply, nbEntitySpawnableLeft);

                int nbEntitySpawnable = nbEntitySpawnableLeft;
                while(nbEntitySpawnable>1 && nbAgentToSpawn > 0)
                {
                    int nbAgentSafeSupply = Math.Min(nbAgentToSpawn, nbEntitySpawnable / 2);
                    IEnumerable<IAgentOriginBase> listAgentSupply = _troopSupplier.SupplyTroops(nbAgentSafeSupply);
                    retListAgentSupply.AddRange(listAgentSupply);
                    nbAgentToSpawn -= nbAgentSafeSupply;
                    nbEntitySpawnable -= nbAgentSafeSupply;

                    if (_spawnWithHorses)
                    {
                        foreach (IAgentOriginBase agentSupply in listAgentSupply)
                        {
                            if (agentSupply.Troop.HasMount())
                            {
                                nbEntitySpawnable -= 1;
                            }
                        }
                    }
                    else{
                        foreach (IAgentOriginBase agentSupply in listAgentSupply)
                        {
                            if (agentSupply.Troop.HasMount() && agentSupply.Troop.IsHero)
                            {
                                nbEntitySpawnable -= 1;
                            }
                        }
                    }                   
                }

                return (retListAgentSupply, nbEntitySpawnable);
            }

            public (int,int) PreSupplyTroopsCustom(int nbAgentToSpawn, int nbEntitySpawnableLeft)
            {
                if (nbAgentToSpawn <= 0)
                    return (0,nbEntitySpawnableLeft);
                (IEnumerable<IAgentOriginBase> listAgentSupply,int nbEntitySpawnable) = SupplyTroopsCustom(nbAgentToSpawn, nbEntitySpawnableLeft);
                _preSuppliedTroops.AddRange(listAgentSupply);

                return (listAgentSupply.Count(), nbEntitySpawnable);
            }

            internal bool hasPreSupply()
            {
                return _preSuppliedTroops.Count()>0;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch("BattleSizeSpawnTick")]
        static bool Prefix(MissionAgentSpawnLogic __instance, ref int ____battleSize, ref List<SpawnPhase>[] ____phases, ref MissionSide[] ____missionSides)
        {
            // Don't use NumActiveTroops of IMissionTroopSupplier, it takes allocated troop not used !!! ...

            SpawnPhase DefenderActivePhase = ____phases[0][0];
            SpawnPhase AttackerActivePhase = ____phases[1][0];
            int TotalSpawnNumber = DefenderActivePhase.RemainingSpawnNumber + AttackerActivePhase.RemainingSpawnNumber;

            int numberOfTroopsCanBeSpawned = __instance.NumberOfTroopsCanBeSpawned;
            if (TotalSpawnNumber <= 0 || numberOfTroopsCanBeSpawned <= 0)
            {
                return false;
            }

            int nbAgentSpawnable = Settings.Instance.RealBattleSize - __instance.Mission.Agents.Count;
            int nbAgentSpawnMin = (int)(((float)____battleSize) * 0.100000001490116);

            // || last troop need to spawn
            if (nbAgentSpawnable > nbAgentSpawnMin || nbAgentSpawnable > DefenderActivePhase.RemainingSpawnNumber || nbAgentSpawnable > AttackerActivePhase.RemainingSpawnNumber)
            {
                int nbSpawnableDef;
                int nbSpawnableAtt;
                if (TotalSpawnNumber > nbAgentSpawnable)
                {
                    nbSpawnableDef = DefenderActivePhase.RemainingSpawnNumber * nbAgentSpawnable / TotalSpawnNumber;
                    nbSpawnableAtt = nbAgentSpawnable - nbSpawnableDef;
                }
                else
                {
                    nbSpawnableDef = DefenderActivePhase.RemainingSpawnNumber;
                    nbSpawnableAtt = AttackerActivePhase.RemainingSpawnNumber;
                }

                // 1 vs 2 max if renforcement
                int defUnitActiveAndSpawn = nbSpawnableDef + DefenderActivePhase.NumberActiveTroops;
                int attUnitActiveAndSpawn = nbSpawnableAtt + AttackerActivePhase.NumberActiveTroops;
                // -50 for tiny battle
                if (nbSpawnableAtt > 0 && defUnitActiveAndSpawn - 50 > attUnitActiveAndSpawn * 2)
                {
                    nbSpawnableDef = 0;
                }
                if (nbSpawnableDef > 0 && attUnitActiveAndSpawn - 50 > defUnitActiveAndSpawn * 2)
                {
                    nbSpawnableAtt = 0;
                }

                //horse 2 times for att in case of engine limit
                if (nbSpawnableAtt > 0)
                {
                    int spawned = (int)____missionSides[1].SpawnTroops(nbSpawnableAtt / 2, true, true);
                    AttackerActivePhase.RemainingSpawnNumber -= spawned;
                    nbSpawnableAtt -= spawned;
                }
                if (nbSpawnableDef > 0)
                {
                    DefenderActivePhase.RemainingSpawnNumber -= (int)____missionSides[0].SpawnTroops(nbSpawnableDef, true, true);

                }
                if (nbSpawnableAtt > 0)
                {
                    AttackerActivePhase.RemainingSpawnNumber -= (int)____missionSides[1].SpawnTroops(nbSpawnableAtt, true, true);
                }
            }
            return false;
        }




        [HarmonyPrefix]
        [HarmonyPatch("CheckInitialSpawns")]
        static bool CheckInitialSpawns(ref bool __result, MissionAgentSpawnLogic __instance, ref List<SpawnPhase>[] ____phases, ref MissionAgentSpawnLogic.FormationSpawnData[] ____formationSpawnData,
            ref MissionSide[] ____missionSides)
        {
            if (!__instance.IsInitialSpawnOver)
            {
                SpawnPhase DefenderActivePhase = ____phases[0][0];
                SpawnPhase AttackerActivePhase = ____phases[1][0];
                int sizeForActivePhase = MathF.Max(DefenderActivePhase.TotalSpawnNumber, AttackerActivePhase.TotalSpawnNumber);
                if (sizeForActivePhase > 0)
                {

                    // presuplly
                    if (!____missionSides[0].hasPreSupply() && !____missionSides[1].hasPreSupply())
                    {
                        int nbEntitySpawnableLeft = Settings.ENTITY_ENGINE_MAX - __instance.Mission.AllAgents.Count();
                        int nbAgentSpawnableLeft  = Settings.Instance.RealBattleSize - __instance.Mission.Agents.Count();

                        //ratio
                        int TotalSpawnNumber = DefenderActivePhase.TotalSpawnNumber + AttackerActivePhase.TotalSpawnNumber;
                        int[] tabSpawnLeft = new int[2];
                        if (TotalSpawnNumber > nbAgentSpawnableLeft)
                        {
                            tabSpawnLeft[0] = DefenderActivePhase.TotalSpawnNumber * nbAgentSpawnableLeft / TotalSpawnNumber;
                            tabSpawnLeft[1] = nbAgentSpawnableLeft - tabSpawnLeft[0];
                        }
                        else
                        {
                            tabSpawnLeft[0] = DefenderActivePhase.TotalSpawnNumber;
                            tabSpawnLeft[1] = AttackerActivePhase.TotalSpawnNumber;
                        }

                        int[] tabSpawned = new int[2];
                        while (nbEntitySpawnableLeft>1 && (tabSpawnLeft[0] > 0 || tabSpawnLeft[1] > 0))
                        {
                            //try to have same size by one loop
                            int nbEntitySpawmableLeftForSide = nbEntitySpawnableLeft / 4;
                            if (tabSpawnLeft[0]==0 || tabSpawnLeft[1] == 0)
                            {
                                nbEntitySpawmableLeftForSide = nbEntitySpawnableLeft;
                            }
                            //for each teams
                            for (int indexSide = 0;indexSide<2;indexSide+=1)
                            {
                                int nbTrySpawn = Math.Min(tabSpawnLeft[indexSide], Math.Max(50, nbEntitySpawmableLeftForSide));
                                (int nbAgentSpawned, int nbEntitySpawmableLeftRes) = ____missionSides[indexSide].PreSupplyTroopsCustom(nbTrySpawn, nbEntitySpawnableLeft);
                                nbEntitySpawnableLeft = nbEntitySpawmableLeftRes;
                                tabSpawnLeft[indexSide] -= nbAgentSpawned;
                                tabSpawned[indexSide] += nbAgentSpawned;
                            }
                        }
                        DefenderActivePhase.InitialSpawnNumber = tabSpawned[0];
                        DefenderActivePhase.RemainingSpawnNumber = DefenderActivePhase.TotalSpawnNumber - DefenderActivePhase.InitialSpawnNumber;
                        AttackerActivePhase.InitialSpawnNumber = tabSpawned[1];
                        AttackerActivePhase.RemainingSpawnNumber = AttackerActivePhase.TotalSpawnNumber - AttackerActivePhase.InitialSpawnNumber;
                    }
                    // presuplly end

                    for (int index = 0; index < 2; ++index)
                    {
                        BattleSideEnum battleSideEnum = (BattleSideEnum)index;
                        if (!__instance.Mission.IsDeploymentPlanMadeForBattleSide(battleSideEnum))
                        {
                            SpawnPhase activePhaseForSide = ____phases[(int)battleSideEnum][0];
                            if (activePhaseForSide.InitialSpawnNumber > 0)
                            {
                                //(int nbAgentSpawned, int nbEntitySpawmableLeft2) = ____missionSides[index].PreSupplyTroopsCustom(activePhaseForSide.InitialSpawnNumber, nbEntitySpawmableLeft);
                                //nbEntitySpawmableLeft = nbEntitySpawmableLeft2;
                                //activePhaseForSide.InitialSpawnNumber = nbAgentSpawned;
                                //activePhaseForSide.RemainingSpawnNumber = activePhaseForSide.TotalSpawnNumber - activePhaseForSide.InitialSpawnNumber;

                                ____missionSides[index].GetFormationSpawnData(____formationSpawnData);
                                for (int fClass = 0; fClass < ____formationSpawnData.Length; ++fClass)
                                {
                                    if (____formationSpawnData[fClass].NumTroops > 0)
                                        __instance.Mission.AddTroopsToDeploymentPlan(battleSideEnum, (FormationClass)fClass, ____formationSpawnData[fClass].FootTroopCount, ____formationSpawnData[fClass].MountedTroopCount);
                                }
                            }
                            __instance.Mission.MakeDeploymentPlanForSide(battleSideEnum, sizeForActivePhase);
                        }
                    }
                }
                List<int> intList = new List<int>();
                for (int index = 0; index < 2; ++index)
                {
                    BattleSideEnum battleSideEnum = (BattleSideEnum)index;
                    SpawnPhase activePhaseForSide = ____phases[(int)battleSideEnum][0];
                    if (__instance.Mission.IsDeploymentPlanMadeForBattleSide(battleSideEnum) && activePhaseForSide.InitialSpawnNumber > 0 && ____missionSides[index].TroopSpawningActive)
                    {
                        ____missionSides[index].SpawnTroops(activePhaseForSide.InitialSpawnNumber, false, true);
                        ____phases[(int)battleSideEnum][0].OnInitialTroopsSpawned();
                        intList.Add(index);
                    }
                }
                if (__instance.IsInitialSpawnOver)
                {
                    foreach (int side in intList)
                    {
                        ____missionSides[side].OnInitialSpawnOver();
                        __instance.OnInitialSpawnForSideEnded((BattleSideEnum)side);
                    }
                }
            }
            __result = __instance.IsInitialSpawnOver;
            return false;
        }

    }

}
