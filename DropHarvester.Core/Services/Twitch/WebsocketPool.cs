using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DropHarvester.Models.Twitch;

namespace DropHarvester.Services.Twitch;

/// <summary>A PubSub topic we want to listen on. <see cref="Key"/> is a stable app-side id.</summary>
public sealed record WebsocketTopic(string Key, string Topic);

/// <summary>
/// Sharded Twitch PubSub connection. Each shard is one websocket carrying up to 50 topics; up to
/// 8 shards give ~398 topic slots (enough for ~199 channels at 2 topics each). Handles
/// LISTEN/UNLISTEN, periodic PING, and auto-reconnect with topic re-subscription.
/// </summary>
public interface IWebsocketPool : IAsyncDisposable
{
    /// <summary>Raised for every PubSub MESSAGE: (topic, decoded message json).</summary>
    event Action<string, JsonElement>? MessageReceived;

    /// <summary>Raised when shard count / connectivity changes.</summary>
    event Action? StatusChanged;

    int ShardCount { get; }
    int TopicCount { get; }
    bool AllConnected { get; }

    /// <summary>Reconcile the live subscriptions to exactly this set (listen/unlisten the diff).</summary>
    /// <param name="topics">Full desired set of topics after reconciliation.</param>
    /// <param name="ct">Token to cancel the reconcile.</param>
    Task SetTopicsAsync(IReadOnlyCollection<WebsocketTopic> topics, CancellationToken ct = default);

    /// <summary>Stop the pool and close every shard.</summary>
    Task StopAsync();
}

/// <summary>Default <see cref="IWebsocketPool"/> that shards PubSub topics across websocket connections.</summary>
public sealed class WebsocketPool : IWebsocketPool
{
    readonly ITwitchAuth _auth;
    readonly System.Net.IWebProxy? _proxy;
    readonly List<Shard> _shards = new();
    readonly SemaphoreSlim _gate = new(1, 1);
    volatile bool _stopped;

    public event Action<string, JsonElement>? MessageReceived;
    public event Action? StatusChanged;

    /// <summary>Builds the pool, resolving an optional proxy from settings.</summary>
    /// <param name="auth">Twitch auth used for LISTEN auth tokens.</param>
    /// <param name="settings">Settings store supplying the optional proxy config.</param>
    public WebsocketPool(ITwitchAuth auth, ISettingsStore settings)
    {
        _auth = auth;
        _proxy = HttpClientBuilder.BuildProxy(settings.Settings.Proxy);
    }

    public int ShardCount => _shards.Count;
    public int TopicCount => _shards.Sum(s => s.TopicCount);
    public bool AllConnected => _shards.Count > 0 && _shards.All(s => s.IsConnected);

    /// <summary>Reconciles live subscriptions to exactly the given set, adding/removing shards as needed.</summary>
    /// <param name="topics">Full desired set of topics after reconciliation.</param>
    /// <param name="ct">Token to cancel the reconcile.</param>
    public async Task SetTopicsAsync(IReadOnlyCollection<WebsocketTopic> topics, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stopped) return;

            var desired = topics.GroupBy(t => t.Topic).Select(g => g.First()).ToDictionary(t => t.Topic);
            var current = _shards.SelectMany(s => s.Topics).ToHashSet();

            var toRemove = current.Where(t => !desired.ContainsKey(t)).ToList();
            var toAdd = desired.Keys.Where(t => !current.Contains(t)).ToList();

            foreach (var topic in toRemove)
            {
                var shard = _shards.FirstOrDefault(s => s.Topics.Contains(topic));
                if (shard is not null)
                    await shard.UnlistenAsync(topic, ct).ConfigureAwait(false);
            }

            foreach (var topic in toAdd)
            {
                var shard = _shards.FirstOrDefault(s => s.HasCapacity) ?? await AddShardAsync(ct).ConfigureAwait(false);
                await shard.ListenAsync(topic, ct).ConfigureAwait(false);
            }

