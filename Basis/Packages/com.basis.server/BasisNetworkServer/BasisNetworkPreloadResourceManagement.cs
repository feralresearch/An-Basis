using Basis.Network.Core;
using BasisNetworkServer.Security;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static BasisPermissions.PermissionManager;
using static SerializableBasis;

/// <summary>
/// Server-side tracking for synchronized resource loads.
/// Tracks which clients have reported readiness and triggers the spawn
/// signal when all are ready or the timeout expires.
/// </summary>
public static class BasisNetworkPreloadResourceManagement
{
    /// <summary>
    /// Timeout for synchronized loads. After this duration the server sends
    /// the spawn signal regardless of how many clients have reported.
    /// </summary>
    public static readonly TimeSpan SynchronizedTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Active synchronized load sessions, keyed by LoadedNetID.
    /// </summary>
    public static readonly ConcurrentDictionary<string, SyncLoadSession> ActiveSessions = new();

    public class SyncLoadSession
    {
        public LocalLoadResource Resource;

        /// <summary>
        /// Peers the barrier is waiting on. Headless clients (observers,
        /// server-side tooling) never ack preloads and are never members.
        /// </summary>
        public HashSet<int> CountedPeers = new();
        public HashSet<int> ReadyPeers = new();
        public HashSet<int> FailedPeers = new();
        public DateTime StartTimeUtc;
        public CancellationTokenSource TimeoutCts;

        public int TotalPeerCount => CountedPeers.Count;
        public bool IsComplete => ReadyPeers.Count + FailedPeers.Count >= TotalPeerCount;
    }

    /// <summary>
    /// Whether a peer participates in the synchronized-load barrier.
    /// Headless platforms can't preload or ack, so counting them would hold
    /// every switch open until the timeout.
    /// </summary>
    public static bool CountsTowardBarrier(NetPeer peer)
    {
        if (NetworkServer.AuthIdentity == null ||
            !NetworkServer.AuthIdentity.NetIDToUUID(peer, out string uuid) ||
            string.IsNullOrEmpty(uuid) ||
            !PermissionIntegration.TryGetPlayerMeta(uuid, out var meta))
            return true;
        return !BasisHeadlessConnectionPolicyManager.IsHeadlessPlatform(meta.playerPlatform);
    }

    /// <summary>
    /// Called when the server receives a LoadResource with LoadStrategy = 2 (Synchronized).
    /// Broadcasts the preload request to all clients and starts tracking readiness.
    /// </summary>
    public static void StartSynchronizedLoad(LocalLoadResource resource)
    {
        string netId = resource.LoadedNetID;

        if (ActiveSessions.ContainsKey(netId))
        {
            BNL.LogError($"PreloadResourceManagement: Session already exists for {netId}");
            return;
        }

        var peerSnapshot = NetworkServer.PeerSnapshot;

        var session = new SyncLoadSession
        {
            Resource = resource,
            StartTimeUtc = DateTime.UtcNow,
            TimeoutCts = new CancellationTokenSource(),
        };
        foreach (var peer in peerSnapshot)
        {
            if (CountsTowardBarrier(peer)) session.CountedPeers.Add(peer.Id);
        }

        if (!ActiveSessions.TryAdd(netId, session))
        {
            BNL.LogError($"PreloadResourceManagement: Failed to add session for {netId}");
            return;
        }

        BNL.Log($"PreloadResourceManagement: Starting synchronized load for {netId}, waiting on {session.TotalPeerCount} of {peerSnapshot.Length} peers");

        // Broadcast the load resource to all clients (they will see LoadStrategy = 2
        // and handle it as a synchronized preload)
        NetDataWriter writer = NetworkServer.RentWriter();
        resource.Serialize(writer);
        NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.LoadResourceChannel, peerSnapshot, DeliveryMethod.ReliableOrdered);
        NetworkServer.ReturnWriter(writer);

        // Store in the main resource database too
        BasisNetworkResourceManagement.UshortNetworkDatabase.TryAdd(netId, resource);

        // No countable peers: complete immediately rather than waiting for the 5-minute timeout
        if (session.TotalPeerCount == 0)
        {
            BNL.Log($"PreloadResourceManagement: No barrier peers connected, completing {netId} immediately");
            session.TimeoutCts.Cancel();
            BroadcastSpawnSignal(netId);
            return;
        }

