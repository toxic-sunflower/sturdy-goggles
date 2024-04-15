using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ProjectX.Interop;
using ProjectX.Authentication;
using ProjectX.Data;
using GrpcMessageSender = Grpc.Core.IAsyncStreamWriter<Google.Protobuf.WellKnownTypes.Any>;

namespace ProjectX.Pvp.Services;


[Authorize]
internal sealed class BattleService : ProjectX.Interop.BattleService.BattleServiceBase
{
    private readonly ICurrentPlayerProvider _currentPlayerProvider;
    private readonly IBroadcaster _broadcaster;
    private readonly IRoomHub _roomHub;
    private readonly ProjectXDbContext _dbContext;
    private readonly ILogger<BattleService> _logger;

    
    public BattleService(
        IBroadcaster broadcaster,
        IRoomHub roomHub,
        ProjectXDbContext dbContext,
        ILogger<BattleService> logger,
        ICurrentPlayerProvider currentPlayerProvider)
    {
        _roomHub = roomHub;
        _dbContext = dbContext;
        _logger = logger;
        _currentPlayerProvider = currentPlayerProvider;
        _broadcaster = broadcaster;
    }


    public override async Task Echo(
        IAsyncStreamReader<BattleSync> requestStream,
        IServerStreamWriter<Any> responseStream,
        ServerCallContext context)
    {
        try
        {
            var playerId = _currentPlayerProvider.GetId();
            var roomId = long.Parse(context.RequestHeaders.Get("matchId")!.Value);

            var match = await _dbContext.Matches
                .Include(m => m.PlayerMatches
                    .Where(pm => pm.PlayerId == playerId))
                .SingleOrDefaultAsync(
                    m => m.Id == roomId,
                    context.CancellationToken);

            if (match is null)
                throw new RpcException(
                    new Status(
                        StatusCode.NotFound,
                        $"No such room: {roomId}"));

            if (!match.PlayerMatches.Any())
                throw new RpcException(
                    new Status(
                        StatusCode.PermissionDenied,
                        $"Player {playerId} doesn't belong to room {roomId}"));
            
            if (!await requestStream.MoveNext())
                return;

            await using var connection = _roomHub
                .Connect(roomId, playerId, responseStream);

            await foreach (var message in requestStream
                   .ReadAllAsync()
                   .WithCancellation(context.CancellationToken))
            {
                try
                {
                    await _broadcaster.BroadcastAsync(
                        roomId,
                        playerId,
                        DateTimeOffset.Now,
                        message.Data.Memory);
                }
                catch (Exception x)
                {
                    throw new RpcException(new Status(StatusCode.Unknown, x.ToString()));
                }
            }            

            await _broadcaster.SendDisconnectedAsync(roomId, playerId);

            // Create replay of connection is last.
        }
        catch (Exception x) when (x is not RpcException)
        {
            throw new RpcException(new Status(StatusCode.Unknown, x.ToString()));
        }
    }
}