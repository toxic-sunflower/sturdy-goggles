using System.Collections.Concurrent;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using ProjectX.Data;

namespace ProjectX.Pvp.Core;


internal sealed class PvpHost : IRoomHub, IBroadcaster
{
    private const int ReconnectionSeconds = 30;
    private const int RemoveOrphansIntervalSeconds = 1;

    private readonly Timer _removeOrphansTimer;
    private readonly List<(long, Guid, DateTimeOffset)> _reconnectingClients = new();
    private readonly ConcurrentDictionary<long, Room> _rooms = new ();
    private readonly IServiceProvider _services;
    
    
    public PvpHost(IServiceProvider services)
    {
        _services = services;
        _removeOrphansTimer = new Timer(
            RemoveOrphans,
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(RemoveOrphansIntervalSeconds));
    }
    
    
    public Connection Connect(long roomId, Guid clientId, IServerStreamWriter<Any> sender)
    {
        var room = _rooms.GetOrAdd(roomId, AddRoom);
        var connection = new Connection(room, clientId, sender);
        
        return room.RegisterConnection(clientId, connection);
    }

    private Room AddRoom(long rid)
    {
        using var scope = _services.CreateScope();
        using var dbContext = scope.ServiceProvider.GetRequiredService<ProjectXDbContext>();

        var players = dbContext.PlayerMatches
            .Include(p => p.Player)
            .Where(p => p.MatchId == rid)
            .ToDictionary(p => p.PlayerId, p => p.Player.SerialId);

        return new Room(rid, players);
    }

    public async Task BroadcastAsync(
        long roomId,
        Guid senderId,
        DateTimeOffset timestamp,
        ReadOnlyMemory<byte> message)
    {   
        if (!_rooms.TryGetValue(roomId, out var room))
            throw new InvalidOperationException(
                $"Can't get connections of room {roomId}: no such room.");

        await room.BroadcastMessageAsync(message);
    }


    public Task SendReconnectingAsync(long roomId, Guid playerId)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
            throw new InvalidOperationException(
                $"Can't get connections of room {roomId}: no such room.");
        
        _reconnectingClients.Add((roomId, playerId, DateTimeOffset.UtcNow));
        
        return room.SendReconnectingAsync(playerId);
    }

    public Task SendDisconnectedAsync(long roomId, Guid playerId)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
            throw new InvalidOperationException(
                $"Can't get connections of room {roomId}: no such room.");
        
        return room.SendDisconnectedAsync(playerId);
    }

    private void RemoveOrphans(object? state)
    {
        foreach (var (id, room) in _rooms.ToArray())
        {
            if (!room.IsFinished)
                continue;

            _rooms.Remove(id, out _);
        }

        foreach (var (roomId, clientId, reconnectingFrom) in _reconnectingClients)
        {
            if (reconnectingFrom + TimeSpan.FromSeconds(ReconnectionSeconds) < DateTimeOffset.UtcNow)
                continue;
            
            if (!_rooms.TryGetValue(roomId, out var room))
                continue;

            room.SendDisconnectedAsync(clientId)
                .GetAwaiter()
                .GetResult();
        }
    }
}