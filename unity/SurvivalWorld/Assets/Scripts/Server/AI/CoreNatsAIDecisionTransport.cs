using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Google.Protobuf;
using Survival.V1;
using UnityEngine;

namespace SurvivalWorld.Server.AI
{
    public sealed class CoreNatsAIDecisionTransport : IAIDecisionTransport
    {
        private const string RequestSubject = "ai.decision.request";
        private const int ReadLimit = 1024 * 1024;

        private readonly NatsEndpoint endpoint;
        private readonly object sync = new object();
        private TcpClient client;
        private NetworkStream stream;
        private Thread readerThread;
        private Action<ActionDecision> onDecision;
        private string resultSubject = string.Empty;
        private bool running;
        private bool subscribed;
        private bool warningLogged;

        public CoreNatsAIDecisionTransport(string natsUrl)
        {
            endpoint = NatsEndpoint.Parse(natsUrl);
        }

        public void PublishDecisionRequest(string serverId, DecisionRequest request)
        {
            if (request == null || !EnsureConnected())
            {
                return;
            }

            string json = AIDecisionJsonCodec.FormatDecisionRequest(serverId, request);
            byte[] payload = Encoding.UTF8.GetBytes(json);
            try
            {
                lock (sync)
                {
                    if (stream == null)
                    {
                        return;
                    }

                    WriteAsciiNoLock("PUB " + RequestSubject + " " + payload.Length.ToString(CultureInfo.InvariantCulture) + "\r\n");
                    stream.Write(payload, 0, payload.Length);
                    WriteAsciiNoLock("\r\n");
                    stream.Flush();
                }

                long stateVersion = request.StateVersions.ContainsKey("personal_state") ? request.StateVersions["personal_state"] : 0L;
                Debug.Log("AI decision request published: actor=" + request.ActorId + ", state_version=" + stateVersion + ", reason=" + request.Reason);
            }
            catch (Exception ex)
            {
                CloseConnection();
                Debug.LogWarning("AI NATS publish failed: " + ex.Message);
            }
        }

        public void SubscribeDecisionResults(string serverId, Action<ActionDecision> onDecision)
        {
            lock (sync)
            {
                this.onDecision = onDecision;
                resultSubject = "ai.decision.result." + SanitizeSubjectToken(serverId);
                subscribed = false;
            }

            if (EnsureConnected())
            {
                lock (sync)
                {
                    SubscribeNoLock();
                }
            }
        }

        public void Dispose()
        {
            CloseConnection();
        }

        private bool EnsureConnected()
        {
            lock (sync)
            {
                if (stream != null && client != null && client.Connected)
                {
                    return true;
                }

                CloseConnectionNoLock();
                try
                {
                    client = new TcpClient();
                    client.NoDelay = true;
                    client.Connect(endpoint.Host, endpoint.Port);
                    stream = client.GetStream();
                    running = true;
                    subscribed = false;
                    readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "Survival AI NATS" };
                    readerThread.Start();

                    WriteAsciiNoLock("CONNECT {\"verbose\":false,\"pedantic\":false,\"lang\":\"csharp\",\"version\":\"m4\",\"name\":\"survival-ds-ai\"}\r\nPING\r\n");
                    if (!string.IsNullOrWhiteSpace(resultSubject))
                    {
                        SubscribeNoLock();
                    }

                    warningLogged = false;
                    Debug.Log("AI NATS connected: " + endpoint.Display);
                    return true;
                }
                catch (Exception ex)
                {
                    CloseConnectionNoLock();
                    if (!warningLogged)
                    {
                        Debug.LogWarning("AI NATS connection unavailable at " + endpoint.Display + ": " + ex.Message);
                        warningLogged = true;
                    }

                    return false;
                }
            }
        }

