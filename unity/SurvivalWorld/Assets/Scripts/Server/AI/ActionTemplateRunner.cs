using System;
using System.Collections.Generic;
using SurvivalWorld.Inventory;

namespace SurvivalWorld.Server.AI
{
    public sealed class ActionTemplateRunner
    {
        private readonly List<Action> compensations = new List<Action>();
        private ActionTemplateDefinition activeTemplate;
        private int stepIndex;
        private int retryCount;
        private long startedAtUnixMs;
        private RunnerState state = RunnerState.Idle;

        public RunnerState State => state;
        public ActionTemplateDefinition ActiveTemplate => activeTemplate;
        public int StepIndex => stepIndex;
        public bool HasActiveTemplate => activeTemplate != null && state == RunnerState.Running;

        public ActionTemplateStartResult Start(ActionTemplateDefinition template, PrimitiveActionRegistry registry, AIPreconditionContext preconditionContext, long unixTimeMs)
        {
            if (template == null)
            {
                return ActionTemplateStartResult.Rejected("Template is required.");
            }

            if (!template.ValidatePrimitives(registry, out string missingPrimitive))
            {
                return ActionTemplateStartResult.Rejected("Missing primitive: " + missingPrimitive);
            }

            if (!template.PreconditionsMet(preconditionContext, out string failedExpression))
            {
                return ActionTemplateStartResult.Rejected("Precondition failed: " + failedExpression);
            }

            Cancel("switch", unixTimeMs);
            activeTemplate = template;
            stepIndex = 0;
            retryCount = 0;
            startedAtUnixMs = unixTimeMs;
            state = RunnerState.Running;
            return ActionTemplateStartResult.Started(template.TemplateId);
        }

        public bool CanSwitchTemplate(long unixTimeMs)
        {
            if (activeTemplate == null || state != RunnerState.Running)
            {
                return true;
            }

            long elapsedMs = Math.Max(0L, unixTimeMs - startedAtUnixMs);
            return elapsedMs >= activeTemplate.MinDurationSeconds * 1000L;
        }

        public ActionTemplateTickResult Tick(PrimitiveActionRegistry registry, PrimitiveActionContext context)
        {
            if (activeTemplate == null || state != RunnerState.Running)
            {
                return ActionTemplateTickResult.Idle();
            }

            long elapsedMs = Math.Max(0L, context.UnixTimeMs - startedAtUnixMs);
            if (elapsedMs > activeTemplate.MaxDurationSeconds * 1000L)
            {
                Cancel("timeout", context.UnixTimeMs);
                return ActionTemplateTickResult.Interrupted("timeout");
            }

            string interrupt = FindActiveInterrupt(context.ActiveInterrupts);
            if (!string.IsNullOrWhiteSpace(interrupt))
            {
                Cancel(interrupt, context.UnixTimeMs);
                return ActionTemplateTickResult.Interrupted(interrupt);
            }

            if (!activeTemplate.PreconditionsMet(CreatePreconditionContext(context), out string failedExpression))
            {
                Cancel("precondition", context.UnixTimeMs);
                return ActionTemplateTickResult.Failed("Precondition failed: " + failedExpression);
            }

            if (stepIndex >= activeTemplate.Steps.Count)
            {
                Complete();
                return ActionTemplateTickResult.Completed();
            }

            ActionStepSpec step = activeTemplate.Steps[stepIndex];
            PrimitiveActionResult result = registry.Execute(step, context);
            AppendCompensations(context.Compensations);

            if (result.Status == PrimitiveActionStatus.Running)
            {
                return ActionTemplateTickResult.Running(activeTemplate.TemplateId, stepIndex);
            }

            if (result.Status == PrimitiveActionStatus.Completed)
            {
                stepIndex++;
                retryCount = 0;
                if (stepIndex >= activeTemplate.Steps.Count)
                {
                    Complete();
                    return ActionTemplateTickResult.Completed();
                }

                return ActionTemplateTickResult.Running(activeTemplate.TemplateId, stepIndex);
            }

            if (result.Retryable && retryCount < activeTemplate.MaxRetries)
            {
                retryCount++;
                return ActionTemplateTickResult.Retrying(result.Error, retryCount);
            }

            Cancel("failed", context.UnixTimeMs);
            return ActionTemplateTickResult.Failed(result.Error);
        }

        public void Cancel(string reason, long unixTimeMs)
        {
            if (activeTemplate == null)
            {
                state = RunnerState.Idle;
                return;
            }

            for (int i = compensations.Count - 1; i >= 0; i--)
            {
                try
                {
                    compensations[i]();
                }
                catch (Exception)
                {
                    // Compensation must not prevent release of later reservations.
                }
            }

            compensations.Clear();
            activeTemplate = null;
            stepIndex = 0;
            retryCount = 0;
            startedAtUnixMs = 0L;
            state = string.Equals(reason, "complete", StringComparison.Ordinal) ? RunnerState.Completed : RunnerState.Cancelled;
        }