        // Start timeout task
        _ = RunTimeoutAsync(netId, session);
    }

    /// <summary>
    /// Called when the server receives a PreloadReady message from a client.
    /// </summary>
    public static void HandleClientReady(string loadedNetId, int peerId, bool isReady)
    {
        if (!ActiveSessions.TryGetValue(loadedNetId, out SyncLoadSession session))
        {
            BNL.LogError($"PreloadResourceManagement: Received ready from peer {peerId} for unknown session {loadedNetId}");
            return;
        }

        if (!session.CountedPeers.Contains(peerId))
        {
            BNL.Log($"PreloadResourceManagement: Ignoring report from uncounted peer {peerId} for {loadedNetId}");
            return;
        }

        if (isReady)
        {
            session.ReadyPeers.Add(peerId);
            BNL.Log($"PreloadResourceManagement: Peer {peerId} ready for {loadedNetId} ({session.ReadyPeers.Count + session.FailedPeers.Count}/{session.TotalPeerCount})");
        }
        else
        {
            session.FailedPeers.Add(peerId);
            BNL.Log($"PreloadResourceManagement: Peer {peerId} FAILED for {loadedNetId} ({session.ReadyPeers.Count + session.FailedPeers.Count}/{session.TotalPeerCount})");
        }

        if (session.IsComplete)
        {
            BNL.Log($"PreloadResourceManagement: All peers reported for {loadedNetId}, sending spawn signal");
            session.TimeoutCts.Cancel();
            BroadcastSpawnSignal(loadedNetId);
        }
    }

    /// <summary>
    /// Runs the timeout for a synchronized load session.
    /// If not all clients have reported by the timeout, sends the spawn signal anyway.
    /// </summary>
    private static async Task RunTimeoutAsync(string netId, SyncLoadSession session)
    {
        try
        {
            await Task.Delay(SynchronizedTimeout, session.TimeoutCts.Token);

            // Timeout reached - send spawn signal regardless
            BNL.Log($"PreloadResourceManagement: Timeout reached for {netId}. Ready: {session.ReadyPeers.Count}, Failed: {session.FailedPeers.Count}, Total: {session.TotalPeerCount}");
            BroadcastSpawnSignal(netId);
        }
        catch (TaskCanceledException)
        {
            // Session completed before timeout - normal
        }
    }

    /// <summary>
    /// Broadcasts the spawn signal to all connected clients for a synchronized load.
    /// Also broadcasts unload messages for existing scenes through the normal unload path
    /// so the server tracking stays consistent. The client-side HandleSpawnPreloaded
    /// also unloads scenes locally as a safety net against message ordering races.
    /// </summary>
    private static void BroadcastSpawnSignal(string loadedNetId)
    {
        if (!ActiveSessions.TryRemove(loadedNetId, out SyncLoadSession session))
        {
            return;
        }

        session.TimeoutCts.Dispose();

        var peerSnapshot = NetworkServer.PeerSnapshot;

        // Only unload existing scenes when the synchronized resource is itself a scene.
        // Props (Mode == 0) should never cause scene unloads.
        // Exclude loadedNetId — that is the scene being switched TO; removing it would
        // race against the SpawnPreloaded signal on the client.
        if (session.Resource.Mode == 1)
        {
            UnloadAllSceneResources(peerSnapshot, loadedNetId);
        }

        SpawnPreloadedMessage spawnMsg = new SpawnPreloadedMessage
        {
            LoadedNetID = loadedNetId,
        };

        NetDataWriter writer = NetworkServer.RentWriter();
        spawnMsg.Serialize(writer);
        NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.SpawnPreloadedChannel, peerSnapshot, DeliveryMethod.ReliableOrdered);
        NetworkServer.ReturnWriter(writer);

        BNL.Log($"PreloadResourceManagement: Spawn signal sent for {loadedNetId}");
    }

    /// <summary>
    /// Unloads all scene-type resources (Mode == 1) from the server database
    /// and broadcasts unload messages to all clients through the normal unload channel.
    /// </summary>
    private static void UnloadAllSceneResources(NetPeer[] peerSnapshot, string excludeNetId = null)
    {
        var sceneResources = BasisNetworkResourceManagement.UshortNetworkDatabase.Values
            .Where(r => r.Mode == 1 && r.LoadedNetID != excludeNetId)
            .ToArray();

        if (sceneResources.Length == 0) return;

        BNL.Log($"PreloadResourceManagement: Unloading {sceneResources.Length} existing scene(s) before synchronized spawn");

        NetDataWriter writer = NetworkServer.RentWriter();
        foreach (var scene in sceneResources)
        {
            BasisNetworkResourceManagement.UshortNetworkDatabase.TryRemove(scene.LoadedNetID, out _);

            UnLoadResource unload = new UnLoadResource
            {
                LoadedNetID = scene.LoadedNetID,
                Mode = 1,
            };
            writer.Reset();
            unload.Serialize(writer);
            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.UnloadResourceChannel, peerSnapshot, DeliveryMethod.ReliableOrdered);

            BNL.Log($"PreloadResourceManagement: Unloaded scene {scene.LoadedNetID}");
        }
        NetworkServer.ReturnWriter(writer);
    }

    /// <summary>
    /// Removes a disconnected peer from all active synchronized load sessions.
    /// Decrements the expected peer count and triggers the spawn signal if
    /// all remaining peers have already reported.
    /// </summary>
    public static void RemovePeer(int peerId)
    {
        List<string> completedSessions = null;

        foreach (var kvp in ActiveSessions)
        {
            var session = kvp.Value;
            if (!session.CountedPeers.Remove(peerId)) continue;
            session.ReadyPeers.Remove(peerId);
            session.FailedPeers.Remove(peerId);

            // Completing (rather than discarding) when the last counted peer
            // leaves keeps the server database consistent: old scenes are
            // unloaded and any uncounted peers still get the spawn signal.
            if (session.IsComplete)
            {
                completedSessions ??= new List<string>();
                completedSessions.Add(kvp.Key);
            }
        }

        if (completedSessions != null)
        {
            foreach (var netId in completedSessions)
            {
                if (ActiveSessions.TryGetValue(netId, out var session))
                {
                    BNL.Log($"PreloadResourceManagement: All remaining peers reported for {netId} after peer {peerId} disconnected, sending spawn signal");
                    session.TimeoutCts.Cancel();
                    BroadcastSpawnSignal(netId);
                }
            }
        }
    }

    /// <summary>
    /// Cleans up all active sessions. Called on server reset.
    /// </summary>
    public static void Reset()
    {
        foreach (var kvp in ActiveSessions)
        {
            kvp.Value.TimeoutCts?.Cancel();
            kvp.Value.TimeoutCts?.Dispose();
        }
        ActiveSessions.Clear();
    }
}
