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
        [SerializeField] private string interactActionName = "Interact";
        [SerializeField] private string primaryActionName = "PrimaryAction";
        [SerializeField] private string alternatePrimaryActionName = "Attack";
        [SerializeField] private string previousActionName = "Previous";
        [SerializeField] private string nextActionName = "Next";
        [SerializeField] private InputActionReference moveAction;
        [SerializeField] private InputActionReference lookAction;
        [SerializeField] private InputActionReference jumpAction;
        [SerializeField] private InputActionReference sprintAction;
        [SerializeField] private InputActionReference interactAction;
        [SerializeField] private InputActionReference primaryAction;
        [SerializeField] private InputActionReference previousAction;
        [SerializeField] private InputActionReference nextAction;
        [SerializeField] private StarterAssetsInputs starterAssetsInputs;
        [SerializeField] private bool preferStarterAssetsInputs = true;
        [SerializeField] private bool enableDirectInputFallback = true;

        private readonly Subject<InputCommand> commands = new Subject<InputCommand>();
        private readonly Subject<Unit> interactPressed = new Subject<Unit>();
        private readonly Subject<Unit> primaryActionPressed = new Subject<Unit>();
        private readonly Subject<int> hotbarSelectionChanged = new Subject<int>();
        private InputAction resolvedMoveAction;
        private InputAction resolvedLookAction;
        private InputAction resolvedJumpAction;
        private InputAction resolvedSprintAction;
        private InputAction resolvedInteractAction;
        private InputAction resolvedPrimaryAction;
        private InputAction resolvedPreviousAction;
        private InputAction resolvedNextAction;
        private long sequence;
        private int hotbarSelectionIndex;
        private bool previousStarterJump;

        public Observable<InputCommand> Commands => commands;
        public Observable<Unit> InteractPressed => interactPressed;
        public Observable<Unit> PrimaryActionPressed => primaryActionPressed;
        public Observable<int> HotbarSelectionChanged => hotbarSelectionChanged;
        public int HotbarSelectionIndex => hotbarSelectionIndex;

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
            Enable(InteractAction);
            Enable(PrimaryAction);
            Enable(PreviousAction);
            Enable(NextAction);
        }

        private void OnDisable()
        {
            previousStarterJump = false;
            Disable(MoveAction);
            Disable(LookAction);
            Disable(JumpAction);
            Disable(SprintAction);
            Disable(InteractAction);
            Disable(PrimaryAction);
            Disable(PreviousAction);
            Disable(NextAction);
        }

        private void OnDestroy()
        {
            commands.Dispose();
            interactPressed.Dispose();
            primaryActionPressed.Dispose();
            hotbarSelectionChanged.Dispose();
        }

        public void ResetSequence()
        {
            sequence = 0;
            previousStarterJump = false;
            hotbarSelectionIndex = 0;
        }

        public InputCommand ReadCurrentCommand(long tick, float cameraYawDegrees)
        {
            PublishMomentaryInputs();

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
        private InputAction InteractAction => interactAction != null && interactAction.action != null ? interactAction.action : resolvedInteractAction;
        private InputAction PrimaryAction => primaryAction != null && primaryAction.action != null ? primaryAction.action : resolvedPrimaryAction;
        private InputAction PreviousAction => previousAction != null && previousAction.action != null ? previousAction.action : resolvedPreviousAction;
        private InputAction NextAction => nextAction != null && nextAction.action != null ? nextAction.action : resolvedNextAction;

        private void PublishMomentaryInputs()
        {
            InputAction interact = InteractAction;
            if (WasPressedThisFrame(interact) || (interact == null && WasDirectInteractPressed()))
            {
                interactPressed.OnNext(default);
            }

            InputAction primary = PrimaryAction;
            if (WasPressedThisFrame(primary) || (primary == null && WasDirectPrimaryActionPressed()))
            {
                primaryActionPressed.OnNext(default);
            }

            int nextHotbar = hotbarSelectionIndex;
            InputAction previous = PreviousAction;
            InputAction next = NextAction;
            if (WasPressedThisFrame(previous) || (previous == null && WasDirectPreviousPressed()))
            {
                nextHotbar = Mathf.Max(0, nextHotbar - 1);
            }

            if (WasPressedThisFrame(next) || (next == null && WasDirectNextPressed()))
            {
                nextHotbar = Mathf.Min(1, nextHotbar + 1);
            }

            Keyboard keyboard = Keyboard.current;
            if (enableDirectInputFallback && keyboard != null)
            {
                if (keyboard.digit1Key.wasPressedThisFrame)
                {
                    nextHotbar = 0;
                }
                else if (keyboard.digit2Key.wasPressedThisFrame)
                {
                    nextHotbar = 1;
                }
            }

            if (nextHotbar != hotbarSelectionIndex)
            {
                hotbarSelectionIndex = nextHotbar;
                hotbarSelectionChanged.OnNext(hotbarSelectionIndex);
            }
        }

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
            string playerInputDefaultActionMap = string.Empty;
            if (actionAsset == null && TryGetComponent(out PlayerInput playerInput))
            {
                actionAsset = playerInput.actions;
                playerInputDefaultActionMap = playerInput.defaultActionMap;
            }

            if (actionAsset == null)
            {
                return;
            }

            InputActionMap map = ResolveActionMap(actionAsset, actionMapName, playerInputDefaultActionMap);
            if (map == null)
            {
                return;
            }

            resolvedMoveAction = FindAction(map, moveActionName);
            resolvedLookAction = FindAction(map, lookActionName);
            resolvedJumpAction = FindAction(map, jumpActionName);
            resolvedSprintAction = FindAction(map, sprintActionName);
            resolvedInteractAction = FindAction(map, interactActionName);
            resolvedPrimaryAction = FindAction(map, primaryActionName, alternatePrimaryActionName);
            resolvedPreviousAction = FindAction(map, previousActionName);
            resolvedNextAction = FindAction(map, nextActionName);
        }

        private static InputAction FindAction(InputActionMap map, string actionName)
        {
            return string.IsNullOrWhiteSpace(actionName) ? null : map.FindAction(actionName, throwIfNotFound: false);
        }

        private static InputAction FindAction(InputActionMap map, string actionName, string fallbackActionName)
        {
            InputAction action = FindAction(map, actionName);
            return action ?? FindAction(map, fallbackActionName);
        }

        private static InputActionMap ResolveActionMap(InputActionAsset asset, string preferredName, string playerInputDefaultActionMap)
        {
            InputActionMap map = FindActionMap(asset, preferredName);
            if (map != null)
            {
                return map;
            }

            map = FindActionMap(asset, playerInputDefaultActionMap);
            if (map != null)
            {
                return map;
            }

            map = FindActionMap(asset, "Player");
            if (map != null)
            {
                return map;
            }

            map = FindActionMap(asset, "Gameplay");
            if (map != null)
            {
                return map;
            }

            return asset.actionMaps.Count > 0 ? asset.actionMaps[0] : null;
        }

        private static InputActionMap FindActionMap(InputActionAsset asset, string mapName)
        {
            return asset == null || string.IsNullOrWhiteSpace(mapName)
                ? null
                : asset.FindActionMap(mapName, throwIfNotFound: false);
        }

        private static bool WasPressedThisFrame(InputAction action)
        {
            return action != null && action.WasPressedThisFrame();
        }

        private bool WasDirectInteractPressed()
        {
            if (!enableDirectInputFallback)
            {
                return false;
            }

            Keyboard keyboard = Keyboard.current;
            Gamepad gamepad = Gamepad.current;
            return (keyboard != null && keyboard.eKey.wasPressedThisFrame) ||
                   (gamepad != null && gamepad.buttonWest.wasPressedThisFrame);
        }

        private bool WasDirectPrimaryActionPressed()
        {
            if (!enableDirectInputFallback)
            {
                return false;
            }

            Mouse mouse = Mouse.current;
            Gamepad gamepad = Gamepad.current;
            return (mouse != null && mouse.leftButton.wasPressedThisFrame) ||
                   (gamepad != null && gamepad.rightTrigger.wasPressedThisFrame);
        }

        private bool WasDirectPreviousPressed()
        {
            if (!enableDirectInputFallback)
            {
                return false;
            }

            Gamepad gamepad = Gamepad.current;
            return gamepad != null && gamepad.leftShoulder.wasPressedThisFrame;
        }

        private bool WasDirectNextPressed()
        {
            if (!enableDirectInputFallback)
            {
                return false;
            }

            Gamepad gamepad = Gamepad.current;
            return gamepad != null && gamepad.rightShoulder.wasPressedThisFrame;
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