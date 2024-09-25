// See https://aka.ms/new-console-template for more information

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;

Console.WriteLine(Environment.ProcessId);

// Load settings
int maxClientCount = 20;
int threadCount = 1_000;
TimeSpan runFor = TimeSpan.FromMinutes(1);

// Stats
long requestCount = 0;
long startTime = Environment.TickCount64;

// Clients
TimeSpan idleTimeout = TimeSpan.FromSeconds(5);
ConcurrentDictionary<int, RequestCountingClient> clients = new ConcurrentDictionary<int, RequestCountingClient>();

// These tasks run the load for specified threadCount.
var tasks = Enumerable.Range(0, threadCount).Select(i =>
{
    return Task.Run(async () =>
    {
        // The individual task will stop after runFor time.
        while (TimeSpan.FromMilliseconds(Environment.TickCount64 - startTime) <= runFor)
        {
            var client = GetClient();
            // Make sure to call the overriden SendAsync and force H/3.
            using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://localhost:5001/")
            {
                Version = HttpVersion.Version30,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            }, default(CancellationToken));
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync();
            Debug.Assert(text == "Hello World!");
            Interlocked.Increment(ref requestCount);
        }
    });
});

var reportTask = Task.Run(async () =>
{
    long lastRequestCount = 0;
    long lastTime = Environment.TickCount64;
    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        long currentRequestCount = Interlocked.Read(ref requestCount);
        long currentTime = Environment.TickCount64;
        long rps = (currentRequestCount - lastRequestCount) / ((currentTime - lastTime) / 1000);
        lastRequestCount = currentRequestCount;
        lastTime = currentTime;
        Console.WriteLine($"Clients: {clients.Count}, processed requests: {currentRequestCount}, rps: {rps}");
        
        // Stop the process when everything wound down.
        if (clients.Count == 0 && rps == 0)
        {
            Environment.Exit(0);
        }
    }
});

// Gets rid of unused connection if there are nor requests in flight.
var scavengeTask = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        foreach (var pair in clients)
        {
            var client = pair.Value;
            if (!client.IsUsable(idleTimeout))
            {
                if (clients.TryRemove(client.Id, out client))
                {
                    client.Dispose();
                }
            }
        }
    }
});

// Wait for the tasks.
await Task.WhenAll(Task.WhenAll(tasks), reportTask, scavengeTask);

HttpClient GetClient()
{
    foreach (var pair in clients)
    {
        var client = pair.Value;
        // Scavenging.
        if (!client.IsUsable(idleTimeout))
        {
            if (clients.TryRemove(client.Id, out client))
            {
                client.Dispose();
            }
            continue;
        }
    
        // We have a client with capacity ==> return it.
        if (client.TryReserveStream())
        {
            return client;
        }
    }

    // This can actually go over max client count, but it'll not grow indefinitely.
    if (clients.Count >= maxClientCount)
    {
        // We hit the limit of clients and limit of streams on all of them, so just pick one connection with the least active requests (give or take).
        var client = clients.MinBy(p => p.Value.ActiveRequests).Value;
        client.TryReserveStream(true);
        return client;
    }

    // Create new client/connection and return it.
    var newClient = new RequestCountingClient();
    var reserved = newClient.TryReserveStream();
    Debug.Assert(reserved);
    clients.TryAdd(newClient.Id, newClient);
    return newClient;
}

public class RequestCountingClient : HttpClient
{
    private const int MaxRequestCount = 100;
    private static int s_lastId = -1;

    private readonly int _id = Interlocked.Increment(ref s_lastId);

    private int _activeRequests;
    private long _notUsedSince = Environment.TickCount64;

    public RequestCountingClient()
        : base(new SocketsHttpHandler()
        {
            // For local testing (shouldn't be used in prod), skips cert validation.
            SslOptions = new SslClientAuthenticationOptions()
            {
                RemoteCertificateValidationCallback = delegate { return true; }
            }
        })
    { }

    public int Id => _id;

    public bool IsUsable(TimeSpan connectionIdleTimeout)
    {
        lock (this)
        {
            if (_activeRequests == 0 &&
                (Environment.TickCount64 - _notUsedSince) > connectionIdleTimeout.TotalMilliseconds)
            {
                return false;
            }

            return true;
        }
    }

    public int ActiveRequests => Volatile.Read(ref _activeRequests);

    // Increments the request count if we have the capacity (or if we're forced).
    public bool TryReserveStream(bool force = false)
    {
        lock (this)
        {
            if (force || _activeRequests < MaxRequestCount)
            {
                ++_activeRequests;
                return true;
            }

            return false;
        }
    }

    public override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // We've incremented the count before choosing this client and processing the request here
        // so we're gonna just decrement after the request gets processed.
        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        finally
        {
            lock (this)
            {
                Debug.Assert(_activeRequests > 0);
                --_activeRequests;
                _notUsedSince = Environment.TickCount64;
            }
        }
    }
}

