using System;

namespace BattleSize
{
    public class Settings
    {
        // *********
        // singleton

        private static Settings _instance;

        public static Settings Instance
        {
            get
            {
                if (Settings._instance == null)
                    Settings._instance = new Settings();
                return Settings._instance;
            }
            set
            {
                Settings._instance = value ?? new Settings();
            }
        }

        // *********

        private int _realBattleSize = 1024;
        public int RealBattleSize
        {
            get => _realBattleSize;
            set
            {
                // 2 <= value <= 2048
                _realBattleSize = Math.Max(2, Math.Min(2048,value));
            }
        }


        public bool ShowInfo { get; set; } = true;

        public bool ErrorWhileLoading  { get; set; } = false;
    }
}
