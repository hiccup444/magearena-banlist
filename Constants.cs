namespace PlayerBanMod
{
    internal static class Constants
    {
        // Persistence
        public const float SaveIntervalSeconds = 5f;

        // UI
        public const int MaxVisibleBannedItems = 50;

        // Storage delimiters
        public const string FieldDelimiter = "◊";
        public const string EntryDelimiter = "※";

        // Kick system
        public const int MaxKickAttempts = 3;
        public const float KickRetryDelaySeconds = 3f;

        // Player tracking
        public const float RecentlyKickedDurationSeconds = 10f;
    }
}