            // Drop now-empty shards (keep at least one alive once created).
            for (int i = _shards.Count - 1; i >= 0 && _shards.Count > 1; i--)
            {
                if (_shards[i].TopicCount == 0)
                {
                    await _shards[i].DisposeAsync().ConfigureAwait(false);
                    _shards.RemoveAt(i);
                }
            }

            StatusChanged?.Invoke();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns a shard with spare capacity, creating and connecting a new one when under the shard cap.</summary>
    /// <param name="ct">Token to cancel the connect.</param>
    /// <returns>A connected shard with topic capacity, or the emptiest shard when at the shard cap.</returns>
    async Task<Shard> AddShardAsync(CancellationToken ct)
    {
        if (_shards.Count >= TwitchConstants.MaxWebsockets)
            return _shards.OrderBy(s => s.TopicCount).First(); // over capacity: overpack the emptiest

        var shard = new Shard(_auth, _proxy, OnShardMessage, OnShardStatus);
        _shards.Add(shard);
        await shard.EnsureConnectedAsync(ct).ConfigureAwait(false);
        return shard;
    }

    /// <summary>Forwards a shard's decoded PubSub message to subscribers.</summary>
    /// <param name="topic">Topic the message arrived on.</param>
    /// <param name="message">Decoded inner message json.</param>
    void OnShardMessage(string topic, JsonElement message) => MessageReceived?.Invoke(topic, message);
    /// <summary>Forwards a shard connectivity change to subscribers.</summary>
    void OnShardStatus() => StatusChanged?.Invoke();

    /// <summary>Marks the pool stopped and disposes every shard.</summary>
    public async Task StopAsync()
    {
        _stopped = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var shard in _shards)
                await shard.DisposeAsync().ConfigureAwait(false);
            _shards.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops the pool and disposes the reconcile gate.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    /// <summary>One websocket connection and its (<=50) topics, with its own receive + ping loops.</summary>
    sealed class Shard : IAsyncDisposable
    {
        readonly ITwitchAuth _auth;
        readonly System.Net.IWebProxy? _proxy;
        readonly Action<string, JsonElement> _onMessage;
        readonly Action _onStatus;
        readonly HashSet<string> _topics = new();
        readonly SemaphoreSlim _sendGate = new(1, 1);
        // Serializes ALL (re)connection so the receive loop, ping loop, the Twitch RECONNECT handler and
        // the orchestrator's ListenAsync can't race to open/dispose the socket underneath each other.
        readonly SemaphoreSlim _connGate = new(1, 1);
        readonly CancellationTokenSource _cts = new();

        // Backoff before a reconnect attempt. This is also the whole "connecting" window the UI shows,
        // so a genuine reconnect blips "connecting" for ~this long exactly ONCE, not on a loop.
        static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);

        ClientWebSocket? _ws;
        Task? _receiveLoop;
        Task? _pingLoop;
        volatile bool _connected;

        /// <summary>Stores the auth, proxy, and message/status callbacks for this shard.</summary>
        /// <param name="auth">Twitch auth used for LISTEN auth tokens.</param>
        /// <param name="proxy">Optional proxy for the websocket.</param>
        /// <param name="onMessage">Callback invoked with (topic, decoded message) for each PubSub MESSAGE.</param>
        /// <param name="onStatus">Callback invoked when this shard's connectivity changes.</param>
        public Shard(ITwitchAuth auth, System.Net.IWebProxy? proxy, Action<string, JsonElement> onMessage, Action onStatus)
        {
            _auth = auth;
            _proxy = proxy;
            _onMessage = onMessage;
            _onStatus = onStatus;
        }

        public IReadOnlyCollection<string> Topics => _topics;
        public int TopicCount => _topics.Count;
        public bool HasCapacity => _topics.Count < TwitchConstants.TopicsPerShard;
        public bool IsConnected => _connected;

