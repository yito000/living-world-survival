using Survival.V1;

namespace SurvivalWorld.Inventory
{
    public interface IInventoryEventSink
    {
        void Enqueue(DomainEvent domainEvent);
    }

    public sealed class NullInventoryEventSink : IInventoryEventSink
    {
        public static readonly NullInventoryEventSink Instance = new NullInventoryEventSink();

        private NullInventoryEventSink()
        {
        }

        public void Enqueue(DomainEvent domainEvent)
        {
        }
    }
}
