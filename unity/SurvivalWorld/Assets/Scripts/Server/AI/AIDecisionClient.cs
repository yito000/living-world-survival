using System;
using System.Collections.Generic;
using Survival.V1;

namespace SurvivalWorld.Server.AI
{
    public sealed class AIDecisionClient
    {
        private readonly string serverId;
        private readonly string worldId;
        private readonly ActionTemplateCatalog templates;
        private readonly IAIDecisionTransport transport;
        private readonly HashSet<string> processedDecisionIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<ActionDecision> receivedDecisions = new Queue<ActionDecision>();
        private readonly Dictionary<string, long> latestRequestStateVersionByActor = new Dictionary<string, long>(StringComparer.Ordinal);

        public AIDecisionClient(string serverId, string worldId, ActionTemplateCatalog templates, IAIDecisionTransport transport)
        {
            this.serverId = serverId ?? string.Empty;
            this.worldId = worldId ?? string.Empty;
            this.templates = templates ?? throw new ArgumentNullException(nameof(templates));
            this.transport = transport ?? NullAIDecisionTransport.Instance;
        }

        public void Start()
        {
            transport.SubscribeDecisionResults(serverId, OnDecisionReceived);
        }

        public event Action<ActionDecision> DecisionReceived;

        public DecisionRequest PublishRequest(AIActorController actor, string reason)
        {
            if (actor == null)
            {
                throw new ArgumentNullException(nameof(actor));
            }

            var request = new DecisionRequest
            {
                ActorId = actor.ActorId,
                WorldId = worldId,
                Reason = reason ?? string.Empty
            };
            request.StateVersions["personal_state"] = actor.PersonalState.Version;
            latestRequestStateVersionByActor[actor.ActorId] = actor.PersonalState.Version;
            transport.PublishDecisionRequest(serverId, request);
            return request;
        }

        public DecisionApplicationResult ApplyDecision(ActionDecision decision, AIActorController actor, long unixTimeMs)
        {
            if (decision == null)
            {
                return DecisionApplicationResult.Rejected("Decision is required.");
            }

            if (actor == null)
            {
                return DecisionApplicationResult.Rejected("Actor is required.");
            }

            if (string.IsNullOrWhiteSpace(decision.DecisionId))
            {
                return DecisionApplicationResult.Rejected("decision_id is required.");
            }

            if (processedDecisionIds.Contains(decision.DecisionId))
            {
                return DecisionApplicationResult.Duplicate(decision.DecisionId);
            }

            if (!string.Equals(decision.ActorId, actor.ActorId, StringComparison.Ordinal))
            {
                return DecisionApplicationResult.Rejected("Decision actor does not match runtime actor.");
            }

            long minimumStateVersion = latestRequestStateVersionByActor.TryGetValue(decision.ActorId, out long requestedVersion)
                ? requestedVersion
                : actor.PersonalState.Version;
            if (decision.StateVersion < minimumStateVersion)
            {
                return DecisionApplicationResult.Rejected("Decision state_version is stale.");
            }

            if (!templates.TryGet(decision.TemplateId, out ActionTemplateDefinition template))
            {
                return DecisionApplicationResult.Rejected("Unknown template: " + decision.TemplateId);
            }

            long createdAtUnixMs = decision.CreatedAtUnixMs <= 0L ? unixTimeMs : decision.CreatedAtUnixMs;
            long leaseUntilUnixMs = createdAtUnixMs + template.MaxDurationSeconds * 1000L;
            if (leaseUntilUnixMs < unixTimeMs)
            {
                return DecisionApplicationResult.Rejected("Decision lease is expired.");
            }

            ActionTemplateDefinition appliedTemplate = ShouldUseDecisionSteps(decision) ? template.WithDecisionSteps(decision.Steps) : template;
            ActionTemplateStartResult start = actor.ApplyTemplate(appliedTemplate, leaseUntilUnixMs, unixTimeMs);
            if (!start.Success)
            {
                return DecisionApplicationResult.Rejected(start.Error);
            }

            processedDecisionIds.Add(decision.DecisionId);
            return DecisionApplicationResult.Applied(decision.DecisionId, appliedTemplate.TemplateId, leaseUntilUnixMs);
        }