        /// <summary>Ensures the socket is open, opening it under the connection gate when needed.</summary>
        /// <param name="ct">Token to cancel the connect.</param>
        public async Task EnsureConnectedAsync(CancellationToken ct)
        {
            // Fast path: already open. Heal a stale "connecting" flag if a prior reconnect left it false
            // even though the socket is actually up (the bug that pinned the UI at "connecting").
            if (_ws is { State: WebSocketState.Open })
            {
                if (!_connected) { _connected = true; _onStatus(); }
                return;
            }

            await _connGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_ws is { State: WebSocketState.Open })
                {
                    if (!_connected) { _connected = true; _onStatus(); }
                    return;
                }
                await OpenSocketAsync(ct).ConfigureAwait(false);
            }
            finally { _connGate.Release(); }
        }

        /// <summary>Opens a fresh socket and starts the receive/ping loops (idempotent). Caller holds
        /// <see cref="_connGate"/>.</summary>
        /// <param name="ct">Token to cancel the connect.</param>
        async Task OpenSocketAsync(CancellationToken ct)
        {
            _ws?.Dispose();
            var ws = new ClientWebSocket();
            if (_proxy is not null)
                ws.Options.Proxy = _proxy;
            await ws.ConnectAsync(new Uri(TwitchConstants.PubSubUrl), ct).ConfigureAwait(false);
            _ws = ws;
            _connected = true;
            _onStatus();

            _receiveLoop ??= Task.Run(() => ReceiveLoopAsync(_cts.Token));
            _pingLoop ??= Task.Run(() => PingLoopAsync(_cts.Token));
        }

        /// <summary>Adds a topic, ensures the socket is connected, and sends a LISTEN for it.</summary>
        /// <param name="topic">PubSub topic to subscribe to.</param>
        /// <param name="ct">Token to cancel the operation.</param>
        public async Task ListenAsync(string topic, CancellationToken ct)
        {
            _topics.Add(topic);
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            await SendListenAsync(new[] { topic }, listen: true, ct).ConfigureAwait(false);
        }

        /// <summary>Removes a topic and sends an UNLISTEN when the socket is open.</summary>
        /// <param name="topic">PubSub topic to unsubscribe from.</param>
        /// <param name="ct">Token to cancel the operation.</param>
        public async Task UnlistenAsync(string topic, CancellationToken ct)
        {
            if (!_topics.Remove(topic))
                return;
            if (_ws is { State: WebSocketState.Open })
                await SendListenAsync(new[] { topic }, listen: false, ct).ConfigureAwait(false);
        }

        /// <summary>Sends a LISTEN or UNLISTEN frame for the topics with the current auth token.</summary>
        /// <param name="topics">Topics to subscribe or unsubscribe.</param>
        /// <param name="listen">True to LISTEN, false to UNLISTEN.</param>
        /// <param name="ct">Token to cancel the send.</param>
        async Task SendListenAsync(string[] topics, bool listen, CancellationToken ct)
        {
            var msg = new
            {
                type = listen ? "LISTEN" : "UNLISTEN",
                nonce = Guid.NewGuid().ToString("N"),
                data = new { topics, auth_token = _auth.State.AccessToken ?? "" },
            };
            await SendRawAsync(JsonSerializer.Serialize(msg), ct).ConfigureAwait(false);
        }

        /// <summary>Sends a raw text frame when the socket is open, serialized by the send gate.</summary>
        /// <param name="json">Frame text to send.</param>
        /// <param name="ct">Token to cancel the send.</param>
        async Task SendRawAsync(string json, CancellationToken ct)
        {
            if (_ws is not { State: WebSocketState.Open })
                return;
            await _sendGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            catch { /* receive loop will trigger reconnect */ }
            finally { _sendGate.Release(); }
        }

        /// <summary>Reads frames from the current socket until cancelled, reconnecting on close or error.</summary>
        /// <param name="ct">Token that stops the loop.</param>
        async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            while (!ct.IsCancellationRequested)
            {
                // Snapshot the socket we're reading. Reconnect swaps _ws for a fresh instance, so passing
                // this exact instance lets ReconnectAsync coalesce: if _ws already moved on, it's a no-op.
                var sock = _ws;
                if (sock is not { State: WebSocketState.Open })
                {
                    await ReconnectAsync(sock, ct).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await sock.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await ReconnectAsync(sock, ct).ConfigureAwait(false);
                            break;
                        }
                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (ms.Length == 0)
                        continue;

                    HandleFrame(Encoding.UTF8.GetString(ms.ToArray()), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await ReconnectAsync(sock, ct).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Parses one PubSub frame and dispatches MESSAGE payloads or handles RECONNECT.</summary>
        /// <param name="text">Raw frame text.</param>
        /// <param name="ct">Token forwarded to any triggered reconnect.</param>
        void HandleFrame(string text, CancellationToken ct)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(text); }
            catch { return; }

            using (doc)
            {
                var root = doc.RootElement;
                var type = root.Str("type");
                switch (type)
                {
                    case "MESSAGE":
                        var data = root.Prop("data");
                        var topic = data?.Str("topic");
                        var inner = data?.Str("message");
                        if (topic is not null && inner is not null)
                        {
                            try
                            {
                                using var innerDoc = JsonDocument.Parse(inner);
                                _onMessage(topic, innerDoc.RootElement.Clone());
                            }
                            catch { /* ignore malformed inner payloads */ }
                        }
                        break;
                    case "RECONNECT":
                        // Twitch is asking us to move off this socket. Force-drop THIS instance and reopen;
                        // passing it means ReconnectAsync tears it down instead of no-op'ing on a live socket.
                        _ = ReconnectAsync(_ws, ct);
                        break;
                    // "PONG" and "RESPONSE" need no action.
                }
            }
        }

        /// <summary>Sends a PING on the socket every ping interval until cancelled.</summary>
        /// <param name="ct">Token that stops the loop.</param>
        async Task PingLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TwitchConstants.PingInterval, ct).ConfigureAwait(false);
                    await SendRawAsync("{\"type\":\"PING\"}", ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }

        /// <summary>Tear down <paramref name="dead"/> and bring up a fresh socket, re-LISTENing every topic.
        /// Single-flight via <see cref="_connGate"/> and coalescing: if another reconnect already replaced
        /// <paramref name="dead"/> with a live socket, this is a no-op. That stops the connecting/connected
        /// flap where a Twitch RECONNECT and the receive loop both fired a reconnect for the same socket.</summary>
        /// <param name="dead">The socket believed dead; ignored if another reconnect already replaced it.</param>
        /// <param name="ct">Token to cancel the reconnect.</param>
        async Task ReconnectAsync(ClientWebSocket? dead, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return;

            await _connGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Already reconnected by someone else? _ws moved to a fresh, open socket -> nothing to do
                // (just make sure the status flag reflects reality).
                if (!ReferenceEquals(_ws, dead) && _ws is { State: WebSocketState.Open })
                {
                    if (!_connected) { _connected = true; _onStatus(); }
                    return;
                }

                _connected = false;
                _onStatus();
                try { _ws?.Abort(); } catch { }
                _ws?.Dispose();
                _ws = null;

                try { await Task.Delay(ReconnectDelay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                await OpenSocketAsync(ct).ConfigureAwait(false);
                if (_topics.Count > 0)
                    await SendListenAsync(_topics.ToArray(), listen: true, ct).ConfigureAwait(false);
            }
            catch { /* couldn't reopen; the receive loop's next tick retries */ }
            finally { _connGate.Release(); }
        }

        /// <summary>Cancels the loops, closes the socket, and disposes the gates and token source.</summary>
        public async ValueTask DisposeAsync()
        {
            try { _cts.Cancel(); } catch { }
            try
            {
                if (_ws is { State: WebSocketState.Open })
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
            _ws?.Dispose();
            _sendGate.Dispose();
            _connGate.Dispose();
            _cts.Dispose();
        }
    }
}
