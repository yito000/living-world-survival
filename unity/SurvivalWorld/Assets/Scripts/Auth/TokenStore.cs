namespace SurvivalWorld.Auth
{
    public interface ITokenStore
    {
        SessionTokenPair Current { get; }
        void Set(SessionTokenPair tokens);
        void Clear();
    }

    public sealed class InMemoryTokenStore : ITokenStore
    {
        public SessionTokenPair Current { get; private set; }

        public void Set(SessionTokenPair tokens)
        {
            Current = tokens;
        }

        public void Clear()
        {
            Current = null;
        }
    }
}