        public int DispatchQueuedDecisions()
        {
            int dispatched = 0;
            while (TryDequeueDecision(out ActionDecision decision))
            {
                dispatched++;
                DecisionReceived?.Invoke(decision);
            }

            return dispatched;
        }

        public void Stop()
        {
            transport.Dispose();
        }

        private bool ShouldUseDecisionSteps(ActionDecision decision)
        {
            if (decision == null || decision.Steps.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < decision.Steps.Count; i++)
            {
                string stepId = decision.Steps[i] == null ? string.Empty : decision.Steps[i].ActionTemplateId;
                if (string.IsNullOrWhiteSpace(stepId))
                {
                    continue;
                }

                if (string.Equals(stepId, decision.TemplateId, StringComparison.Ordinal) || templates.TryGet(stepId, out _))
                {
                    return false;
                }

                return true;
            }

            return false;
        }
        private void OnDecisionReceived(ActionDecision decision)
        {
            if (decision == null)
            {
                return;
            }

            lock (receivedDecisions)
            {
                receivedDecisions.Enqueue(decision);
            }
        }

        private bool TryDequeueDecision(out ActionDecision decision)
        {
            lock (receivedDecisions)
            {
                if (receivedDecisions.Count == 0)
                {
                    decision = null;
                    return false;
                }

                decision = receivedDecisions.Dequeue();
                return true;
            }
        }
    }

    public interface IAIDecisionTransport : IDisposable
    {
        void PublishDecisionRequest(string serverId, DecisionRequest request);
        void SubscribeDecisionResults(string serverId, Action<ActionDecision> onDecision);
    }

    public sealed class NullAIDecisionTransport : IAIDecisionTransport
    {
        public static readonly NullAIDecisionTransport Instance = new NullAIDecisionTransport();

        private NullAIDecisionTransport()
        {
        }

        public void PublishDecisionRequest(string serverId, DecisionRequest request)
        {
        }

        public void SubscribeDecisionResults(string serverId, Action<ActionDecision> onDecision)
        {
        }

        public void Dispose()
        {
        }
    }

    public readonly struct DecisionApplicationResult
    {
        private DecisionApplicationResult(DecisionApplicationStatus status, string decisionId, string templateId, long leaseUntilUnixMs, string error)
        {
            Status = status;
            DecisionId = decisionId ?? string.Empty;
            TemplateId = templateId ?? string.Empty;
            LeaseUntilUnixMs = leaseUntilUnixMs;
            Error = error ?? string.Empty;
        }

        public DecisionApplicationStatus Status { get; }
        public string DecisionId { get; }
        public string TemplateId { get; }
        public long LeaseUntilUnixMs { get; }
        public string Error { get; }
        public bool Success => Status == DecisionApplicationStatus.Applied || Status == DecisionApplicationStatus.Duplicate;

        public static DecisionApplicationResult Applied(string decisionId, string templateId, long leaseUntilUnixMs)
        {
            return new DecisionApplicationResult(DecisionApplicationStatus.Applied, decisionId, templateId, leaseUntilUnixMs, string.Empty);
        }

        public static DecisionApplicationResult Duplicate(string decisionId)
        {
            return new DecisionApplicationResult(DecisionApplicationStatus.Duplicate, decisionId, string.Empty, 0L, string.Empty);
        }

        public static DecisionApplicationResult Rejected(string error)
        {
            return new DecisionApplicationResult(DecisionApplicationStatus.Rejected, string.Empty, string.Empty, 0L, error);
        }
    }

    public enum DecisionApplicationStatus
    {
        Applied = 0,
        Duplicate = 1,
        Rejected = 2
    }
}
