using System;
using System.Globalization;
using FishNet.Connection;
using FishNet.Object;
using Survival.V1;
using SurvivalWorld.Client.UI;
using SurvivalWorld.Server;
using SurvivalWorld.Server.Simulation;
using UnityEngine;

namespace SurvivalWorld.Player
{
    public sealed class NetworkPrimaryActionCommandBridge : NetworkBehaviour
    {
        private long localSequence;
        private ServerBootstrap cachedServerBootstrap;

        public void SubmitPrimaryAction(int equipmentSlot, Vector3 aimOrigin, Vector3 aimDirection, long clientTick)
        {
            if (!IsOwner) return;
            Vector3 direction = aimDirection.sqrMagnitude <= 0.0001f ? transform.forward : aimDirection.normalized;
            SubmitPrimaryActionServerRpc(NextCommandId("primary"), equipmentSlot, aimOrigin.x, aimOrigin.y, aimOrigin.z, direction.x, direction.y, direction.z, clientTick);
        }

        [ServerRpc]
        private void SubmitPrimaryActionServerRpc(string commandId, int equipmentSlot, float ox, float oy, float oz, float dx, float dy, float dz, long clientTick)
        {
            ServerBootstrap bootstrap = GetServerBootstrap();
            if (bootstrap == null)
            {
                PrimaryActionResultTargetRpc(Owner, false, false, 0f, string.Empty, "ServerBootstrap was not found.");
                return;
            }

            var command = new PrimaryActionCommand
            {
                CommandId = commandId ?? string.Empty,
                EquipmentSlot = equipmentSlot,
                AimOrigin = new Vec3 { X = ox, Y = oy, Z = oz },
                AimDirection = new Vec3 { X = dx, Y = dy, Z = dz },
                ClientTick = clientTick
            };

            if (!bootstrap.TryApplyPrimaryActionCommand(Owner, command, out HuntingAttackResult result))
            {
                PrimaryActionResultTargetRpc(Owner, false, false, 0f, string.Empty, "Rejected before primary action.");
                return;
            }

            PrimaryActionResultTargetRpc(Owner, result.Success, result.Killed, result.AppliedDamage, result.CarcassId, result.Error);
            string error = string.IsNullOrWhiteSpace(result.Error) ? string.Empty : ", error=" + result.Error;
            Debug.Log("Primary action result: success=" + result.Success + ", killed=" + result.Killed + ", damage=" + result.AppliedDamage + error);
        }

        [TargetRpc]
        private void PrimaryActionResultTargetRpc(NetworkConnection target, bool success, bool killed, float damage, string carcassId, string error)
        {
            string message = !success ? "Attack rejected: " + (string.IsNullOrWhiteSpace(error) ? "no valid animal target" : error) : killed ? "Animal killed: carcass=" + carcassId : "Animal hit: damage=" + damage.ToString("0.#", CultureInfo.InvariantCulture);
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