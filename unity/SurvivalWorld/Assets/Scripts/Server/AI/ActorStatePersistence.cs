using System;
using System.Globalization;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Survival.V1;
using SurvivalWorld.Inventory;
using ProtoInventoryEntry = Survival.V1.InventoryEntry;
using RuntimeInventoryEntry = SurvivalWorld.Inventory.InventoryEntry;

namespace SurvivalWorld.Server.AI
{
    public interface IActorStateGateway
    {
        UniTask<SaveResponse> SaveAsync(SaveRequest request, CancellationToken cancellationToken);
    }

    public sealed class GeneratedActorStateGateway : IActorStateGateway
    {
        private readonly ActorStateService.ActorStateServiceClient client;
        private readonly string serviceSecret;

        public GeneratedActorStateGateway(ActorStateService.ActorStateServiceClient client, string serviceSecret)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.serviceSecret = serviceSecret ?? string.Empty;
        }

        public async UniTask<SaveResponse> SaveAsync(SaveRequest request, CancellationToken cancellationToken)
        {
            return await client.SaveAsync(request, CreateHeaders(), cancellationToken: cancellationToken).ResponseAsync.AsUniTask();
        }

        private Metadata CreateHeaders()
        {
            return string.IsNullOrWhiteSpace(serviceSecret) ? null : new Metadata { { "x-service-secret", serviceSecret } };
        }
    }

    public sealed class NullActorStateGateway : IActorStateGateway
    {
        public static readonly NullActorStateGateway Instance = new NullActorStateGateway();

        private NullActorStateGateway()
        {
        }

        public UniTask<SaveResponse> SaveAsync(SaveRequest request, CancellationToken cancellationToken)
        {
            return UniTask.FromResult(new SaveResponse { Status = ResultStatus.Ok });
        }
    }

    public sealed class AIActorRuntimeStatePersistence
    {
        private readonly IActorStateGateway gateway;
        private readonly string worldId;

        public AIActorRuntimeStatePersistence(IActorStateGateway gateway)
            : this(gateway, string.Empty)
        {
        }

        public AIActorRuntimeStatePersistence(IActorStateGateway gateway, string worldId)
        {
            this.gateway = gateway ?? NullActorStateGateway.Instance;
            this.worldId = worldId ?? string.Empty;
        }

        public async UniTask<SaveResponse> SaveAsync(AIActorController actor, CancellationToken cancellationToken)
        {
            if (actor == null)
            {
                throw new ArgumentNullException(nameof(actor));
            }

            SaveRequest request = CreateSaveRequest(actor);
            return await gateway.SaveAsync(request, cancellationToken);
        }

        public SaveRequest CreateSaveRequest(AIActorController actor)
        {
            var request = new SaveRequest
            {
                ActorId = actor.ActorId,
                Version = actor.PersonalState.Version,
                PersonalState = ByteString.CopyFromUtf8(SerializePersonalState(actor.PersonalState, worldId))
            };

            InventorySnapshot snapshot = actor.Inventory == null ? null : actor.Inventory.RequestSnapshot();
            if (snapshot != null)
            {
                for (int i = 0; i < snapshot.Entries.Count; i++)
                {
                    RuntimeInventoryEntry entry = snapshot.Entries[i];
                    request.InventorySummary.Add(new ProtoInventoryEntry
                    {
                        SlotIndex = entry.SlotIndex,
                        Item = new ItemRef
                        {
                            ItemDefinitionId = entry.ItemDefinitionId,
                            ItemInstanceId = entry.ItemInstanceId
                        },
                        Quantity = entry.Quantity,
                        Reserved = entry.Reserved
                    });
                }
            }

            return request;
        }

        private static string SerializePersonalState(AIPersonalState state, string worldId)
        {
            StringBuilder builder = new StringBuilder(512);
            builder.Append('{');
            AppendField(builder, "actor_id", state.ActorId).Append(',');
            AppendField(builder, "world_id", worldId).Append(',');
            AppendField(builder, "version", state.Version).Append(',');
            AppendField(builder, "hunger", state.Hunger).Append(',');
            AppendField(builder, "urgency_food", state.Urgency.Food).Append(',');
            AppendField(builder, "urgency_cleanup", state.Urgency.Cleanup).Append(',');
            AppendField(builder, "urgency_earn", state.Urgency.Earn).Append(',');
            AppendField(builder, "need_score", state.Urgency.NeedScore).Append(',');
            AppendField(builder, "inventory_pressure", state.Urgency.InventoryPressure).Append(',');
            AppendField(builder, "cleanliness_pressure", state.Urgency.CleanlinessPressure).Append(',');
            AppendField(builder, "wealth_score", state.Urgency.WealthScore).Append(',');
            AppendField(builder, "active_template", state.ActionState.ActiveTemplateId).Append(',');
            AppendField(builder, "template_version", state.ActionState.TemplateVersion).Append(',');
            AppendField(builder, "started_at_unix_ms", state.ActionState.StartedAtUnixMs).Append(',');
            AppendField(builder, "lease_until_unix_ms", state.ActionState.LeaseUntilUnixMs).Append(',');
            AppendField(builder, "failure_count", state.ActionState.FailureCount);
            builder.Append('}');
            return builder.ToString();
        }

        private static StringBuilder AppendField(StringBuilder builder, string name, string value)
        {
            builder.Append('"').Append(name).Append("\":\"").Append(Escape(value)).Append('"');
            return builder;
        }

        private static StringBuilder AppendField(StringBuilder builder, string name, long value)
        {
            builder.Append('"').Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
            return builder;
        }

        private static StringBuilder AppendField(StringBuilder builder, string name, int value)
        {
            builder.Append('"').Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
            return builder;
        }

        private static StringBuilder AppendField(StringBuilder builder, string name, float value)
        {
            builder.Append('"').Append(name).Append("\":").Append(value.ToString("0.###", CultureInfo.InvariantCulture));
            return builder;
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
