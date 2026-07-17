using R3;
using StarterAssets;
using Survival.V1;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SurvivalWorld.Player
{
    public sealed class ThirdPersonInputReader : MonoBehaviour
    {
        [SerializeField] private InputActionAsset actionAsset;
        [SerializeField] private string actionMapName = "Gameplay";
        [SerializeField] private string moveActionName = "Move";
        [SerializeField] private string lookActionName = "Look";
        [SerializeField] private string jumpActionName = "Jump";
        [SerializeField] private string sprintActionName = "Sprint";
        [SerializeField] private InputActionReference moveAction;
        [SerializeField] private InputActionReference lookAction;
        [SerializeField] private InputActionReference jumpAction;
        [SerializeField] private InputActionReference sprintAction;
        [SerializeField] private StarterAssetsInputs starterAssetsInputs;
        [SerializeField] private bool preferStarterAssetsInputs = true;

        private readonly Subject<InputCommand> commands = new Subject<InputCommand>();
        private InputAction resolvedMoveAction;
        private InputAction resolvedLookAction;
        private InputAction resolvedJumpAction;
        private InputAction resolvedSprintAction;
        private long sequence;
        private bool previousStarterJump;

        public Observable<InputCommand> Commands => commands;

        private void Awake()
        {
            ResolveStarterAssetsInputs();
            ResolveActions();
        }

        private void OnEnable()
        {
            ResolveStarterAssetsInputs();
            ResolveActions();
            Enable(MoveAction);
            Enable(LookAction);
            Enable(JumpAction);
            Enable(SprintAction);
        }

        private void OnDisable()
        {
            previousStarterJump = false;
            Disable(MoveAction);
            Disable(LookAction);
            Disable(JumpAction);
            Disable(SprintAction);
        }

        private void OnDestroy()
        {
            commands.Dispose();
        }

        public void ResetSequence()
        {
            sequence = 0;
            previousStarterJump = false;
        }

        public InputCommand ReadCurrentCommand(long tick, float cameraYawDegrees)
        {
            if (TryReadStarterAssetsInput(out Vector2 starterMove, out Vector2 starterLook, out bool starterJump, out bool starterSprint))
            {
                return BuildCommand(starterMove, starterLook, starterJump, starterSprint, cameraYawDegrees, tick);
            }

            InputAction move = MoveAction;
            InputAction look = LookAction;
            InputAction jump = JumpAction;
            InputAction sprint = SprintAction;

            Vector2 moveValue = move == null ? Vector2.zero : move.ReadValue<Vector2>();
            Vector2 lookValue = look == null ? Vector2.zero : look.ReadValue<Vector2>();
            bool jumpValue = jump != null && jump.WasPressedThisFrame();
            bool sprintValue = sprint != null && sprint.IsPressed();
            return BuildCommand(moveValue, lookValue, jumpValue, sprintValue, cameraYawDegrees, tick);
        }

        public InputCommand BuildCommand(Vector2 move, Vector2 look, bool jump, bool sprint, float cameraYawDegrees, long tick)
        {
            Vector3 worldMove = ToCameraRelativeMove(move, cameraYawDegrees);
            var command = new InputCommand
            {
                Tick = tick,
                Sequence = ++sequence,
                Move = new Vec3 { X = worldMove.x, Y = worldMove.y, Z = worldMove.z },
                Look = new Vec3 { X = look.x, Y = look.y, Z = 0f },
                Jump = jump,
                Sprint = sprint
            };

            commands.OnNext(command);
            return command;
        }

        public static Vector3 ToCameraRelativeMove(Vector2 move, float cameraYawDegrees)
        {
            Vector2 clamped = Vector2.ClampMagnitude(move, 1f);
            Vector3 local = new Vector3(clamped.x, 0f, clamped.y);
            return Quaternion.Euler(0f, cameraYawDegrees, 0f) * local;
        }

        private InputAction MoveAction => moveAction != null && moveAction.action != null ? moveAction.action : resolvedMoveAction;
        private InputAction LookAction => lookAction != null && lookAction.action != null ? lookAction.action : resolvedLookAction;
        private InputAction JumpAction => jumpAction != null && jumpAction.action != null ? jumpAction.action : resolvedJumpAction;
        private InputAction SprintAction => sprintAction != null && sprintAction.action != null ? sprintAction.action : resolvedSprintAction;

        private bool TryReadStarterAssetsInput(out Vector2 move, out Vector2 look, out bool jump, out bool sprint)
        {
            move = Vector2.zero;
            look = Vector2.zero;
            jump = false;
            sprint = false;

            if (!preferStarterAssetsInputs || starterAssetsInputs == null)
            {
                return false;
            }

            move = starterAssetsInputs.move;
            look = starterAssetsInputs.look;
            jump = starterAssetsInputs.jump && !previousStarterJump;
            sprint = starterAssetsInputs.sprint;
            previousStarterJump = starterAssetsInputs.jump;
            return true;
        }

        private void ResolveStarterAssetsInputs()
        {
            if (starterAssetsInputs == null)
            {
                starterAssetsInputs = GetComponent<StarterAssetsInputs>();
            }
        }

        private void ResolveActions()
        {
            if (actionAsset == null && TryGetComponent(out PlayerInput playerInput))
            {
                actionAsset = playerInput.actions;
            }

            if (actionAsset == null)
            {
                return;
            }

            InputActionMap map = actionAsset.FindActionMap(actionMapName, throwIfNotFound: false);
            if (map == null)
            {
                return;
            }

            resolvedMoveAction = FindAction(map, moveActionName);
            resolvedLookAction = FindAction(map, lookActionName);
            resolvedJumpAction = FindAction(map, jumpActionName);
            resolvedSprintAction = FindAction(map, sprintActionName);
        }

        private static InputAction FindAction(InputActionMap map, string actionName)
        {
            return string.IsNullOrWhiteSpace(actionName) ? null : map.FindAction(actionName, throwIfNotFound: false);
        }

        private static void Enable(InputAction action)
        {
            if (action != null)
            {
                action.Enable();
            }
        }

        private static void Disable(InputAction action)
        {
            if (action != null)
            {
                action.Disable();
            }
        }
    }
}
