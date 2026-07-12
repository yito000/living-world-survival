using System;

namespace SurvivalWorld.Net
{
    public readonly struct ServerEndpoint
    {
        public ServerEndpoint(string host, ushort port)
        {
            Host = host;
            Port = port;
        }

        public string Host { get; }
        public ushort Port { get; }

        public override string ToString()
        {
            return Host + ":" + Port;
        }
    }

    public static class EndpointParser
    {
        public static bool TryParse(string value, out ServerEndpoint endpoint)
        {
            endpoint = default;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Port > 0 && uri.Port <= ushort.MaxValue)
            {
                endpoint = new ServerEndpoint(uri.Host, (ushort)uri.Port);
                return true;
            }

            int separator = trimmed.LastIndexOf(':');
            if (separator <= 0 || separator == trimmed.Length - 1)
            {
                return false;
            }

            string host = trimmed.Substring(0, separator).Trim('[', ']');
            string portText = trimmed.Substring(separator + 1);
            if (string.IsNullOrWhiteSpace(host) || !ushort.TryParse(portText, out ushort port))
            {
                return false;
            }

            endpoint = new ServerEndpoint(host, port);
            return true;
        }
    }
}
