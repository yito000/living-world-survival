using System;
using SurvivalWorld.Inventory;
using SurvivalWorld.Server.Inventory;
using SurvivalWorld.Shared;
using SurvivalWorld.Shared.Events;
using SurvivalWorld.Shared.MasterData;

namespace SurvivalWorld.Server.Simulation
{
    public sealed class FarmPlotSystem
    {
        private readonly M3InventoryService inventory;
        private readonly DomainEventFactory eventFactory;
        private readonly IInventoryEventSink eventSink;

        public FarmPlotSystem(M3InventoryService inventory, DomainEventFactory eventFactory, IInventoryEventSink eventSink)
        {
            this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            this.eventFactory = eventFactory ?? throw new ArgumentNullException(nameof(eventFactory));
            this.eventSink = eventSink ?? NullInventoryEventSink.Instance;
        }

        public M3CommandResult Plant(FarmPlotState plot, string cropId, long unixTimeMs)
        {
            if (plot == null)
            {
                return M3CommandResult.Rejected("Plot is required.");
            }

            if (plot.State != FarmPlotLifecycle.Empty)
            {
                return M3CommandResult.Rejected("Plot is not empty.");
            }

            plot.CropId = string.IsNullOrWhiteSpace(cropId) ? "potato" : cropId;
            plot.ReadyAtUnixMs = unixTimeMs + (SurvivalTuning.FarmPotatoGrowthSeconds * 1000L);
            plot.State = FarmPlotLifecycle.Growing;
            eventSink.Enqueue(eventFactory.Create(
                plot.PlotId,
                DomainEventTypes.FarmCropPlanted,
                JsonPayload.Object(
                    JsonPayload.Field("plot_id", plot.PlotId),
                    JsonPayload.Field("crop_id", plot.CropId),
                    JsonPayload.Field("ready_at_unix_ms", plot.ReadyAtUnixMs)),
                string.Empty,
                unixTimeMs));
            return M3CommandResult.Ok();
        }

        public M3CommandResult Harvest(FarmPlotState plot, InventoryOwner actorInventory, long unixTimeMs)
        {
            if (plot == null)
            {
                return M3CommandResult.Rejected("Plot is required.");
            }

            if (plot.State != FarmPlotLifecycle.Growing && plot.State != FarmPlotLifecycle.Ready)
            {
                return M3CommandResult.Rejected("Plot is not harvestable.");
            }

            if (unixTimeMs < plot.ReadyAtUnixMs)
            {
                return M3CommandResult.Rejected("Crop is not ready.");
            }

            plot.State = FarmPlotLifecycle.Ready;
            var produced = new ItemStack("potato", 2);
            if (!inventory.CanGrant(actorInventory, produced.ItemDefinitionId, produced.ItemInstanceId, produced.Quantity, out string reason))
            {
                return M3CommandResult.Rejected(reason);
            }

            inventory.Grant(actorInventory, produced);
            plot.State = FarmPlotLifecycle.Harvested;
            eventSink.Enqueue(eventFactory.Create(
                plot.PlotId,
                DomainEventTypes.FarmCropHarvested,
                JsonPayload.Object(
                    JsonPayload.Field("plot_id", plot.PlotId),
                    JsonPayload.Field("crop_id", plot.CropId),
                    JsonPayload.Raw("produced", JsonPayload.ItemStackArray(new[] { produced }, false))),
                string.Empty,
                unixTimeMs));
            return M3CommandResult.Ok();
        }
    }

    public sealed class FarmPlotState
    {
        public FarmPlotState(string plotId)
        {
            PlotId = plotId ?? string.Empty;
        }

        public string PlotId { get; }
        public string CropId { get; set; } = string.Empty;
        public FarmPlotLifecycle State { get; set; } = FarmPlotLifecycle.Empty;
        public long ReadyAtUnixMs { get; set; }
    }

    public enum FarmPlotLifecycle
    {
        Empty,
        Growing,
        Ready,
        Harvested
    }
}
