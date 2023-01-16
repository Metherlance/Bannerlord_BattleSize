﻿using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BattleSize
{
    [HarmonyPatch(typeof(MissionAgentSpawnLogic))]
    class MissionAgentSpawnLogicPatch
    {
        [HarmonyPostfix]
        [HarmonyPatch("get_MaxNumberOfTroopsForMission")]
        static void Postfix(ref int __result)
        {
            // default is 1024 troops ( 2048 agents max / 2 (for cav) )
            // just after this._battleSize = MathF.Min(this._battleSize, MissionAgentSpawnLogic.MaxNumberOfTroopsForMission);
            __result *= 2;
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

        private struct FormationSpawnData
        {
            public int FootTroopCount;
            public int MountedTroopCount;

            public int NumTroops
            {
                get
                {
                    return this.FootTroopCount + this.MountedTroopCount;
                }
            }
        }

        private class MissionSide
        {
            private readonly MissionAgentSpawnLogic _spawnLogic;
            private readonly BattleSideEnum _side;
            private readonly IMissionTroopSupplier _troopSupplier;
            private BannerBearerLogic _bannerBearerLogic;
            private readonly MBList<Formation> _spawnedFormations;
            private bool _spawnWithHorses;
            private float _reinforcementBatchPriority;
            private int _reinforcementQuotaRequirement;
            private int _reinforcementBatchSize;
            private int _reinforcementsSpawnedInLastBatch;
            private int _numSpawnedTroops;
            private readonly List<IAgentOriginBase> _reservedTroops = new List<IAgentOriginBase>();
            private List<ValueTuple<Team, List<IAgentOriginBase>>> _troopOriginsToSpawnPerTeam;
            private readonly (int currentTroopIndex, int troopCount)[] _reinforcementSpawnedUnitCountPerFormation;
            private readonly Dictionary<IAgentOriginBase, FormationClass> _reinforcementTroopFormationAssignments;

            public bool TroopSpawnActive { get; private set; }

            public bool IsPlayerSide { get; }

            public bool ReinforcementSpawnActive { get; private set; }

            public bool ReinforcementsNotifiedOnLastBatch { get; private set; }

            public int NumberOfActiveTroops => this._numSpawnedTroops - this._troopSupplier.NumRemovedTroops;

            public int ReinforcementQuotaRequirement => this._reinforcementQuotaRequirement;

            public int ReinforcementsSpawnedInLastBatch => this._reinforcementsSpawnedInLastBatch;


            public int SpawnTroops(int number, bool isReinforcement)
            {
                int nbAgentCanBeAllocate = Settings.ENTITY_ENGINE_MAX - Mission.Current.AllAgents.Count;
                if (number <= 0 || nbAgentCanBeAllocate<2)
                {
                    return 0;
                }

                int nbSpawnFromPreSupplied = MathF.Min(_reservedTroops.Count, number);
                List<IAgentOriginBase> listAgentBase = new List<IAgentOriginBase>(number);
                if (nbSpawnFromPreSupplied > 0)
                {
                    int indexReserved = 0;
                    for (; indexReserved < nbSpawnFromPreSupplied && nbAgentCanBeAllocate>1; indexReserved++)
                    {
                        IAgentOriginBase reservedTroop = _reservedTroops[indexReserved];
                        nbAgentCanBeAllocate-=1;
                        if (reservedTroop.Troop.HasMount() && (_spawnWithHorses || reservedTroop.Troop.IsHero))
                        {
                            nbAgentCanBeAllocate -= 1;
                        }
                        listAgentBase.Add(reservedTroop);
                    }

                    _reservedTroops.RemoveRange(0, indexReserved);
                }

                int numberToAllocate = number - nbSpawnFromPreSupplied;
                if (numberToAllocate>0)
                {
                    listAgentBase.AddRange(SupplyTroopsCustom(numberToAllocate, nbAgentCanBeAllocate).Item1);
                }
                //listAgentBase.AddRange(_troopSupplier.SupplyTroops(numberToAllocate));

                int nbSpawned = 0;
                List<IAgentOriginBase> listAgentInFormationToSpawn = new List<IAgentOriginBase>();
                int nbTroopMount = 0;
                int nbTroopNoMount = 0;
                
                // todo for isReinforcement
                //_reinforcementTroopFormationAssignments.TryGetValue(key, out num4);
                List<ValueTuple<IAgentOriginBase, int>> troopFormationAssignments = MissionGameModels.Current.BattleSpawnModel.GetInitialSpawnAssignments(_side, listAgentBase);
               
                for (int indexFormationClass = 0; indexFormationClass < 8; indexFormationClass++)
                {
                    listAgentInFormationToSpawn.Clear();
                    IAgentOriginBase agentOriginBase = null;
                    FormationClass formationClass = (FormationClass)indexFormationClass;
                    foreach (var troopFormationAssignment in troopFormationAssignments)
                    {
                        IAgentOriginBase troopAssigned = troopFormationAssignment.Item1;
                        FormationClass formationAssigned = (FormationClass) troopFormationAssignment.Item2;
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
                    if (nbAgentInFormationToSpawn > 0)
                    {
                        bool isMounted = _spawnWithHorses && MissionDeploymentPlan.HasSignificantMountedTroops(nbTroopNoMount, nbTroopMount);
                        Formation formation = Mission.GetAgentTeam(listAgentInFormationToSpawn[0], IsPlayerSide).GetFormation(formationClass);
                        int indexTroopInFormation = 0;
                        int num2 = nbAgentInFormationToSpawn;
                        if (this.ReinforcementSpawnActive)
                        {
                            indexTroopInFormation = this._reinforcementSpawnedUnitCountPerFormation[(int)formationClass].currentTroopIndex;
                            num2 = this._reinforcementSpawnedUnitCountPerFormation[(int)formationClass].troopCount;
                        }
                        if (formation != null && !formation.HasBeenPositioned)
                        {
                            formation.BeginSpawn(num2, isMounted);
                            Mission.Current.SpawnFormation(formation);
                            _spawnedFormations.Add(formation);
                        }
                        foreach (IAgentOriginBase agentBaseToSpawn in listAgentInFormationToSpawn)
                        {
                            if (this._bannerBearerLogic != null && Mission.Current.Mode != MissionMode.Deployment && this._bannerBearerLogic.GetMissingBannerCount(formation) > 0)
                            {
                                this._bannerBearerLogic.SpawnBannerBearer(agentBaseToSpawn, this.IsPlayerSide, formation,
                                    this._spawnWithHorses, isReinforcement, num2, indexTroopInFormation, true, true, false, new Vec3?(), new Vec2?(), (string)null);
                            }
                            else
                            {
                                Mission.Current.SpawnTroop(agentBaseToSpawn, this.IsPlayerSide, true,
                                    this._spawnWithHorses, isReinforcement, num2, indexTroopInFormation, true, true, false, new Vec3?(), new Vec2?(), (string)null);
                            }

                            ++this._numSpawnedTroops;
                            ++indexTroopInFormation;
                        }
                        nbSpawned += listAgentInFormationToSpawn.Count;
                    }
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
                    foreach (Formation formation2 in team2.FormationsIncludingEmpty)
                    {
                        if (formation2.CountOfUnits > 0 && formation2.IsSpawning)
                        {
                            formation2.EndSpawn();
                        }
                    }
                }

                return nbSpawned;

            }

            public void GetFormationSpawnData(FormationSpawnData[] formationSpawnData)
            {
                if (formationSpawnData == null || formationSpawnData.Length != 11)
                    return;
                for (int index = 0; index < formationSpawnData.Length; ++index)
                {
                    formationSpawnData[index].FootTroopCount = 0;
                    formationSpawnData[index].MountedTroopCount = 0;
                }
                foreach (IAgentOriginBase reservedTroop in this._reservedTroops)
                {
                    FormationClass formationClass = reservedTroop.Troop.GetFormationClass();
                    if (reservedTroop.Troop.HasMount())
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

            public (IEnumerable<IAgentOriginBase>, int) SupplyTroopsCustom(int nbAgentToSpawn, int nbEntitySpawnableLeft)
            {
                List<IAgentOriginBase> retListAgentSupply = new List<IAgentOriginBase>(nbAgentToSpawn);
                if (nbAgentToSpawn <= 0)
                    return (retListAgentSupply, nbEntitySpawnableLeft);

                int nbEntitySpawnable = nbEntitySpawnableLeft;
                while (nbEntitySpawnable > 1 && nbAgentToSpawn > 0)
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
                    else
                    {
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

            public (int, int) ReserveTroops(int nbAgentToSpawn, int nbEntitySpawnableLeft)
            {
                if (nbAgentToSpawn <= 0)
                    return (0, nbEntitySpawnableLeft);
                (IEnumerable<IAgentOriginBase> listAgentSupply, int nbEntitySpawnable) = SupplyTroopsCustom(nbAgentToSpawn, nbEntitySpawnableLeft);
                _reservedTroops.AddRange(listAgentSupply);
                return (listAgentSupply.Count(), nbEntitySpawnable);
            }

            internal bool hasPreSupply()
            {
                return _reservedTroops.Count() > 0;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch("CheckReinforcementSpawn")]
        static bool CheckReinforcementSpawn(MissionAgentSpawnLogic __instance, ref int ____battleSize, ref List<SpawnPhase>[] ____phases, ref MissionSide[] ____missionSides)
        {
            // Don't use NumActiveTroops of IMissionTroopSupplier, it takes allocated troop not used !!! ...

            SpawnPhase DefenderActivePhase = ____phases[0][0];
            SpawnPhase AttackerActivePhase = ____phases[1][0];
            int TotalSpawnNumber = DefenderActivePhase.RemainingSpawnNumber + AttackerActivePhase.RemainingSpawnNumber;

            //int numberOfTroopsCanBeSpawned = __instance.NumberOfTroopsCanBeSpawned;
            if (TotalSpawnNumber <= 0)// || numberOfTroopsCanBeSpawned <= 0
            {
                return false;
            }

            int nbAgentSpawnable = ____battleSize - __instance.Mission.AllAgents.Count;
            int nbAgentSpawnMin = (int)(((float)____battleSize) * 0.1);

            // || last troop need to spawn
            if (nbAgentSpawnable > nbAgentSpawnMin)
            {
                int nbSpawnableDef;
                int nbSpawnableAtt;
                if (TotalSpawnNumber > nbAgentSpawnable)
                {
                    // ratio with spawn left and active units
                    float ratioDefByAtt = (float)(DefenderActivePhase.RemainingSpawnNumber + DefenderActivePhase.NumberActiveTroops) /
                        (float)(AttackerActivePhase.RemainingSpawnNumber + AttackerActivePhase.NumberActiveTroops);
                    // 1 vs max
                    int nbTroopMin = (int)((float)____battleSize / (1f + Settings.Instance.OneVsMax));

                    if (ratioDefByAtt < (1 / Settings.Instance.OneVsMax))
                    {
                        nbSpawnableDef = MathF.Max(MathF.Min(MathF.Min(DefenderActivePhase.RemainingSpawnNumber, nbTroopMin - DefenderActivePhase.NumberActiveTroops), nbAgentSpawnable),0);
                        nbSpawnableAtt = nbAgentSpawnable - nbSpawnableDef;
                    }
                    else if (ratioDefByAtt > Settings.Instance.OneVsMax)
                    {
                        // begin with side whose has less troops
                        nbSpawnableAtt = MathF.Max(MathF.Min(MathF.Min(AttackerActivePhase.RemainingSpawnNumber, nbTroopMin - AttackerActivePhase.NumberActiveTroops), nbAgentSpawnable),0);
                        nbSpawnableDef = nbAgentSpawnable - nbSpawnableAtt;
                    }
                    else
                    {
                        // nb unit max without horse and dead corpse not cleaned
                        int realBattleSizeWithoutHorse = nbAgentSpawnable + DefenderActivePhase.NumberActiveTroops + AttackerActivePhase.NumberActiveTroops;
                        // troup min def - def active
                        nbSpawnableDef = MathF.Max(MathF.Min(((int)(realBattleSizeWithoutHorse * ratioDefByAtt) - DefenderActivePhase.NumberActiveTroops), nbAgentSpawnable),0);
                        nbSpawnableAtt = nbAgentSpawnable - nbSpawnableDef;
                    }

                }
                else
                {
                    nbSpawnableDef = DefenderActivePhase.RemainingSpawnNumber;
                    nbSpawnableAtt = AttackerActivePhase.RemainingSpawnNumber;
                }

                //horse 2 times for att in case of engine limit
                if (nbSpawnableAtt > 1)
                {
                    int spawned = (int)____missionSides[1].SpawnTroops(nbSpawnableAtt / 2, true);
                    AttackerActivePhase.RemainingSpawnNumber -= spawned;
                    nbSpawnableAtt -= spawned;
                }
                if (nbSpawnableDef > 0)
                {
                    DefenderActivePhase.RemainingSpawnNumber -= (int)____missionSides[0].SpawnTroops(nbSpawnableDef, true);

                }
                if (nbSpawnableAtt > 0)
                {
                    AttackerActivePhase.RemainingSpawnNumber -= (int)____missionSides[1].SpawnTroops(nbSpawnableAtt, true);
                }

            }
            return false;
        }



        [HarmonyPrefix]
        [HarmonyPatch("CheckInitialSpawns")]
        static bool CheckInitialSpawns(ref bool __result, MissionAgentSpawnLogic __instance, ref int ____battleSize, ref List<SpawnPhase>[] ____phases, ref FormationSpawnData[] ____formationSpawnData,
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
                    if (!____missionSides[0].hasPreSupply() && !____missionSides[1].hasPreSupply() && DefenderActivePhase.InitialSpawnNumber>0 && AttackerActivePhase.InitialSpawnNumber > 0)
                    {
                        int nbEntitySpawnableLeft = Settings.ENTITY_ENGINE_MAX - __instance.Mission.AllAgents.Count();
                        int nbAgentSpawnableLeft = ____battleSize - __instance.Mission.Agents.Count();

                        //ratio
                        int TotalSpawnNumber = DefenderActivePhase.TotalSpawnNumber + AttackerActivePhase.TotalSpawnNumber;
                        int[] tabSpawnLeft = new int[2];
                        if (TotalSpawnNumber > nbAgentSpawnableLeft)
                        {
                            float ratioDefByAtt = (float)DefenderActivePhase.TotalSpawnNumber / (float)AttackerActivePhase.TotalSpawnNumber;
                            // 1 vs max
                            int nbTroopMin = (int)((float)nbAgentSpawnableLeft / (1f + Settings.Instance.OneVsMax));
                            if (ratioDefByAtt < (1 / Settings.Instance.OneVsMax))
                            {
                                tabSpawnLeft[0] = (int)MathF.Min(DefenderActivePhase.TotalSpawnNumber, nbTroopMin);
                                tabSpawnLeft[1] = nbAgentSpawnableLeft - tabSpawnLeft[0];
                            }
                            else if (ratioDefByAtt > Settings.Instance.OneVsMax)
                            {
                                // begin with side whose has less troops
                                tabSpawnLeft[1] = (int)MathF.Min(AttackerActivePhase.TotalSpawnNumber, nbTroopMin);
                                tabSpawnLeft[0] = nbAgentSpawnableLeft - tabSpawnLeft[1];
                            }
                            else
                            {
                                tabSpawnLeft[0] = (int)(nbAgentSpawnableLeft * ratioDefByAtt);
                                tabSpawnLeft[1] = nbAgentSpawnableLeft - tabSpawnLeft[0];
                            }

                        }
                        else
                        {
                            tabSpawnLeft[0] = DefenderActivePhase.TotalSpawnNumber;
                            tabSpawnLeft[1] = AttackerActivePhase.TotalSpawnNumber;
                        }

                        int[] tabSpawned = new int[2];
                        while (nbEntitySpawnableLeft > 1 && (tabSpawnLeft[0] > 0 || tabSpawnLeft[1] > 0))
                        {
                            //try to have same size by one loop
                            int nbEntitySpawmableLeftForSide = nbEntitySpawnableLeft / 4;
                            if (tabSpawnLeft[0] == 0 || tabSpawnLeft[1] == 0)
                            {
                                nbEntitySpawmableLeftForSide = nbEntitySpawnableLeft;
                            }
                            //for each teams
                            for (int indexSide = 0; indexSide < 2; indexSide += 1)
                            {
                                int nbTrySpawn = Math.Min(tabSpawnLeft[indexSide], Math.Max(50, nbEntitySpawmableLeftForSide));
                                (int nbAgentSpawned, int nbEntitySpawmableLeftRes) = ____missionSides[indexSide].ReserveTroops(nbTrySpawn, nbEntitySpawnableLeft);
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

                    for (int indexBattleSide = 0; indexBattleSide < 2; ++indexBattleSide)
                    {
                        BattleSideEnum battleSideEnum = (BattleSideEnum)indexBattleSide;
                        SpawnPhase activePhaseForSide = ____phases[(int)battleSideEnum][0];
                        if (!__instance.Mission.DeploymentPlan.IsPlanMadeForBattleSide(battleSideEnum, DeploymentPlanType.Initial))
                        {
                            if (activePhaseForSide.InitialSpawnNumber > 0)
                            {
                                // already done earlier
                                //  this._missionSides[index].ReserveTroops(activePhaseForSide.InitialSpawnNumber);
                                ____missionSides[indexBattleSide].GetFormationSpawnData(____formationSpawnData);
                                for (int fClass = 0; fClass < ____formationSpawnData.Length; ++fClass)
                                {
                                    if (____formationSpawnData[fClass].NumTroops > 0)
                                    {
                                        __instance.Mission.AddTroopsToDeploymentPlan(battleSideEnum, DeploymentPlanType.Initial, (FormationClass)fClass, ____formationSpawnData[fClass].FootTroopCount, ____formationSpawnData[fClass].MountedTroopCount);
                                    }
                                }
                            }
                            float spawnPathOffset = 0.0f;
                            if (__instance.Mission.HasSpawnPath)
                            {
                                spawnPathOffset = Mission.GetBattleSizeOffset(sizeForActivePhase, __instance.Mission.GetInitialSpawnPath());
                            }
                            __instance.Mission.MakeDeploymentPlanForSide(battleSideEnum, DeploymentPlanType.Initial, spawnPathOffset);
                        }
                        else if (!__instance.Mission.DeploymentPlan.IsPlanMadeForBattleSide(battleSideEnum, DeploymentPlanType.Reinforcement))
                        {
                            if (activePhaseForSide.InitialSpawnNumber > 0)
                            {
                                ____missionSides[indexBattleSide].GetFormationSpawnData(____formationSpawnData);
                                for (int fClass = 0; fClass < ____formationSpawnData.Length; ++fClass)
                                {
                                    if (____formationSpawnData[fClass].NumTroops > 0)
                                    {
                                        __instance.Mission.AddTroopsToDeploymentPlan(battleSideEnum, DeploymentPlanType.Reinforcement, (FormationClass)fClass, ____formationSpawnData[fClass].FootTroopCount, ____formationSpawnData[fClass].MountedTroopCount);
                                    }
                                }
                            }
                            __instance.Mission.MakeDeploymentPlanForSide(battleSideEnum, DeploymentPlanType.Reinforcement);
                        }
                    }
                }
                List<int> intList = new List<int>();
                for (int index = 0; index < 2; ++index)
                {
                    BattleSideEnum battleSideEnum = (BattleSideEnum)index;
                    SpawnPhase activePhaseForSide = ____phases[(int)battleSideEnum][0];
                    bool __troopSpawnActive = ____missionSides[index].TroopSpawnActive;
                    if (__instance.Mission.DeploymentPlan.IsPlanMadeForBattleSide(battleSideEnum,DeploymentPlanType.Initial)
                         && __troopSpawnActive && activePhaseForSide.InitialSpawnNumber > 0)
                    {
                        // this will empty reserved troops...
                        ____missionSides[index].SpawnTroops(activePhaseForSide.InitialSpawnNumber, false);

                        // Don't know how send event ...
                        //EventInfo eventInfo = typeof(MissionAgentSpawnLogic).GetEvent("OnNotifyInitialTroopsSpawned");
                        //MissionAgentSpawnLogic.SpawnNotificationData notif = new MissionAgentSpawnLogic.SpawnNotificationData((BattleSideEnum)index, activePhaseForSide.InitialSpawnNumber);

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
