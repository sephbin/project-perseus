using System;

namespace ProjectPerseus
{
    public class Config
    {
        private static Config instance;
        public static Config Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = new Config();
                }
                return instance;
            }
        }
        
        public string ApiToken
        {
            get => Properties.Settings.Default.ApiToken;
            set
            {
                Properties.Settings.Default.ApiToken = value;
                Properties.Settings.Default.Save();
            }
        }

        public string BaseUrl { 
            get => Properties.Settings.Default.BaseUrl;
            set
            {
                Properties.Settings.Default.BaseUrl = value;
                Properties.Settings.Default.Save();
            }
        }

        public bool FullSyncNextSync { 
            get => Properties.Settings.Default.FullSyncNextSync;
            set
            {
                Properties.Settings.Default.FullSyncNextSync = value;
                Properties.Settings.Default.Save();
            }
        }
        
        // todo: this is a hack and should be removed.
        // todo: this stores the last sync version with the logged in windows user which means 
        // todo: that the user can't do multiple documents.
        // todo: we should store this in the Revit document instead.
        public Guid LastSyncVersionGuid
        {
            get => Properties.Settings.Default.LastSyncVersionGuid;
            set
            {
                Properties.Settings.Default.LastSyncVersionGuid = value;
                Properties.Settings.Default.Save();
            }
        }
    }
}