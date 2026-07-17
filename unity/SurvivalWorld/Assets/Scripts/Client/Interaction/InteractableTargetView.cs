using System;
using System.Globalization;
using FishNet.Object;
using UnityEngine;

namespace SurvivalWorld.Client.Interaction
{
    public enum InteractionKind { None, Mine, StationCraft, StationCancel, FarmPlant, FarmHarvest, Butcher, Clean, Buyer, Pickup, Animal }

    public sealed class InteractableTargetView : MonoBehaviour
    {
        [SerializeField] private InteractionKind interactionKind = InteractionKind.Mine;
        [SerializeField] private uint explicitTargetNetworkId;
        [SerializeField] private long expectedVersion;
        [SerializeField] private string displayName = "Target";
        [SerializeField] private string promptText = string.Empty;
        [SerializeField] private string interactionTypeOverride = string.Empty;
        [SerializeField] private string recipeId = string.Empty;
        [SerializeField] private string serverStateId = string.Empty;
        [SerializeField] private string resourceType = "stone";
        [SerializeField] private string stationType = "anvil";
        [SerializeField] private int maximumAmount = 10;
        [SerializeField] private bool primaryActionTarget;
        [SerializeField] private NetworkObject networkObject;

        public InteractionKind Kind => interactionKind;
        public long ExpectedVersion => expectedVersion;
        public string DisplayName => displayName ?? string.Empty;
        public string PromptText => string.IsNullOrWhiteSpace(promptText) ? DefaultPromptText() : promptText;
        public string ServerStateId => string.IsNullOrWhiteSpace(serverStateId) ? ResolveTargetNetworkId().ToString(CultureInfo.InvariantCulture) : serverStateId;
        public string ResourceType => string.IsNullOrWhiteSpace(resourceType) ? "stone" : resourceType;
        public string StationType => string.IsNullOrWhiteSpace(stationType) ? "anvil" : stationType;
        public string RecipeId => recipeId ?? string.Empty;
        public int MaximumAmount => Mathf.Max(1, maximumAmount);
        public bool IsPrimaryActionTarget => primaryActionTarget || interactionKind == InteractionKind.Animal;

        private void Awake()
        {
            if (networkObject == null) networkObject = GetComponentInParent<NetworkObject>();
        }

        public void Configure(InteractionKind kind, uint id, string name, string prompt, string type, long version, string stateId, string resource, string station, string recipe, int maxAmount, bool primaryTarget)
        {
            interactionKind = kind;
            explicitTargetNetworkId = id;
            displayName = name ?? string.Empty;
            promptText = prompt ?? string.Empty;
            interactionTypeOverride = type ?? string.Empty;
            expectedVersion = version;
            serverStateId = stateId ?? string.Empty;
            resourceType = resource ?? string.Empty;
            stationType = station ?? string.Empty;
            recipeId = recipe ?? string.Empty;
            maximumAmount = Mathf.Max(1, maxAmount);
            primaryActionTarget = primaryTarget;
        }

        public bool TryBuildCandidate(out InteractionCandidate candidate)
        {
            uint id = ResolveTargetNetworkId();
            if (id == 0 || interactionKind == InteractionKind.None)
            {
                candidate = default;
                return false;
            }

            candidate = new InteractionCandidate(this, id, ResolveInteractionType(), expectedVersion, DisplayName, PromptText, interactionKind);
            return true;
        }

        public uint ResolveTargetNetworkId()
        {
            if (networkObject != null)
            {
                try
                {
                    uint id = Convert.ToUInt32(networkObject.ObjectId, CultureInfo.InvariantCulture);
                    if (id != 0) return id;
                }
                catch (OverflowException) { }
            }

            return explicitTargetNetworkId;
        }

        public string ResolveInteractionType()
        {
            if (!string.IsNullOrWhiteSpace(interactionTypeOverride)) return interactionTypeOverride;
            switch (interactionKind)
            {
                case InteractionKind.Mine: return "mine";
                case InteractionKind.StationCraft: return "station_craft:" + (string.IsNullOrWhiteSpace(recipeId) ? "stone_spear" : recipeId);
                case InteractionKind.StationCancel: return "station_cancel";
                case InteractionKind.FarmPlant: return "farm_plant";
                case InteractionKind.FarmHarvest: return "farm_harvest";
                case InteractionKind.Butcher: return "butcher";
                case InteractionKind.Clean: return "clean";
                case InteractionKind.Buyer: return "buyer_open";
                case InteractionKind.Pickup: return "pickup";
                default: return string.Empty;
            }
        }

        private string DefaultPromptText()
        {
            return interactionKind == InteractionKind.Animal ? "LMB Attack " + DisplayName : "E " + DisplayName;
        }
    }

    public readonly struct InteractionCandidate
    {
        public InteractionCandidate(InteractableTargetView target, uint id, string type, long version, string name, string prompt, InteractionKind kind)
        {
            Target = target;
            TargetNetworkId = id;
            InteractionType = type ?? string.Empty;
            ExpectedVersion = version;
            DisplayName = name ?? string.Empty;
            PromptText = prompt ?? string.Empty;
            Kind = kind;
        }

        public InteractableTargetView Target { get; }
        public uint TargetNetworkId { get; }
        public string InteractionType { get; }
        public long ExpectedVersion { get; }
        public string DisplayName { get; }
        public string PromptText { get; }
        public InteractionKind Kind { get; }
        public bool IsValid => TargetNetworkId != 0 && !string.IsNullOrWhiteSpace(InteractionType);
    }
}