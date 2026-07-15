using System;
using System.Globalization;
using Cysharp.Threading.Tasks;
using FishNet.Connection;
using FishNet.Object;
using Survival.V1;
using SurvivalWorld.Inventory;
using SurvivalWorld.Server;
using UnityEngine;

namespace SurvivalWorld.Player
{
    public sealed class NetworkInventoryCommandBridge : NetworkBehaviour
    {
        private const string SmokeArg = "--sw-m2-inventory-smoke";
        private bool commandLineSmokeSubmitted;
        private ServerBootstrap cachedServerBootstrap;

        public override void OnStartClient()
        {
            base.OnStartClient();
            TryRunCommandLineSmoke();
        }

        public override void OnOwnershipClient(NetworkConnection prevOwner)
        {
            base.OnOwnershipClient(prevOwner);
            TryRunCommandLineSmoke();
        }

        public void SubmitAdd(string commandId, string itemDefinitionId, int quantity)
        {
            if (!IsOwner)
            {
                return;
            }

            SubmitInventoryAddServerRpc(commandId, itemDefinitionId, quantity);
        }

        public void SubmitCommand(InventoryCommand command)
        {
            if (!IsOwner || command == null)
            {
                return;
            }

            string itemDefinitionId = command.ItemRef == null ? string.Empty : command.ItemRef.ItemDefinitionId;
            string itemInstanceId = command.ItemRef == null ? string.Empty : command.ItemRef.ItemInstanceId;
            string targetDefinitionId = command.TargetRef == null ? string.Empty : command.TargetRef.ItemDefinitionId;
            string targetInstanceId = command.TargetRef == null ? string.Empty : command.TargetRef.ItemInstanceId;
            SubmitInventoryCommandServerRpc(
                command.CommandId,
                (int)command.Operation,
                itemDefinitionId,
                itemInstanceId,
                command.Quantity,
                targetDefinitionId,
                targetInstanceId,
                command.ExpectedVersion);
        }

        private void TryRunCommandLineSmoke()
        {
            if (commandLineSmokeSubmitted || !IsOwner || !HasCommandLineArg(SmokeArg))
            {
                return;
            }

            commandLineSmokeSubmitted = true;
            RunCommandLineSmokeAsync(destroyCancellationToken).Forget();
        }