        private void SubscribeNoLock()
        {
            if (stream == null || subscribed || string.IsNullOrWhiteSpace(resultSubject))
            {
                return;
            }

            WriteAsciiNoLock("SUB " + resultSubject + " 1\r\nPING\r\n");
            subscribed = true;
            Debug.Log("AI NATS subscribed: " + resultSubject);
        }
        private void ReadLoop()
        {
            try
            {
                while (running)
                {
                    NetworkStream current;
                    lock (sync)
                    {
                        current = stream;
                    }

                    if (current == null)
                    {
                        return;
                    }

                    string line = ReadLine(current);
                    if (line == null)
                    {
                        return;
                    }

                    if (line.Length == 0 || line.StartsWith("INFO", StringComparison.Ordinal) || line.StartsWith("+OK", StringComparison.Ordinal) || line.StartsWith("PONG", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (line.StartsWith("PING", StringComparison.Ordinal))
                    {
                        WriteAscii("PONG\r\n");
                    }
                    else if (line.StartsWith("MSG ", StringComparison.Ordinal))
                    {
                        HandleMessage(current, line);
                    }
                    else if (line.StartsWith("-ERR", StringComparison.Ordinal))
                    {
                        Debug.LogWarning("AI NATS server error: " + line);
                    }
                }
            }
            catch (Exception ex)
            {
                if (running)
                {
                    Debug.LogWarning("AI NATS read loop stopped: " + ex.Message);
                }
            }
            finally
            {
                CloseConnection();
            }
        }

        private void HandleMessage(NetworkStream current, string line)
        {
            if (!TryParseMessageSize(line, out int size) || size < 0 || size > ReadLimit)
            {
                Debug.LogWarning("AI NATS message header rejected: " + line);
                return;
            }

            byte[] payload = ReadExact(current, size);
            ReadCrlf(current);
            string json = Encoding.UTF8.GetString(payload);
            if (!AIDecisionJsonCodec.TryParseDecision(json, out ActionDecision decision, out string error))
            {
                Debug.LogWarning("AI decision result parse failed: " + error);
                return;
            }

            Debug.Log("AI decision result received: actor=" + decision.ActorId + ", template=" + decision.TemplateId + ", decision_id=" + decision.DecisionId);
            Action<ActionDecision> callback;
            lock (sync)
            {
                callback = onDecision;
            }

            callback?.Invoke(decision);
        }

        private static bool TryParseMessageSize(string line, out int size)
        {
            size = 0;
            string[] parts = line.Split(' ');
            return parts.Length >= 4 && int.TryParse(parts[parts.Length - 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
        }

        private static string ReadLine(NetworkStream source)
        {
            var bytes = new List<byte>(128);
            while (true)
            {
                int value = source.ReadByte();
                if (value < 0)
                {
                    return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
                }

                if (value == '\n')
                {
                    if (bytes.Count > 0 && bytes[bytes.Count - 1] == '\r')
                    {
                        bytes.RemoveAt(bytes.Count - 1);
                    }

                    return Encoding.ASCII.GetString(bytes.ToArray());
                }

                bytes.Add((byte)value);
                if (bytes.Count > ReadLimit)
                {
                    throw new InvalidOperationException("NATS line exceeded read limit.");
                }
            }
        }

        private static byte[] ReadExact(NetworkStream source, int size)
        {
            byte[] payload = new byte[size];
            int offset = 0;
            while (offset < size)
            {
                int read = source.Read(payload, offset, size - offset);
                if (read <= 0)
                {
                    throw new InvalidOperationException("NATS payload ended before expected size.");
                }

                offset += read;
            }

            return payload;
        }

        private static void ReadCrlf(NetworkStream source)
        {
            int cr = source.ReadByte();
            int lf = source.ReadByte();
            if (cr != '\r' || lf != '\n')
            {
                throw new InvalidOperationException("NATS payload was not followed by CRLF.");
            }
        }

        private void WriteAscii(string value)
        {
            lock (sync)
            {
                WriteAsciiNoLock(value);
                stream?.Flush();
            }
        }

        private void WriteAsciiNoLock(string value)
        {
            if (stream == null)
            {
                return;
            }

            byte[] bytes = Encoding.ASCII.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
        }

        private void CloseConnection()
        {
            lock (sync)
            {
                CloseConnectionNoLock();
            }
        }

        private void CloseConnectionNoLock()
        {
            running = false;
            subscribed = false;
            try { stream?.Dispose(); } catch (ObjectDisposedException) { }
            try { client?.Close(); } catch (ObjectDisposedException) { }
            stream = null;
            client = null;
        }

        private static string SanitizeSubjectToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unknown";
            }

            var builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                builder.Append(char.IsWhiteSpace(c) || c == '.' || c == '*' || c == '>' ? '_' : c);
            }

            return builder.ToString();
        }
    }
    public static class AIDecisionJsonCodec
    {
        public static string FormatDecisionRequest(string serverId, DecisionRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var builder = new StringBuilder(256);
            builder.Append('{');
            AppendString(builder, "actor_id", request.ActorId).Append(',');
            AppendString(builder, "world_id", request.WorldId).Append(',');
            AppendString(builder, "server_id", serverId).Append(',');
            builder.Append("\"state_versions\":{");
            bool first = true;
            foreach (KeyValuePair<string, long> pair in request.StateVersions)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                AppendStringValue(builder, pair.Key).Append(':').Append(pair.Value.ToString(CultureInfo.InvariantCulture));
                first = false;
            }