        private void Complete()
        {
            activeTemplate = null;
            compensations.Clear();
            stepIndex = 0;
            retryCount = 0;
            startedAtUnixMs = 0L;
            state = RunnerState.Completed;
        }

        private string FindActiveInterrupt(ISet<string> activeInterrupts)
        {
            if (activeInterrupts == null || activeInterrupts.Count == 0)
            {
                return string.Empty;
            }

            for (int i = 0; i < activeTemplate.Interrupts.Count; i++)
            {
                string interrupt = activeTemplate.Interrupts[i];
                if (activeInterrupts.Contains(interrupt))
                {
                    return interrupt;
                }
            }

            return string.Empty;
        }

        private static AIPreconditionContext CreatePreconditionContext(PrimitiveActionContext context)
        {
            AIInventorySummary summary = context.Inventory == null
                ? new AIInventorySummary(
                    context.PersonalState == null ? 0 : context.PersonalState.UsedSlots,
                    context.PersonalState == null ? 0 : Math.Max(0, context.PersonalState.CapacitySlots - context.PersonalState.UsedSlots),
                    context.PersonalState == null ? 0 : context.PersonalState.CapacitySlots,
                    context.PersonalState == null ? 0 : context.PersonalState.SellableCount,
                    context.PersonalState == null ? 0L : context.PersonalState.NetWorth)
                : context.Inventory.GetSummary();
            return new AIPreconditionContext(context.PersonalState, summary);
        }

        private void AppendCompensations(IReadOnlyList<Action> newCompensations)
        {
            if (newCompensations == null)
            {
                return;
            }

            for (int i = 0; i < newCompensations.Count; i++)
            {
                compensations.Add(newCompensations[i]);
            }
        }
    }

    public enum RunnerState
    {
        Idle = 0,
        Running = 1,
        Completed = 2,
        Cancelled = 3
    }

    public readonly struct ActionTemplateStartResult
    {
        private ActionTemplateStartResult(bool success, string templateId, string error)
        {
            Success = success;
            TemplateId = templateId ?? string.Empty;
            Error = error ?? string.Empty;
        }

        public bool Success { get; }
        public string TemplateId { get; }
        public string Error { get; }

        public static ActionTemplateStartResult Started(string templateId)
        {
            return new ActionTemplateStartResult(true, templateId, string.Empty);
        }

        public static ActionTemplateStartResult Rejected(string error)
        {
            return new ActionTemplateStartResult(false, string.Empty, error);
        }
    }

    public readonly struct ActionTemplateTickResult
    {
        private ActionTemplateTickResult(RunnerTickStatus status, string templateId, int stepIndex, string error, int retryCount)
        {
            Status = status;
            TemplateId = templateId ?? string.Empty;
            StepIndex = stepIndex;
            Error = error ?? string.Empty;
            RetryCount = retryCount;
        }

        public RunnerTickStatus Status { get; }
        public string TemplateId { get; }
        public int StepIndex { get; }
        public string Error { get; }
        public int RetryCount { get; }

        public static ActionTemplateTickResult Idle()
        {
            return new ActionTemplateTickResult(RunnerTickStatus.Idle, string.Empty, 0, string.Empty, 0);
        }

        public static ActionTemplateTickResult Running(string templateId, int stepIndex)
        {
            return new ActionTemplateTickResult(RunnerTickStatus.Running, templateId, stepIndex, string.Empty, 0);
        }

        public static ActionTemplateTickResult Retrying(string error, int retryCount)
        {
            return new ActionTemplateTickResult(RunnerTickStatus.Retrying, string.Empty, 0, error, retryCount);
        }

        public static ActionTemplateTickResult Completed()
        {
            return new ActionTemplateTickResult(RunnerTickStatus.Completed, string.Empty, 0, string.Empty, 0);
        }

        public static ActionTemplateTickResult Interrupted(string reason)
        {
            return new ActionTemplateTickResult(RunnerTickStatus.Interrupted, string.Empty, 0, reason, 0);
        }

        public static ActionTemplateTickResult Failed(string error)
        {
            return new ActionTemplateTickResult(RunnerTickStatus.Failed, string.Empty, 0, error, 0);
        }
    }

    public enum RunnerTickStatus
    {
        Idle = 0,
        Running = 1,
        Retrying = 2,
        Completed = 3,
        Interrupted = 4,
        Failed = 5
    }
}
