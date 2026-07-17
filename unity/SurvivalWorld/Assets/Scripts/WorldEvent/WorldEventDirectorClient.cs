using System;
using System.Collections.Generic;
using Google.Protobuf;
using Survival.V1;

namespace SurvivalWorld.Server.WorldEvents
{
    public sealed class WorldEventDirectorClient
    {
        private readonly string worldId;
        private readonly IWorldEventServiceGateway gateway;
        private readonly IWorldEventSpawnSink spawnSink;
        private readonly Dictionary<string, WorldEventInstanceRunner> runnersById = new Dictionary<string, WorldEventInstanceRunner>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> activeRegionToInstance = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, WorldEventProposalResult> handledProposals = new Dictionary<string, WorldEventProposalResult>(StringComparer.Ordinal);

        public WorldEventDirectorClient(string worldId, IWorldEventServiceGateway gateway, IWorldEventSpawnSink spawnSink)
        {
            this.worldId = worldId ?? string.Empty;
            this.gateway = gateway ?? NullWorldEventServiceGateway.Instance;
            this.spawnSink = spawnSink ?? NullWorldEventSpawnSink.Instance;
            MaxActiveEvents = 3;
        }

        public int MaxActiveEvents { get; set; }
        public IReadOnlyDictionary<string, WorldEventInstanceRunner> RunnersById => runnersById;

        public WorldEventProposalResult HandleProposal(EventProposal proposal, long unixTimeMs)
        {
            string proposalId = proposal == null ? string.Empty : proposal.ProposalId;
            if (!string.IsNullOrWhiteSpace(proposalId) && handledProposals.TryGetValue(proposalId, out WorldEventProposalResult existing))
            {
                return WorldEventProposalResult.Duplicate(proposalId, existing.EventInstanceId);
            }

            WorldEventParameterBag parameters = proposal == null ? WorldEventParameterBag.Empty : WorldEventParameterBag.From(proposal.Params);
            if (!ValidateProposal(proposal, parameters, out string reasonCode))
            {
                var rejected = WorldEventProposalResult.Rejected(proposalId, reasonCode);
                if (!string.IsNullOrWhiteSpace(proposalId))
                {
                    handledProposals[proposalId] = rejected;
                }

                return rejected;
            }

            RegisterRequest register = CreateRegisterRequest(proposal);
            RegisterResponse response = gateway.Register(register) ?? new RegisterResponse();
            string eventInstanceId = string.IsNullOrWhiteSpace(response.EventInstanceId)
                ? "local-" + proposal.ProposalId
                : response.EventInstanceId;

            var config = new WorldEventInstanceConfig(eventInstanceId, proposal.ProposalId, proposal.TemplateId, proposal.WorldId, proposal.RegionId, parameters);
            var runner = new WorldEventInstanceRunner(config, WorldEventEffectFactory.Create(proposal.TemplateId, parameters));
            UpdateStateResponse activeResponse = gateway.UpdateState(new UpdateStateRequest
            {
                EventInstanceId = eventInstanceId,
                ExpectedState = WorldEventState.Proposed,
                NewState = WorldEventState.Active
            }) ?? new UpdateStateResponse { Status = ResultStatus.Ok };

            if (activeResponse.Status != ResultStatus.Ok && activeResponse.Status != ResultStatus.Duplicate)
            {
                var rejected = WorldEventProposalResult.Rejected(proposal.ProposalId, "state_update_rejected");
                handledProposals[proposal.ProposalId] = rejected;
                return rejected;
            }

            runner.Activate(unixTimeMs);
            runnersById[eventInstanceId] = runner;
            activeRegionToInstance[proposal.RegionId] = eventInstanceId;

            WorldEventProposalResult approved = WorldEventProposalResult.ApprovedResult(proposal.ProposalId, eventInstanceId);
            handledProposals[proposal.ProposalId] = approved;
            return approved;
        }

        public int Tick(long unixTimeMs)
        {
            if (runnersById.Count == 0)
            {
                return 0;
            }

            var runners = new List<WorldEventInstanceRunner>(runnersById.Values);
            int completed = 0;
            for (int i = 0; i < runners.Count; i++)
            {
                WorldEventInstanceRunner runner = runners[i];
                if (!runner.IsActive)
                {
                    continue;
                }

                bool didComplete = runner.Tick(unixTimeMs, spawnSink);
                if (!didComplete)
                {
                    continue;
                }

                gateway.UpdateState(new UpdateStateRequest
                {
                    EventInstanceId = runner.Config.EventInstanceId,
                    ExpectedState = WorldEventState.Active,
                    NewState = WorldEventState.Completed,
                    Stats = runner.Stats.ToByteString()
                });
                activeRegionToInstance.Remove(runner.Config.RegionId);
                completed++;
            }

            return completed;
        }

        public bool TryGetRunner(string eventInstanceId, out WorldEventInstanceRunner runner)
        {
            return runnersById.TryGetValue(eventInstanceId ?? string.Empty, out runner);
        }

        private bool ValidateProposal(EventProposal proposal, WorldEventParameterBag parameters, out string reasonCode)
        {
            reasonCode = string.Empty;
            if (proposal == null)
            {
                reasonCode = "proposal_required";
                return false;
            }

            if (string.IsNullOrWhiteSpace(proposal.ProposalId))
            {
                reasonCode = "proposal_id_required";
                return false;
            }

            if (!WorldEventEffectFactory.IsSupported(proposal.TemplateId))
            {
                reasonCode = "unsupported_template";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(worldId) && !string.Equals(worldId, proposal.WorldId, StringComparison.Ordinal))
            {
                reasonCode = "world_mismatch";
                return false;
            }

            if (string.IsNullOrWhiteSpace(proposal.RegionId))
            {
                reasonCode = "region_required";
                return false;
            }

            if (parameters.TryGetInt("rules_version", out int rulesVersion) && rulesVersion != WorldEventRules.Version)
            {
                reasonCode = "version_mismatch";
                return false;
            }

            if (ActiveCount() >= Math.Max(1, MaxActiveEvents))
            {
                reasonCode = "load_shed";
                return false;
            }

            if (activeRegionToInstance.ContainsKey(proposal.RegionId))
            {
                reasonCode = "region_conflict";
                return false;
            }

            return true;
        }

        private int ActiveCount()
        {
            int count = 0;
            foreach (WorldEventInstanceRunner runner in runnersById.Values)
            {
                if (runner.IsActive)
                {
                    count++;
                }
            }

            return count;
        }

        private static RegisterRequest CreateRegisterRequest(EventProposal proposal)
        {
            return new RegisterRequest
            {
                ProposalId = proposal.ProposalId,
                TemplateId = proposal.TemplateId,
                WorldId = proposal.WorldId,
                RegionId = proposal.RegionId,
                Params = proposal.Params ?? ByteString.Empty
            };
        }
    }
}