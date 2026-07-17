using System;
using System.Globalization;
using FishNet.Connection;
using FishNet.Object;
using Survival.V1;
using SurvivalWorld.Client.Interaction;
using SurvivalWorld.Client.UI;
using SurvivalWorld.Server;
using SurvivalWorld.Server.Simulation;
using UnityEngine;

namespace SurvivalWorld.Player
{
    public sealed class NetworkInteractionCommandBridge : NetworkBehaviour
    {
        private long localSequence;
        private ServerBootstrap cachedServerBootstrap;

        public void SubmitCandidate(InteractionCandidate candidate)
        {
            if (!IsOwner || !candidate.IsValid) return;
            SubmitInteractionServerRpc(NextCommandId("interact"), candidate.TargetNetworkId, candidate.InteractionType, candidate.ExpectedVersion);
        }

        [ServerRpc]
        private void SubmitInteractionServerRpc(string commandId, uint targetNetworkId, string interactionType, long expectedVersion)
        {
            ServerBootstrap bootstrap = GetServerBootstrap();
            if (bootstrap == null)
            {
                InteractionResultTargetRpc(Owner, false, "ServerBootstrap was not found.", string.Empty);
                Debug.LogWarning("Interaction command rejected because ServerBootstrap was not found.");
                return;
            }

            var command = new InteractCommand
            {
                CommandId = commandId ?? string.Empty,
                TargetNetworkId = targetNetworkId,
                InteractionType = interactionType ?? string.Empty,
                ExpectedVersion = expectedVersion
            };

            if (!bootstrap.TryApplyInteractCommand(Owner, command, out M3CommandResult result))
            {
                InteractionResultTargetRpc(Owner, false, "Rejected before interaction.", command.InteractionType);
                return;
            }

            InteractionResultTargetRpc(Owner, result.Success, result.Error, command.InteractionType);
            string error = string.IsNullOrWhiteSpace(result.Error) ? string.Empty : ", error=" + result.Error;
            Debug.Log("Interaction command result: type=" + command.InteractionType + ", success=" + result.Success + error);
        }

        [TargetRpc]
        private void InteractionResultTargetRpc(NetworkConnection target, bool success, string error, string interactionType)
        {
            string message = success ? "Interaction accepted: " + interactionType : "Interaction rejected: " + (string.IsNullOrWhiteSpace(error) ? interactionType : error);
            ActionFeedbackPresenter.GetOrCreate().Show(message);
            Debug.Log(message);
        }

        private ServerBootstrap GetServerBootstrap()
        {
            if (cachedServerBootstrap == null) cachedServerBootstrap = FindFirstObjectByType<ServerBootstrap>();
            return cachedServerBootstrap;
        }

        private string NextCommandId(string prefix)
        {
            localSequence++;
            return prefix + "-" + localSequence.ToString(CultureInfo.InvariantCulture) + "-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        }
    }
}