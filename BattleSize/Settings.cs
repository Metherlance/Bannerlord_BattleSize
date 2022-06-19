using System;

namespace BattleSize
{
    public class Settings
    {
        // *********
        // singleton

        private static Settings _instance;
        public const int ENTITY_ENGINE_MAX = 2047;

        public static Settings Instance
        {
            get
            {
                if (Settings._instance == null) {
                    Settings._instance = new Settings();
                }
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
                // 2 <= value <= 2047
                _realBattleSize = Math.Max(2, Math.Min(2047, value));
            }
        }

        private float _oneVsMax = 2;
        public float OneVsMax
        {
            get => _oneVsMax;
            set
            {
                // 1 <= value <= 4
                _oneVsMax = Math.Max(1, Math.Min(4, value));
            }
        }

        private float _riderDieMountFleeDie = .5f;
        public float RiderDieMountFleeDie
        {
            get => _riderDieMountFleeDie;
            set
            {
                // 0 <= value <= 1
                _riderDieMountFleeDie = Math.Max(1, Math.Min(0, value));
            }
        }

        public bool ShowInfo { get; set; } = true;

        public bool ErrorWhileLoading { get; set; } = false;

    }
}