            builder.Append("},");
            AppendString(builder, "reason", request.Reason);
            builder.Append('}');
            return builder.ToString();
        }

        public static bool TryParseDecision(string json, out ActionDecision decision, out string error)
        {
            decision = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "empty payload";
                return false;
            }

            try
            {
                decision = JsonParser.Default.Parse<ActionDecision>(json);
                if (decision != null)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            try
            {
                ActionDecisionJson dto = JsonUtility.FromJson<ActionDecisionJson>(json);
                if (dto == null)
                {
                    error = string.IsNullOrWhiteSpace(error) ? "payload was not an ActionDecision" : error;
                    return false;
                }

                decision = new ActionDecision
                {
                    DecisionId = dto.decision_id ?? string.Empty,
                    ActorId = dto.actor_id ?? string.Empty,
                    StateVersion = dto.state_version,
                    TemplateId = dto.template_id ?? string.Empty,
                    CreatedAtUnixMs = dto.created_at_unix_ms
                };

                if (dto.steps != null)
                {
                    for (int i = 0; i < dto.steps.Length; i++)
                    {
                        if (dto.steps[i] != null && !string.IsNullOrWhiteSpace(dto.steps[i].action_template_id))
                        {
                            decision.Steps.Add(new ActionStep { ActionTemplateId = dto.steps[i].action_template_id });
                        }
                    }
                }

                bool parsed = !string.IsNullOrWhiteSpace(decision.DecisionId) || !string.IsNullOrWhiteSpace(decision.ActorId);
                if (!parsed && string.IsNullOrWhiteSpace(error))
                {
                    error = "missing decision_id and actor_id";
                }

                return parsed;
            }
            catch (Exception ex)
            {
                error = string.IsNullOrWhiteSpace(error) ? ex.Message : error + "; fallback: " + ex.Message;
                return false;
            }
        }

        private static StringBuilder AppendString(StringBuilder builder, string name, string value)
        {
            AppendStringValue(builder, name).Append(':');
            AppendStringValue(builder, value);
            return builder;
        }

        private static StringBuilder AppendStringValue(StringBuilder builder, string value)
        {
            builder.Append('"');
            string text = value ?? string.Empty;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            builder.Append('"');
            return builder;
        }

        [Serializable]
        private sealed class ActionDecisionJson
        {
            public string decision_id;
            public string actor_id;
            public long state_version;
            public string template_id;
            public ActionStepJson[] steps;
            public long created_at_unix_ms;
        }

        [Serializable]
        private sealed class ActionStepJson
        {
            public string action_template_id;
        }
    }

    internal readonly struct NatsEndpoint
    {
        private NatsEndpoint(string host, int port)
        {
            Host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;
            Port = port <= 0 ? 4222 : port;
        }

        public string Host { get; }
        public int Port { get; }
        public string Display => "nats://" + Host + ":" + Port.ToString(CultureInfo.InvariantCulture);

        public static NatsEndpoint Parse(string value)
        {
            string text = string.IsNullOrWhiteSpace(value) ? "nats://127.0.0.1:4222" : value.Trim();
            if (!text.Contains("://"))
            {
                text = "nats://" + text;
            }

            if (Uri.TryCreate(text, UriKind.Absolute, out Uri uri))
            {
                return new NatsEndpoint(uri.Host, uri.Port <= 0 ? 4222 : uri.Port);
            }

            return new NatsEndpoint("127.0.0.1", 4222);
        }
    }
}