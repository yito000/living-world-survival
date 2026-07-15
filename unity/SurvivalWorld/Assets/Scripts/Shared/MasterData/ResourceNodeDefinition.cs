namespace SurvivalWorld.Shared.MasterData
{
    public sealed class ResourceNodeDefinition
    {
        public ResourceNodeDefinition(string resourceType, string grantedItemDefinitionId, int baseMineAmount, int hardness, string requiredToolTag)
        {
            ResourceType = resourceType ?? string.Empty;
            GrantedItemDefinitionId = grantedItemDefinitionId ?? string.Empty;
            BaseMineAmount = baseMineAmount <= 0 ? 1 : baseMineAmount;
            Hardness = hardness <= 0 ? 1 : hardness;
            RequiredToolTag = requiredToolTag ?? string.Empty;
        }

        public string ResourceType { get; }
        public string GrantedItemDefinitionId { get; }
        public int BaseMineAmount { get; }
        public int Hardness { get; }
        public string RequiredToolTag { get; }
    }
}
