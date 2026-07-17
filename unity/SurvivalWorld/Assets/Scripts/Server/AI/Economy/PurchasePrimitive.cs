using System;
using System.Globalization;
using Survival.V1;
using SurvivalWorld.Economy;
using UnityEngine;

namespace SurvivalWorld.Server.AI.Economy
{
    public static class PurchasePrimitive
    {
        public const string Purchase = "Purchase";
        public const string PurchaseStub = "PurchaseStub";
        public const string SellStub = "SellStub";

        public static void Register(PrimitiveActionRegistry registry, BuyerPurchaseHandler handler)
        {
            if (registry == null || handler == null)
            {
                return;
            }

            registry.Register(Purchase, context => ExecutePurchase(context, handler));
            registry.Register(PurchaseStub, context => ExecutePurchase(context, handler));
            registry.Register(SellStub, context => PrimitiveActionResult.Completed());
        }

        private static PrimitiveActionResult ExecutePurchase(PrimitiveActionContext context, BuyerPurchaseHandler handler)
        {
            if (context == null)
            {
                return PrimitiveActionResult.Failed("Context is required.", false);
            }

            string buyerInstanceId = context.GetParameter("buyer_instance_id");
            string stockEntryId = context.GetParameter("stock_entry_id");
            if (string.IsNullOrWhiteSpace(buyerInstanceId) || string.IsNullOrWhiteSpace(stockEntryId))
            {
                return PrimitiveActionResult.Failed("buyer_instance_id and stock_entry_id are required.", true);
            }

            SurvivalWorld.Inventory.InventorySnapshot snapshot = context.Inventory == null ? null : context.Inventory.RequestSnapshot();
            long version = snapshot == null ? 0L : snapshot.Version;
            string commandId = context.GetParameter("command_id");
            if (string.IsNullOrWhiteSpace(commandId))
            {
                commandId = "ai-purchase:" + context.ActorId + ":" + buyerInstanceId + ":" + stockEntryId;
                context.Blackboard["command_id"] = commandId;
            }

            var command = new BuyerPurchaseCommand
            {
                CommandId = commandId,
                BuyerInstanceId = buyerInstanceId,
                StockEntryId = stockEntryId,
                InventoryVersion = version
            };

            var actor = new BuyerPurchaseActor("ai", context.ActorId, ResolveActorPosition(context), true);
            BuyerPurchaseResult result = handler.Handle(actor, command);
            if (result.Success)
            {
                return PrimitiveActionResult.Completed();
            }

            bool retryable = result.Status == BuyerPurchaseResultStatus.Pending || result.Status == BuyerPurchaseResultStatus.Conflict;
            string error = string.IsNullOrWhiteSpace(result.Error) ? result.Status.ToString() : result.Error;
            return PrimitiveActionResult.Failed(error, retryable);
        }

        private static Vector3 ResolveActorPosition(PrimitiveActionContext context)
        {
            return new Vector3(
                ParseFloat(context.GetParameter("actor_x")),
                ParseFloat(context.GetParameter("actor_y")),
                ParseFloat(context.GetParameter("actor_z")));
        }

        private static float ParseFloat(string value)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ? parsed : 0f;
        }
    }
}
