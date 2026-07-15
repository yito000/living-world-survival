namespace SurvivalWorld.Shared
{
    public static class SurvivalTuning
    {
        public const float HungerInitial = 100f;
        public const float HealthInitial = 100f;
        public const float HungerDecayPerSecond = 1f / 60f;
        public const float HungerWarning = 30f;
        public const float HungerCritical = 10f;
        public const float StarvationHealthDrain = 1f;
        public const float StarvationHealthDrainIntervalSeconds = 5f;
        public const int CookedMeatHungerGain = 30;
        public const int LuxuryFoodHungerGain = 30;
        public const float DefaultInteractionRange = 3.0f;
        public const int FarmPotatoGrowthSeconds = 120;
        public const int FoodWasteCleaningReward = 5;
    }
}
