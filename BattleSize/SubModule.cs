using TaleWorlds.MountAndBlade;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Library;
using System;
using System.IO;

namespace BattleSize
{

    public class SubModule : MBSubModuleBase
    {
        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();

            try
            {
                // load xml
                Settings.Instance = SettingsLoader.LoadSettings(Path.Combine(BasePath.Name, "Modules", "BattleSize", "ModuleData", "config.xml"));

                // patch methods
                var harmony = new Harmony("bannerlord.battlesize");
                harmony.PatchAll();

            }
            catch (Exception e)
            {
                Settings.Instance.ErrorWhileLoading  = true;
            }
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();

            // show info
            if (Settings.Instance.ShowInfo)
            { 
                if (Settings.Instance.ErrorWhileLoading)
                {
                    InformationManager.DisplayMessage(new InformationMessage("BattleSize | error while loading"));
                }
                else
                {
                    InformationManager.DisplayMessage(new InformationMessage("BattleSize | set " + Settings.Instance.RealBattleSize + " to RealBattleSize"));
                }
            }
            // show only 1 time
            Settings.Instance.ShowInfo = false;
        }
    }
}

