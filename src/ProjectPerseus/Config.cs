namespace ProjectPerseus
{
    public static class Config
    {
        public static string ApiToken
        {
            get => Properties.Settings.Default.ApiToken;
            set
            {
                Properties.Settings.Default.ApiToken = value;
                Properties.Settings.Default.Save();
            }
        }

        public static string BaseUrl { 
            get => Properties.Settings.Default.BaseUrl;
            set
            {
                Properties.Settings.Default.BaseUrl = value;
                Properties.Settings.Default.Save();
            }
        }
    }
}