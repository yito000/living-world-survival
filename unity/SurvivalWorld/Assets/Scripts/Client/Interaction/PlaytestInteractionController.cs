using System;
using R3;
using SurvivalWorld.Client.UI;
using SurvivalWorld.Player;
using UnityEngine;

namespace SurvivalWorld.Client.Interaction
{
    [RequireComponent(typeof(ThirdPersonInputReader))]
    public sealed class PlaytestInteractionController : MonoBehaviour
    {
        [SerializeField] private ThirdPersonInputReader inputReader;
        [SerializeField] private InteractionScanner scanner;
        [SerializeField] private NetworkInteractionCommandBridge interactionBridge;
        [SerializeField] private NetworkPrimaryActionCommandBridge primaryActionBridge;
        [SerializeField] private NetworkBuyerPurchaseCommandBridge buyerPurchaseBridge;
        [SerializeField] private InteractionPromptView promptView;
        [SerializeField] private ActionFeedbackPresenter feedbackPresenter;
        private IDisposable interactSubscription;
        private IDisposable primaryActionSubscription;

        private void Awake() => ResolveReferences();

        private void OnEnable()
        {
            ResolveReferences();
            interactSubscription = inputReader.InteractPressed.Subscribe(_ => SubmitInteraction());
            primaryActionSubscription = inputReader.PrimaryActionPressed.Subscribe(_ => SubmitPrimaryAction());
        }

        private void OnDisable()
        {
            interactSubscription?.Dispose();
            primaryActionSubscription?.Dispose();
            interactSubscription = null;
            primaryActionSubscription = null;
        }

        private void Update()
        {
            if (!CanUseLocalControls())
            {
                promptView?.Hide();
                return;
            }

            if (scanner != null && scanner.TryGetCandidate(out InteractionCandidate candidate)) promptView.SetCandidate(candidate);
            else promptView.Hide();
        }

        private void ResolveReferences()
        {
            if (inputReader == null) inputReader = GetComponent<ThirdPersonInputReader>();
            if (scanner == null)
            {
                scanner = GetComponent<InteractionScanner>();
                if (scanner == null) scanner = gameObject.AddComponent<InteractionScanner>();
            }
            if (interactionBridge == null) interactionBridge = GetComponent<NetworkInteractionCommandBridge>();
            if (primaryActionBridge == null) primaryActionBridge = GetComponent<NetworkPrimaryActionCommandBridge>();
            if (buyerPurchaseBridge == null) buyerPurchaseBridge = GetComponent<NetworkBuyerPurchaseCommandBridge>();
            if (promptView == null) promptView = InteractionPromptView.GetOrCreate();
            if (feedbackPresenter == null) feedbackPresenter = ActionFeedbackPresenter.GetOrCreate();
        }

        private bool CanUseLocalControls()
        {
            if (interactionBridge != null) return interactionBridge.IsOwner;
            if (primaryActionBridge != null) return primaryActionBridge.IsOwner;
            return true;
        }

        private void SubmitInteraction()
        {
            if (!CanUseLocalControls() || scanner == null || !scanner.TryGetCandidate(out InteractionCandidate candidate)) return;
            if (candidate.Kind == InteractionKind.Buyer)
            {
                BuyerStockView.GetOrCreate().Open(buyerPurchaseBridge, candidate.DisplayName, candidate.TargetNetworkId);
                feedbackPresenter.Show("Buyer stock opened");
                return;
            }

            if (interactionBridge == null)
            {
                feedbackPresenter.Show("Interaction bridge missing");
                return;
            }

            interactionBridge.SubmitCandidate(candidate);
            feedbackPresenter.Show("Submitted " + candidate.InteractionType);
        }

        private void SubmitPrimaryAction()
        {
            if (!CanUseLocalControls() || primaryActionBridge == null) return;
            Camera aimCamera = Camera.main;
            Vector3 origin = aimCamera == null ? transform.position + Vector3.up : aimCamera.transform.position;
            Vector3 direction = aimCamera == null ? transform.forward : aimCamera.transform.forward;
            primaryActionBridge.SubmitPrimaryAction(inputReader == null ? 0 : inputReader.HotbarSelectionIndex, origin, direction, Time.frameCount);
            feedbackPresenter.Show("Submitted attack");
        }
    }
}