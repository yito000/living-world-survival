using SurvivalWorld.Items;

namespace SurvivalWorld.Inventory
{
    public sealed class AIInventoryAdapter
    {
        private readonly InventoryService inventoryService;
        private readonly InventoryOwner owner;

        public AIInventoryAdapter(InventoryService inventoryService, InventoryOwner owner)
        {
            this.inventoryService = inventoryService;
            this.owner = owner;
        }

        public int InventoryPressure { get; private set; }

        public InventoryMutationResult AddLoot(string itemDefinitionId, int quantity)
        {
            InventoryMutationResult result = inventoryService.AddItem(owner, itemDefinitionId, string.Empty, quantity);
            if (!result.Success)
            {
                InventoryPressure++;
            }

            return result;
        }
    }
}
