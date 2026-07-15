using System;
using System.Collections.Generic;
using Survival.V1;
using SurvivalWorld.Inventory;
using SurvivalWorld.Server.Simulation;

namespace SurvivalWorld.Server.Handlers
{
    public sealed class InteractionCommandHandler
    {
        private readonly Dictionary<uint, InteractionTarget> targets = new Dictionary<uint, InteractionTarget>();

        public void RegisterTarget(uint networkId, InteractionTarget target)
        {
            targets[networkId] = target;
        }

        public M3CommandResult Handle(InteractCommand command, InteractionActorContext actor, long unixTimeMs)
        {
            if (command == null)
            {
                return M3CommandResult.Rejected("Command is required.");
            }

            if (!targets.TryGetValue(command.TargetNetworkId, out InteractionTarget target))
            {
                return M3CommandResult.Rejected("Interaction target not registered.");
            }

            string type = command.InteractionType ?? string.Empty;
            switch (type)
            {
                case "mine":
                    if (target.ResourceNode == null || target.ResourceNodeSystem == null)
                    {
                        return M3CommandResult.Rejected("Target is not a resource node.");
                    }

                    ResourceMineResult mine = target.ResourceNodeSystem.Mine(
                        target.ResourceNode,
                        actor.Inventory,
                        new ResourceMineRequest(actor.Position, command.ExpectedVersion, actor.HasLineOfSight, actor.ToolTags, actor.ToolQuality, unixTimeMs));
                    return mine.Success ? M3CommandResult.Ok() : M3CommandResult.Rejected(mine.Error);

                case "station_cancel":
                    if (target.Station == null || target.StationJobSystem == null)
                    {
                        return M3CommandResult.Rejected("Target is not a station.");
                    }

                    StationJobResult cancel = target.StationJobSystem.CancelJob(target.Station, actor.Inventory, unixTimeMs);
                    return cancel.Success ? M3CommandResult.Ok() : M3CommandResult.Rejected(cancel.Error);

                case "farm_plant":
                    return target.FarmPlotSystem != null && target.FarmPlot != null
                        ? target.FarmPlotSystem.Plant(target.FarmPlot, "potato", unixTimeMs)
                        : M3CommandResult.Rejected("Target is not a farm plot.");

                case "farm_harvest":
                    return target.FarmPlotSystem != null && target.FarmPlot != null
                        ? target.FarmPlotSystem.Harvest(target.FarmPlot, actor.Inventory, unixTimeMs)
                        : M3CommandResult.Rejected("Target is not a farm plot.");

                case "clean":
                    return target.CleaningSystem != null && !string.IsNullOrWhiteSpace(target.WorldItemId)
                        ? target.CleaningSystem.Clean(target.WorldItemId, unixTimeMs)
                        : M3CommandResult.Rejected("Target is not cleanable.");

                case "butcher":
                    return target.HuntingSystem != null && !string.IsNullOrWhiteSpace(target.CarcassId)
                        ? target.HuntingSystem.Butcher(target.CarcassId, actor.Inventory, actor.ToolQuality, actor.DropSeed, unixTimeMs)
                        : M3CommandResult.Rejected("Target is not a carcass.");

                default:
                    if (type.StartsWith("station_craft:", StringComparison.Ordinal))
                    {
                        string recipeId = type.Substring("station_craft:".Length);
                        if (target.Station == null || target.StationJobSystem == null)
                        {
                            return M3CommandResult.Rejected("Target is not a station.");
                        }

                        StationJobResult start = target.StationJobSystem.StartJob(target.Station, actor.Inventory, actor.ActorId, recipeId, unixTimeMs);
                        return start.Success ? M3CommandResult.Ok() : M3CommandResult.Rejected(start.Error);
                    }

                    return M3CommandResult.Rejected("Unsupported interaction type: " + type);
            }
        }
    }

    public sealed class InteractionTarget
    {
        public ResourceNodeSystem ResourceNodeSystem { get; set; }
        public ResourceNodeState ResourceNode { get; set; }
        public StationJobSystem StationJobSystem { get; set; }
        public StationState Station { get; set; }
        public FarmPlotSystem FarmPlotSystem { get; set; }
        public FarmPlotState FarmPlot { get; set; }
        public HuntingSystem HuntingSystem { get; set; }
        public string CarcassId { get; set; } = string.Empty;
        public CleaningSystem CleaningSystem { get; set; }
        public string WorldItemId { get; set; } = string.Empty;
    }

    public sealed class InteractionActorContext
    {
        public string ActorId { get; set; } = string.Empty;
        public InventoryOwner Inventory { get; set; }
        public UnityEngine.Vector3 Position { get; set; }
        public bool HasLineOfSight { get; set; } = true;
        public string[] ToolTags { get; set; } = Array.Empty<string>();
        public int ToolQuality { get; set; } = 1;
        public int DropSeed { get; set; } = 1;
    }
}
