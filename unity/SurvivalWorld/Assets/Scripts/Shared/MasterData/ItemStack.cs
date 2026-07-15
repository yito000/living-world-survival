namespace SurvivalWorld.Shared.MasterData
{
    public readonly struct ItemStack
    {
        public ItemStack(string itemDefinitionId, int quantity, string itemInstanceId = "")
        {
            ItemDefinitionId = itemDefinitionId ?? string.Empty;
            Quantity = quantity;
            ItemInstanceId = itemInstanceId ?? string.Empty;
        }

        public string ItemDefinitionId { get; }
        public int Quantity { get; }
        public string ItemInstanceId { get; }
    }
}
