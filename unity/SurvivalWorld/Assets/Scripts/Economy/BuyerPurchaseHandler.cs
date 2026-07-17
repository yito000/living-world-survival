using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Survival.V1;
using SurvivalWorld.Inventory;
using UnityEngine;

namespace SurvivalWorld.Economy
{
    public readonly struct BuyerPurchaseActor
    {
        public BuyerPurchaseActor(string ownerType, string ownerId, Vector3 position, bool authenticated)
        {
            OwnerType = string.IsNullOrWhiteSpace(ownerType) ? "player" : ownerType;
            OwnerId = ownerId ?? string.Empty;
            Position = position;
            Authenticated = authenticated;
        }

        public string OwnerType { get; }
        public string OwnerId { get; }
        public Vector3 Position { get; }
        public bool Authenticated { get; }
    }

    public sealed class BuyerPurchaseHandler
    {
        private readonly BuyerRegistry buyerRegistry;
        private readonly InventoryRuntimeService inventoryRuntime;
        private readonly PurchaseProtocol purchaseProtocol;
        private readonly string worldId;
        private readonly float maxPurchaseDistance;
        private readonly Dictionary<string, BuyerPurchaseResult> processedCommands = new Dictionary<string, BuyerPurchaseResult>(StringComparer.Ordinal);

        public BuyerPurchaseHandler(
            BuyerRegistry buyerRegistry,
            InventoryRuntimeService inventoryRuntime,
            PurchaseProtocol purchaseProtocol,
            string worldId,
            float maxPurchaseDistance = 4f)
        {
            this.buyerRegistry = buyerRegistry ?? throw new ArgumentNullException(nameof(buyerRegistry));
            this.inventoryRuntime = inventoryRuntime ?? throw new ArgumentNullException(nameof(inventoryRuntime));
            this.purchaseProtocol = purchaseProtocol ?? throw new ArgumentNullException(nameof(purchaseProtocol));
            this.worldId = worldId ?? string.Empty;
            this.maxPurchaseDistance = Math.Max(0.1f, maxPurchaseDistance);
        }

        public async Task<BuyerPurchaseResult> HandleAsync(BuyerPurchaseActor actor, BuyerPurchaseCommand command, CancellationToken cancellationToken)
        {
            InventorySnapshot snapshot = inventoryRuntime.RequestSnapshot(actor.OwnerType, actor.OwnerId);
            if (!actor.Authenticated)
            {
                return BuyerPurchaseResult.Rejected("Purchaser is not authenticated.", snapshot);
            }

            if (command == null)
            {
                return BuyerPurchaseResult.Rejected("BuyerPurchaseCommand is required.", snapshot);
            }

            if (string.IsNullOrWhiteSpace(command.CommandId))
            {
                return BuyerPurchaseResult.Rejected("Command id is required.", snapshot);
            }

            string commandKey = CommandKey(actor.OwnerType, actor.OwnerId, command.CommandId);
            if (processedCommands.TryGetValue(commandKey, out BuyerPurchaseResult previous))
            {
                return previous;
            }

            if (!buyerRegistry.TryGet(command.BuyerInstanceId, out BuyerInstance buyer))
            {
                BuyerPurchaseResult missing = BuyerPurchaseResult.Rejected("Buyer does not exist.", snapshot);
                processedCommands[commandKey] = missing;
                return missing;
            }

            if (!buyer.CanAcceptNewPurchase)
            {
                BuyerPurchaseResult inactive = BuyerPurchaseResult.Rejected("Buyer is not active.", snapshot);
                processedCommands[commandKey] = inactive;
                return inactive;
            }

            if (Vector3.Distance(actor.Position, buyer.Position) > maxPurchaseDistance)
            {
                BuyerPurchaseResult distant = BuyerPurchaseResult.Rejected("Purchaser is too far from buyer.", snapshot);
                processedCommands[commandKey] = distant;
                return distant;
            }

            if (!buyer.TryGetStock(command.StockEntryId, out BuyerStockEntry stock) || !stock.HasStock)
            {
                BuyerPurchaseResult outOfStock = BuyerPurchaseResult.FromApiFailure(PurchaseStatus.OutOfStock);
                processedCommands[commandKey] = outOfStock;
                return outOfStock;
            }

            if (command.InventoryVersion != snapshot.Version)
            {
                BuyerPurchaseResult conflict = BuyerPurchaseResult.Conflict("Inventory version conflict.", snapshot);
                processedCommands[commandKey] = conflict;
                return conflict;
            }

            if (!buyer.TryBeginTransaction())
            {
                BuyerPurchaseResult inactive = BuyerPurchaseResult.Rejected("Buyer is not active.", snapshot);
                processedCommands[commandKey] = inactive;
                return inactive;
            }

            try
            {
                var context = new BuyerPurchaseContext
                {
                    WorldId = worldId,
                    OwnerType = actor.OwnerType,
                    PurchaserId = actor.OwnerId,
                    CommandId = command.CommandId,
                    BuyerInstanceId = command.BuyerInstanceId,
                    StockEntryId = command.StockEntryId,
                    InventoryVersion = command.InventoryVersion
                };

                BuyerPurchaseResult result = await purchaseProtocol.CommitPurchaseAsync(context, cancellationToken);
                if (result.RuntimeReflected)
                {
                    buyer.ApplyCommittedPurchase(command.StockEntryId, 1);
                }

                processedCommands[commandKey] = result;
                return result;
            }
            finally
            {
                buyer.CompleteTransaction();
            }
        }

        public BuyerPurchaseResult Handle(BuyerPurchaseActor actor, BuyerPurchaseCommand command)
        {
            return HandleAsync(actor, command, CancellationToken.None).GetAwaiter().GetResult();
        }

        private static string CommandKey(string ownerType, string ownerId, string commandId)
        {
            return (ownerType ?? string.Empty) + ":" + (ownerId ?? string.Empty) + ":" + (commandId ?? string.Empty);
        }
    }
}
