namespace SurvivalWorld.Server.Simulation
{
    public readonly struct M3CommandResult
    {
        private M3CommandResult(bool success, string error)
        {
            Success = success;
            Error = error ?? string.Empty;
        }

        public bool Success { get; }
        public string Error { get; }

        public static M3CommandResult Ok()
        {
            return new M3CommandResult(true, string.Empty);
        }

        public static M3CommandResult Rejected(string error)
        {
            return new M3CommandResult(false, error);
        }
    }
}
