using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Survival.V1;

namespace SurvivalWorld.World
{
    public readonly struct WorldBootstrapResult
    {
        private WorldBootstrapResult(bool ok, string error, int eventsApplied, long sequence)
        {
            Ok = ok;
            Error = error ?? string.Empty;
            EventsApplied = eventsApplied;
            Sequence = sequence;
        }

        public bool Ok { get; }
        public string Error { get; }
        public int EventsApplied { get; }
        public long Sequence { get; }

        public static WorldBootstrapResult Success(int eventsApplied, long sequence)
        {
            return new WorldBootstrapResult(true, string.Empty, eventsApplied, sequence);
        }

        public static WorldBootstrapResult Failure(string error)
        {
            return new WorldBootstrapResult(false, error, 0, 0);
        }
    }

    public sealed class WorldBootstrapClient
    {
        private readonly IWorldDataGateway gateway;
        private readonly WorldRuntimeState runtimeState;

        public WorldBootstrapClient(IWorldDataGateway gateway, WorldRuntimeState runtimeState)
        {
            this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            this.runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        }

        public async UniTask<WorldBootstrapResult> LoadAsync(string worldId, string serverBuild, CancellationToken cancellationToken)
        {
            try
            {
                LoadBootstrapResponse response = await gateway.LoadBootstrapAsync(worldId, serverBuild, cancellationToken);
                runtimeState.RestoreSnapshot(response.SnapshotPayload, response.Sequence);
                int applied = runtimeState.ApplyEventTail(response.EventTail);
                return WorldBootstrapResult.Success(applied, response.Sequence);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return WorldBootstrapResult.Failure(ex.Message);
            }
        }
    }
}
