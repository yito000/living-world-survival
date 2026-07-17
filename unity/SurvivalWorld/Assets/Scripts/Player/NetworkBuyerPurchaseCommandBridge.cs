using FishNet.Connection;
using FishNet.Object;
using Survival.V1;
using SurvivalWorld.Economy;
using SurvivalWorld.Server;
using UnityEngine;

namespace SurvivalWorld.Player
{
    public sealed class NetworkBuyerPurchaseCommandBridge : NetworkBehaviour
    {
        private ServerBootstrap cachedServerBootstrap;

        public void SubmitPurchase(BuyerPurchaseCommand command)
        {
            if (!IsOwner || command == null)
            {
                return;
            }

            SubmitBuyerPurchaseServerRpc(command.CommandId, command.BuyerInstanceId, command.StockEntryId, command.InventoryVersion);
        }

        [ServerRpc]
        private void SubmitBuyerPurchaseServerRpc(string commandId, string buyerInstanceId, string stockEntryId, long inventoryVersion)
        {
            ServerBootstrap bootstrap = GetServerBootstrap();
            if (bootstrap == null)
            {
                BuyerPurchaseResultTargetRpc(Owner, BuyerPurchaseResultStatus.Rejected.ToString(), PurchaseStatus.Rejected.ToString(), "ServerBootstrap was not found.", -1L);
                Debug.LogWarning("Buyer purchase rejected because ServerBootstrap was not found.");
                return;
            }

            var command = new BuyerPurchaseCommand
            {
                CommandId = commandId ?? string.Empty,
                BuyerInstanceId = buyerInstanceId ?? string.Empty,
                StockEntryId = stockEntryId ?? string.Empty,
                InventoryVersion = inventoryVersion
            };

            if (!bootstrap.TryApplyBuyerPurchaseCommand(Owner, command, out BuyerPurchaseResult result))
            {
                BuyerPurchaseResultTargetRpc(Owner, BuyerPurchaseResultStatus.Rejected.ToString(), PurchaseStatus.Rejected.ToString(), "Rejected before purchase.", -1L);
                Debug.LogWarning("Buyer purchase rejected before protocol execution.");
                return;
            }

            long version = result.Snapshot == null ? -1L : result.Snapshot.Version;
            BuyerPurchaseResultTargetRpc(Owner, result.Status.ToString(), result.ApiStatus.ToString(), result.Error, version);
        }

        private ServerBootstrap GetServerBootstrap()
        {
            if (cachedServerBootstrap == null)
            {
                cachedServerBootstrap = FindFirstObjectByType<ServerBootstrap>();
            }

            return cachedServerBootstrap;
        }

        [TargetRpc]
        private void BuyerPurchaseResultTargetRpc(NetworkConnection target, string status, string apiStatus, string error, long inventoryVersion)
        {
            string errorText = string.IsNullOrWhiteSpace(error) ? string.Empty : ", error=" + error;
            Debug.Log("Buyer purchase result: status=" + status + ", api_status=" + apiStatus + ", inventory_version=" + inventoryVersion + errorText);
        }
    }
}
