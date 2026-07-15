namespace SurvivalWorld.Shared.Events
{
    public static class DomainEventTypes
    {
        public const string ResourceMined = "resource.mined";
        public const string ResourceNodeDepleted = "resource.node_depleted";
        public const string ResourceNodeRegenerated = "resource.node_regenerated";
        public const string StationJobStarted = "station.job_started";
        public const string StationJobCompleted = "station.job_completed";
        public const string StationJobCancelled = "station.job_cancelled";
        public const string DevelopmentBlueprintUnlocked = "development.blueprint_unlocked";
        public const string FarmCropPlanted = "farm.crop_planted";
        public const string FarmCropHarvested = "farm.crop_harvested";
        public const string HuntingAnimalKilled = "hunting.animal_killed";
        public const string HuntingCarcassButchered = "hunting.carcass_butchered";
        public const string CookingCompleted = "cooking.completed";
        public const string InventoryItemConsumed = "inventory.item_consumed";
        public const string ItemDiscarded = "item.discarded";
        public const string CleaningCompleted = "cleaning.completed";
        public const string CharacterVitalsChanged = "character.vitals_changed";
    }
}