        private async UniTaskVoid RunCommandLineSmokeAsync(System.Threading.CancellationToken cancellationToken)
        {
            float delaySeconds = GetCommandLineFloat("--sw-m2-inventory-delay", 1.0f);
            await UniTask.Delay(TimeSpan.FromSeconds(Math.Max(0f, delaySeconds)), cancellationToken: cancellationToken);

            string runId = GetCommandLineValue("--sw-m2-inventory-run-id", "m2-smoke-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
            string itemDefinitionId = GetCommandLineValue("--sw-m2-inventory-item", "stone");
            int quantity = GetCommandLineInt("--sw-m2-inventory-quantity", 5);
            int targetSlot = GetCommandLineInt("--sw-m2-inventory-target-slot", 2);
            int dropQuantity = GetCommandLineInt("--sw-m2-inventory-drop-quantity", 1);

            SubmitInventoryAddServerRpc(runId + "-add", itemDefinitionId, quantity);
            await UniTask.Delay(TimeSpan.FromMilliseconds(250), cancellationToken: cancellationToken);

            SubmitInventoryCommandServerRpc(runId + "-move", (int)InventoryOperation.Move, itemDefinitionId, string.Empty, 0, "slot:" + targetSlot.ToString(CultureInfo.InvariantCulture), string.Empty, -1);
            await UniTask.Delay(TimeSpan.FromMilliseconds(250), cancellationToken: cancellationToken);

            SubmitInventoryCommandServerRpc(runId + "-drop", (int)InventoryOperation.Drop, itemDefinitionId, string.Empty, dropQuantity, string.Empty, string.Empty, -1);
            Debug.Log("Submitted M2 inventory smoke commands: run_id=" + runId + ", item=" + itemDefinitionId + ", quantity=" + quantity + ".");
        }

        [ServerRpc]
        private void SubmitInventoryAddServerRpc(string commandId, string itemDefinitionId, int quantity)
        {
            ServerBootstrap bootstrap = GetServerBootstrap();
            if (bootstrap == null)
            {
                SendInventoryCommandResult(Owner, "ADD", null, "ServerBootstrap was not found.");
                Debug.LogWarning("Inventory ADD rejected because ServerBootstrap was not found.");
                return;
            }

            if (!bootstrap.TryApplyInventoryAdd(Owner, commandId, itemDefinitionId, quantity, out InventoryMutationResult result))
            {
                SendInventoryCommandResult(Owner, "ADD", null, "Rejected before mutation.");
                Debug.LogWarning("Inventory ADD rejected before mutation.");
                return;
            }

            SendInventoryCommandResult(Owner, "ADD", result, string.Empty);
            LogServerRpcResult("ADD", result);
        }

        [ServerRpc]
        private void SubmitInventoryCommandServerRpc(string commandId, int operation, string itemDefinitionId, string itemInstanceId, int quantity, string targetDefinitionId, string targetInstanceId, long expectedVersion)
        {
            ServerBootstrap bootstrap = GetServerBootstrap();
            if (bootstrap == null)
            {
                SendInventoryCommandResult(Owner, ((InventoryOperation)operation).ToString(), null, "ServerBootstrap was not found.");
                Debug.LogWarning("Inventory command rejected because ServerBootstrap was not found.");
                return;
            }

            var command = new InventoryCommand
            {
                CommandId = commandId,
                ExpectedVersion = expectedVersion,
                Operation = (InventoryOperation)operation,
                Quantity = quantity,
                ItemRef = new ItemRef
                {
                    ItemDefinitionId = itemDefinitionId ?? string.Empty,
                    ItemInstanceId = itemInstanceId ?? string.Empty
                },
                TargetRef = new ItemRef
                {
                    ItemDefinitionId = targetDefinitionId ?? string.Empty,
                    ItemInstanceId = targetInstanceId ?? string.Empty
                }
            };

            if (!bootstrap.TryApplyInventoryCommand(Owner, command, out InventoryMutationResult result))
            {
                SendInventoryCommandResult(Owner, command.Operation.ToString(), null, "Rejected before mutation.");
                Debug.LogWarning("Inventory command rejected before mutation: op=" + command.Operation + ".");
                return;
            }

            SendInventoryCommandResult(Owner, command.Operation.ToString(), result, string.Empty);
            LogServerRpcResult(command.Operation.ToString(), result);
        }

        private ServerBootstrap GetServerBootstrap()
        {
            if (cachedServerBootstrap == null)
            {
                cachedServerBootstrap = FindFirstObjectByType<ServerBootstrap>();
            }

            return cachedServerBootstrap;
        }

        private void SendInventoryCommandResult(NetworkConnection target, string operation, InventoryMutationResult result, string fallbackError)
        {
            if (target == null)
            {
                return;
            }

            string status = result == null ? InventoryMutationStatus.Rejected.ToString() : result.Status.ToString();
            string eventId = result == null || result.DomainEvent == null ? string.Empty : result.DomainEvent.EventId;
            string error = result == null ? fallbackError : result.Error;
            long version = result == null || result.Snapshot == null ? -1 : result.Snapshot.Version;
            InventoryCommandResultTargetRpc(target, operation, status, eventId, error ?? string.Empty, version);
        }

        [TargetRpc]
        private void InventoryCommandResultTargetRpc(NetworkConnection target, string operation, string status, string eventId, string error, long version)
        {
            string errorText = string.IsNullOrWhiteSpace(error) ? string.Empty : ", error=" + error;
            Debug.Log("Inventory command result: op=" + operation + ", status=" + status + ", version=" + version + ", event_id=" + eventId + errorText);
        }

        private static void LogServerRpcResult(string operation, InventoryMutationResult result)
        {
            if (result == null)
            {
                return;
            }

            string eventId = result.DomainEvent == null ? string.Empty : result.DomainEvent.EventId;
            string error = string.IsNullOrWhiteSpace(result.Error) ? string.Empty : ", error=" + result.Error;
            Debug.Log("Inventory ServerRpc result: op=" + operation + ", status=" + result.Status + ", event_id=" + eventId + error);
        }

        private static bool HasCommandLineArg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetCommandLineValue(string name, string fallback)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.Ordinal))
                {
                    return args[i + 1];
                }
            }

            return fallback;
        }

        private static int GetCommandLineInt(string name, int fallback)
        {
            string value = GetCommandLineValue(name, string.Empty);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;
        }

        private static float GetCommandLineFloat(string name, float fallback)
        {
            string value = GetCommandLineValue(name, string.Empty);
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ? parsed : fallback;
        }
    }
}