using UnityEngine;

namespace SurvivalWorld.Client.Interaction
{
    public sealed class InteractionScanner : MonoBehaviour
    {
        [SerializeField] private Camera sourceCamera;
        [SerializeField] private float range = 3f;
        [SerializeField] private LayerMask mask = ~0;

        public RaycastHit LastHit { get; private set; }
        public bool HasCandidate { get; private set; }
        public InteractionCandidate CurrentCandidate { get; private set; }
        public Camera SourceCamera { get => sourceCamera; set => sourceCamera = value; }
        public float Range { get => range; set => range = Mathf.Max(0.1f, value); }

        private void Awake()
        {
            if (sourceCamera == null) sourceCamera = Camera.main;
        }

        private void Update()
        {
            Scan();
        }

        public bool Scan()
        {
            if (sourceCamera == null)
            {
                HasCandidate = false;
                CurrentCandidate = default;
                return false;
            }

            Ray ray = sourceCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (!Physics.Raycast(ray, out RaycastHit hit, range, mask, QueryTriggerInteraction.Collide))
            {
                HasCandidate = false;
                LastHit = default;
                CurrentCandidate = default;
                return false;
            }

            LastHit = hit;
            InteractableTargetView target = hit.collider == null ? null : hit.collider.GetComponentInParent<InteractableTargetView>();
            InteractionCandidate candidate = default;
            HasCandidate = target != null && target.TryBuildCandidate(out candidate);
            CurrentCandidate = HasCandidate ? candidate : default;
            return HasCandidate;
        }

        public bool TryGetCandidate(out InteractionCandidate candidate)
        {
            if (!HasCandidate) Scan();
            candidate = CurrentCandidate;
            return HasCandidate && candidate.IsValid;
        }
    }
}