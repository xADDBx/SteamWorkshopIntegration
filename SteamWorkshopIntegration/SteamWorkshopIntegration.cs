using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Kingmaker.Modding {
    public class SteamWorkshopIntegration {
        public static bool Started { get; private set; }
        // This file contains information about the latest installed version
        public const string ModManagingInfoFile = "WorkshopManaged.json";
        private static SteamWorkshopIntegration s_Instance;
        public static SteamWorkshopIntegration Instance {
            get {
                if (s_Instance == null) {
                    s_Instance = new();
                }
                return s_Instance;
            }
        }
        public void Start() {
            if (Started) {
                return;
            }
            Started = true;
        }
    }
}
