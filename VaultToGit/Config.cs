namespace VaultToGit
{
    partial class Config
    {
        public static Config Instance { get; private set; }

        public string URL => $"http://{Host}/VaultService/";
        public string Host { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string Repository { get; set; }
        public string EmailDomain { get; set; }
    }
}
