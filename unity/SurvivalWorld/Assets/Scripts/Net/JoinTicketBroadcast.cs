using FishNet.Broadcast;

namespace SurvivalWorld.Net
{
    public struct JoinTicketBroadcast : IBroadcast
    {
        public string Ticket;
    }
}
