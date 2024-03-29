﻿using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using static TaleWorlds.MountAndBlade.MovementOrder;

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
                if (number <= 0 || nbAgentCanBeAllocate < 2)
                {
                    return 0;
                }

                int nbSpawnFromPreSupplied = MathF.Min(_reservedTroops.Count, number);
                List<IAgentOriginBase> listAgentBase = new List<IAgentOriginBase>(number);
                if (nbSpawnFromPreSupplied > 0)
                {
                    int indexReserved = 0;
                    for (; indexReserved < nbSpawnFromPreSupplied && nbAgentCanBeAllocate > 1; indexReserved++)
                    {
                        IAgentOriginBase reservedTroop = _reservedTroops[indexReserved];
                        nbAgentCanBeAllocate -= 1;
                        if (reservedTroop.Troop.HasMount() && (_spawnWithHorses || reservedTroop.Troop.IsHero))
                        {
                            nbAgentCanBeAllocate -= 1;
                        }
                        listAgentBase.Add(reservedTroop);
                    }

                    _reservedTroops.RemoveRange(0, indexReserved);
                }

                int numberToAllocate = number - nbSpawnFromPreSupplied;
                if (numberToAllocate > 0)
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
                        FormationClass formationAssigned = (FormationClass)troopFormationAssignment.Item2;
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
                            Mission.Current.SetFormationPositioningFromDeploymentPlan(formation);
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

            public bool HasSpawnableReinforcements
            {
                get
                {
                    return this.ReinforcementSpawnActive && this.HasReservedTroops && (double)this.ReinforcementBatchSize > 0.0;
                }
            }

            public bool HasReservedTroops
            {
                get
                {
                    return this._reservedTroops.Count > 0;
                }
            }
            public float ReinforcementBatchSize
            {
                get
                {
                    return (float)this._reinforcementBatchSize;
                }
            }
            public float ReinforcementBatchPriority
            {
                get
                {
                    return this._reinforcementBatchPriority;
                }
            }

            public int TryReinforcementSpawn()
            {
                int num1 = 0;
                if (this.ReinforcementSpawnActive && this.TroopSpawnActive && this._reservedTroops.Count > 0)
                {
                    int num2 = MissionAgentSpawnLogic.MaxNumberOfAgentsForMission - this._spawnLogic.NumberOfAgents;
                    int reservedTroopQuota = this.GetReservedTroopQuota(0);
                    int num3 = reservedTroopQuota;
                    if (num2 >= num3)
                    {
                        num1 = this.SpawnTroops(1, true);
                        if (num1 > 0)
                        {
                            this._reinforcementQuotaRequirement -= reservedTroopQuota;
                            if (this._reservedTroops.Count >= this._reinforcementBatchSize)
                                this._reinforcementQuotaRequirement += this.GetReservedTroopQuota(this._reinforcementBatchSize - 1);
                            this._reinforcementBatchPriority /= 2f;
                        }
                    }
                }
                this._reinforcementsSpawnedInLastBatch += num1;
                return num1;
            }

            private int GetReservedTroopQuota(int index)
            {
                return !this._spawnWithHorses || !this._reservedTroops[index].Troop.IsMounted ? 1 : 2;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch("CheckGlobalReinforcementBatch")]
        static void CheckGlobalReinforcementBatch(ref bool ____spawningReinforcements, ref List<SpawnPhase>[] ____phases)
        {
            SpawnPhase DefenderActivePhase = ____phases[0][0];
            SpawnPhase AttackerActivePhase = ____phases[1][0];
            ____spawningReinforcements |= DefenderActivePhase.RemainingSpawnNumber>0 || AttackerActivePhase.RemainingSpawnNumber>0;
        }

        [HarmonyPostfix]
        [HarmonyPatch("CheckCustomReinforcementBatch")]
        static void CheckCustomReinforcementBatch(ref bool ____spawningReinforcements, ref List<SpawnPhase>[] ____phases)
        {
            SpawnPhase DefenderActivePhase = ____phases[0][0];
            SpawnPhase AttackerActivePhase = ____phases[1][0];
            ____spawningReinforcements |= DefenderActivePhase.RemainingSpawnNumber > 0 || AttackerActivePhase.RemainingSpawnNumber > 0;
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
                        nbSpawnableDef = MathF.Max(MathF.Min(MathF.Min(DefenderActivePhase.RemainingSpawnNumber, nbTroopMin - DefenderActivePhase.NumberActiveTroops), nbAgentSpawnable), 0);
                        nbSpawnableAtt = nbAgentSpawnable - nbSpawnableDef;
                    }
                    else if (ratioDefByAtt > Settings.Instance.OneVsMax)
                    {
                        // begin with side whose has less troops
                        nbSpawnableAtt = MathF.Max(MathF.Min(MathF.Min(AttackerActivePhase.RemainingSpawnNumber, nbTroopMin - AttackerActivePhase.NumberActiveTroops), nbAgentSpawnable), 0);
                        nbSpawnableDef = nbAgentSpawnable - nbSpawnableAtt;
                    }
                    else
                    {
                        // nb unit max without horse and dead corpse not cleaned
                        int realBattleSizeWithoutHorse = nbAgentSpawnable + DefenderActivePhase.NumberActiveTroops + AttackerActivePhase.NumberActiveTroops;
                        int nbDefTroopsTotal = DefenderActivePhase.RemainingSpawnNumber + DefenderActivePhase.NumberActiveTroops;
                        int nbTroopsTotal = nbDefTroopsTotal + AttackerActivePhase.RemainingSpawnNumber + AttackerActivePhase.NumberActiveTroops;
                        // troup min def - def active
                        nbSpawnableDef = MathF.Max(MathF.Min(((realBattleSizeWithoutHorse * nbDefTroopsTotal / nbTroopsTotal) - DefenderActivePhase.NumberActiveTroops), nbAgentSpawnable), 0);
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
        [HarmonyPatch("CheckDeployment")]
        static bool CheckDeployment(ref bool __result, MissionAgentSpawnLogic __instance, ref int ____battleSize, ref List<SpawnPhase>[] ____phases, ref FormationSpawnData[] ____formationSpawnData,
        ref MissionSide[] ____missionSides, ref Action<BattleSideEnum, int> ___OnInitialTroopsSpawned, ref List<BattleSideEnum> ____sidesWhereSpawnOccured)
        {
            bool isDeploymentOver = __instance.IsDeploymentOver;
            if (!isDeploymentOver)
            {
                SpawnPhase DefenderActivePhase = ____phases[0][0];
                SpawnPhase AttackerActivePhase = ____phases[1][0];
                int GetBattleSizeForActivePhase = MathF.Max(DefenderActivePhase.TotalSpawnNumber, AttackerActivePhase.TotalSpawnNumber);

                // presuplly
                if (!____missionSides[0].hasPreSupply() && !____missionSides[1].hasPreSupply() && DefenderActivePhase.InitialSpawnNumber > 0 && AttackerActivePhase.InitialSpawnNumber > 0)
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
                            tabSpawnLeft[0] = nbAgentSpawnableLeft * DefenderActivePhase.TotalSpawnNumber / TotalSpawnNumber;
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


                for (int indexSide = 0; indexSide < 2; ++indexSide)
                {
                    BattleSideEnum battleSideEnum = (BattleSideEnum)indexSide;
                    SpawnPhase activePhaseForSide = ____phases[indexSide][0];
                    if (!__instance.Mission.DeploymentPlan.IsPlanMadeForBattleSide(battleSideEnum, DeploymentPlanType.Initial))
                    {
                        if (activePhaseForSide.InitialSpawnNumber > 0)
                        {
                            // already done earlier
                            //____missionSides[indexSide].ReserveTroops(activePhaseForSide.InitialSpawnNumber,2012);
                            ____missionSides[indexSide].GetFormationSpawnData(____formationSpawnData);
                            for (int fClass = 0; fClass < ____formationSpawnData.Length; ++fClass)
                            {
                                if (____formationSpawnData[fClass].NumTroops > 0)
                                    __instance.Mission.AddTroopsToDeploymentPlan(battleSideEnum, DeploymentPlanType.Initial, (FormationClass)fClass, ____formationSpawnData[fClass].FootTroopCount, ____formationSpawnData[fClass].MountedTroopCount);
                            }
                        }
                        float spawnPathOffset = 0.0f;
                        if (__instance.Mission.HasSpawnPath)
                            spawnPathOffset = Mission.GetBattleSizeOffset(GetBattleSizeForActivePhase, __instance.Mission.GetInitialSpawnPath());
                        __instance.Mission.MakeDeploymentPlanForSide(battleSideEnum, DeploymentPlanType.Initial, spawnPathOffset);
                    }
                    if (!__instance.Mission.DeploymentPlan.IsPlanMadeForBattleSide(battleSideEnum, DeploymentPlanType.Reinforcement))
                    {
                        int num = Math.Max(____battleSize / (2 * ____formationSpawnData.Length), 1);
                        for (int fClass = 0; fClass < ____formationSpawnData.Length; ++fClass)
                        {
                            if (((FormationClass)fClass).IsMounted())
                                __instance.Mission.AddTroopsToDeploymentPlan(battleSideEnum, DeploymentPlanType.Reinforcement, (FormationClass)fClass, 0, num);
                            else
                                __instance.Mission.AddTroopsToDeploymentPlan(battleSideEnum, DeploymentPlanType.Reinforcement, (FormationClass)fClass, num, 0);
                        }
                        __instance.Mission.MakeDeploymentPlanForSide(battleSideEnum, DeploymentPlanType.Reinforcement, 0.0f);
                    }
                }
                for (int indexSide = 0; indexSide < 2; ++indexSide)
                {
                    BattleSideEnum side = (BattleSideEnum)indexSide;
                    SpawnPhase activePhaseForSide = ____phases[indexSide][0];
                    if (__instance.Mission.DeploymentPlan.IsPlanMadeForBattleSide(side, DeploymentPlanType.Initial) && activePhaseForSide.InitialSpawnNumber > 0 && ____missionSides[indexSide].TroopSpawnActive)
                    {
                        int initialSpawnNumber = activePhaseForSide.InitialSpawnNumber;
                        ____missionSides[indexSide].SpawnTroops(initialSpawnNumber, false);
                        ____phases[indexSide][0].OnInitialTroopsSpawned();
                        ____missionSides[indexSide].OnInitialSpawnOver();
                        if (!____sidesWhereSpawnOccured.Contains(side))
                            ____sidesWhereSpawnOccured.Add(side);

                        if (___OnInitialTroopsSpawned != null)
                        {
                            ___OnInitialTroopsSpawned(side, initialSpawnNumber);
                        }
                    }
                }

                isDeploymentOver = __instance.IsDeploymentOver;
                if (isDeploymentOver)
                {
                    foreach (BattleSideEnum side in ____sidesWhereSpawnOccured)
                    {
                        __instance.OnBattleSideDeployed(side);
                    }
                }
            }
            __result = isDeploymentOver;
            return false;
        }

    }

}
