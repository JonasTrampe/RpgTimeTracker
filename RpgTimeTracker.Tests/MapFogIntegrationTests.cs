using System;
using System.IO;
using System.Threading.Tasks;
using RpgTimeTracker.Network;
using RpgTimeTracker.PlayerClient.Network;
using RpgTimeTracker.Shared.Models;
using RpgTimeTracker.Shared.Models.Rpc;
using RpgTimeTracker.Shared.Services;

namespace RpgTimeTracker.Tests;

/// <summary>
///     End-to-end tests of the host-to-client map/fog wire path, using the REAL
///     TcpPlayerServerService and PlayerTcpClientService over a real localhost socket - not
///     mocks. A mock of either side would not have caught the actual bugs this guards against
///     (e.g. a map-floor image header that forgot to set TotalLength, silently truncating every
///     transfer - see git history for "Fix map floor images never fully arriving on clients").
///
///     Needs a running Avalonia dispatcher: TcpPlayerServerService posts its new-connection
///     catch-up and heartbeat-loop work through Dispatcher.UIThread (see
///     TcpPlayerServerService.SendCatchUpAsync/HeartbeatLoopAsync) since that code also reads
///     UI-bound state in the real app. Every test body below runs via HeadlessDispatch.RunAsync,
///     which provides that real, pumped dispatcher (see HeadlessDispatch.cs for why not
///     Avalonia.Headless.XUnit's [AvaloniaFact] directly).
///
///     Not parallelized with itself (each test binds a real TCP port) - see the CollectionDefinition
///     below. Each test asks the OS for a free ephemeral port (TcpPlayerServerService.Start(0, ...))
///     rather than using a fixed one, so no two test runs (including Theory cases of the same
///     test method, which do NOT necessarily run strictly sequentially) can ever collide on a
///     still-releasing socket.
/// </summary>
[Collection(nameof(MapFogIntegrationCollection))]
public sealed class MapFogIntegrationTests : IDisposable
{
    // A bit more generous than the actual expected latency (localhost round trips complete in
    // low milliseconds) to comfortably absorb the first test's one-off cold-start cost
    // (HeadlessUnitTestSession's dispatcher thread spinning up, JIT warmup, etc.) - a real hang
    // would still fail well before this budget.
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    private TcpPlayerServerService? _server;
    private PlayerTcpClientService? _client;
    private int _port;

    public void Dispose()
    {
        _client?.Disconnect();
        _client?.Dispose();
        _server?.Dispose();
    }

    private async Task<PlayerTcpClientService> StartServerAndConnectedClientAsync()
    {
        _server = new TcpPlayerServerService(
            snapshotProvider: () => new SessionSnapshotParams(),
            clockStateProvider: () => new ClockHeartbeatParams());
        _server.Start(0, "MapFogIntegrationTests", enableDiscovery: false);
        _port = _server.Port;

        _client = new PlayerTcpClientService();
        var connected = WaitForEvent<bool>(h => _client.ConnectionStateChanged += h, h => _client.ConnectionStateChanged -= h);

        // ConnectionStateChanged fires as soon as the CLIENT's TCP connect succeeds - the
        // SERVER hasn't necessarily finished processing session.hello and added this connection
        // to its broadcast list (_clients) yet, that happens asynchronously afterward. Publishing
        // anything before that registration completes would silently reach nobody (broadcasts
        // only iterate already-registered clients) and the test would hang until EventTimeout
        // waiting for an event that was never going to arrive - a real (if rare) race, not a
        // production bug. SessionSnapshotReceived only fires once the server's catch-up has
        // actually run for this connection, so waiting for it here is the correct "fully
        // connected, ready to receive broadcasts" signal.
        var snapshotReceived = WaitForEvent<SessionSnapshotParams>(h => _client.SessionSnapshotReceived += h,
            h => _client.SessionSnapshotReceived -= h);

        await _client.ConnectAsync("127.0.0.1", _port).ConfigureAwait(false);
        Assert.True(await connected, "Client never reported a successful connection.");
        await snapshotReceived;

        return _client;
    }

    private static Task<T> WaitForEvent<T>(Action<Action<T>> subscribe, Action<Action<T>> unsubscribe)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(T value)
        {
            tcs.TrySetResult(value);
        }

        subscribe(Handler);
        return WaitWithTimeoutAsync(tcs.Task, () => unsubscribe(Handler));
    }

    private static Task<(T1, T2)> WaitForEvent<T1, T2>(Action<Action<T1, T2>> subscribe, Action<Action<T1, T2>> unsubscribe)
    {
        var tcs = new TaskCompletionSource<(T1, T2)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(T1 first, T2 second)
        {
            tcs.TrySetResult((first, second));
        }

        subscribe(Handler);
        return WaitWithTimeoutAsync(tcs.Task, () => unsubscribe(Handler));
    }

    private static async Task<T> WaitWithTimeoutAsync<T>(Task<T> task, Action unsubscribe)
    {
        try
        {
            var completed = await Task.WhenAny(task, Task.Delay(EventTimeout)).ConfigureAwait(false);
            if (completed != task) throw new TimeoutException($"Timed out waiting {EventTimeout} for an event.");
            return await task.ConfigureAwait(false);
        }
        finally
        {
            unsubscribe();
        }
    }

    private static TcpPlayerServerService.OpenMapFloor CreateFloor(Guid floorId, byte[] imageBytes, FogMask startingFog,
        FogMask? currentFog = null)
    {
        return new TcpPlayerServerService.OpenMapFloor
        {
            FloorId = floorId,
            FloorName = "Test Floor",
            ImageFileName = "floor.png",
            ImageMimeType = "image/png",
            ImageBytes = imageBytes,
            CellSizePx = startingFog.CellSizePx,
            GridWidth = startingFog.GridWidth,
            GridHeight = startingFog.GridHeight,
            StartingFog = startingFog,
            CurrentFog = currentFog ?? startingFog.Clone()
        };
    }

    [Fact]
    public Task Floor_image_larger_than_one_chunk_arrives_byte_identical()
    {
        return HeadlessDispatch.RunAsync(async () =>
        {
            var client = await StartServerAndConnectedClientAsync();

            // 64KB is TcpPlayerServerService's chunk size - anything past that exercises the
            // exact path that used to complete (and stop reading chunks) after the FIRST one,
            // because the header's TotalLength was never set. A recognizable, non-repeating
            // pattern (not just zeros) so a truncated/misaligned copy can't accidentally match.
            var originalBytes = new byte[200_000];
            for (var i = 0; i < originalBytes.Length; i++) originalBytes[i] = (byte)(i % 251);

            var floorId = Guid.NewGuid();
            var floor = CreateFloor(floorId, originalBytes, FogMask.CreateFullyHidden(4, 4, 32));

            var received = WaitForEvent<Guid, string>(
                h => client.MapFloorImageReceived += h,
                h => client.MapFloorImageReceived -= h);

            await _server!.PublishMapShowAsync(Guid.NewGuid(), "Test Map", [floor]).ConfigureAwait(false);
            var (receivedFloorId, tempPath) = await received;

            Assert.Equal(floorId, receivedFloorId);
            var receivedBytes = await File.ReadAllBytesAsync(tempPath);
            Assert.Equal(originalBytes.Length, receivedBytes.Length);
            Assert.Equal(originalBytes, receivedBytes);

            File.Delete(tempPath);
        });
    }

    [Fact]
    public Task Fog_reveal_and_hide_cells_apply_to_the_client_side_mask()
    {
        return HeadlessDispatch.RunAsync(async () =>
        {
            var client = await StartServerAndConnectedClientAsync();
            var floorId = Guid.NewGuid();
            var startingFog = FogMask.CreateFullyHidden(4, 4, 32);
            var floor = CreateFloor(floorId, [1, 2, 3], startingFog);

            var mapShown = WaitForEvent<MapShowParams>(h => client.MapShowReceived += h, h => client.MapShowReceived -= h);
            await _server!.PublishMapShowAsync(Guid.NewGuid(), "Test Map", [floor]).ConfigureAwait(false);
            var mapShow = await mapShown;

            var clientFog = FogMaskSerializer.Deserialize(Convert.FromBase64String(mapShow.Floors[0].CurrentFogBase64));
            Assert.False(clientFog.IsRevealed(1, 1));

            var fogUpdated = WaitForEvent<MapFogUpdateParams>(h => client.MapFogUpdateReceived += h,
                h => client.MapFogUpdateReceived -= h);
            await _server.PublishMapFogUpdateAsync(floorId,
            [
                new FogCellDto { X = 1, Y = 1, Revealed = true },
                new FogCellDto { X = 2, Y = 2, Revealed = true }
            ]).ConfigureAwait(false);
            var update = await fogUpdated;

            foreach (var cell in update.Cells) clientFog.SetRevealed(cell.X, cell.Y, cell.Revealed);

            Assert.True(clientFog.IsRevealed(1, 1));
            Assert.True(clientFog.IsRevealed(2, 2));
            Assert.False(clientFog.IsRevealed(0, 0));
        });
    }

    [Fact]
    public Task Fog_reset_restores_the_starting_template()
    {
        return HeadlessDispatch.RunAsync(async () =>
        {
            var client = await StartServerAndConnectedClientAsync();
            var floorId = Guid.NewGuid();
            var startingFog = FogMask.CreateFullyHidden(4, 4, 32);
            var floor = CreateFloor(floorId, [1, 2, 3], startingFog);

            var mapShown = WaitForEvent<MapShowParams>(h => client.MapShowReceived += h, h => client.MapShowReceived -= h);
            await _server!.PublishMapShowAsync(Guid.NewGuid(), "Test Map", [floor]).ConfigureAwait(false);
            var mapShow = await mapShown;
            var clientStartingFog =
                FogMaskSerializer.Deserialize(Convert.FromBase64String(mapShow.Floors[0].StartingFogBase64));
            var clientCurrentFog =
                FogMaskSerializer.Deserialize(Convert.FromBase64String(mapShow.Floors[0].CurrentFogBase64));

            var fogUpdated = WaitForEvent<MapFogUpdateParams>(h => client.MapFogUpdateReceived += h,
                h => client.MapFogUpdateReceived -= h);
            await _server.PublishMapFogUpdateAsync(floorId, [new FogCellDto { X = 1, Y = 1, Revealed = true }])
                .ConfigureAwait(false);
            var update = await fogUpdated;
            foreach (var cell in update.Cells) clientCurrentFog.SetRevealed(cell.X, cell.Y, cell.Revealed);
            Assert.True(clientCurrentFog.IsRevealed(1, 1));

            var fogReset = WaitForEvent<Guid>(h => client.MapFogResetReceived += h, h => client.MapFogResetReceived -= h);
            await _server.PublishMapFogResetAsync(floorId).ConfigureAwait(false);
            var resetFloorId = await fogReset;
            Assert.Equal(floorId, resetFloorId);

            clientCurrentFog.RevealedBits = clientStartingFog.RevealedBits;
            Assert.False(clientCurrentFog.IsRevealed(1, 1));
        });
    }

    [Theory]
    [InlineData("#0C0C0C", 100, 0.0, true)]
    [InlineData("#FF00AA", 45, 60.0, true)]
    [InlineData("#123456", 0, 30.0, false)]
    [InlineData("#FFFFFF", 100, 0.0, false)]
    public Task Render_style_settings_round_trip_to_the_client_unchanged(string colorHex, int opacityPercent,
        double blurRadius, bool blurEnabled)
    {
        return HeadlessDispatch.RunAsync(async () =>
        {
            var client = await StartServerAndConnectedClientAsync();

            var styleChanged = WaitForEvent<MapRenderStyleChangedParams>(h => client.MapRenderStyleChanged += h,
                h => client.MapRenderStyleChanged -= h);
            await _server!.PublishMapRenderStyleAsync(colorHex, opacityPercent, blurRadius, blurEnabled)
                .ConfigureAwait(false);
            var received = await styleChanged;

            Assert.Equal(colorHex, received.ColorHex);
            Assert.Equal(opacityPercent, received.OpacityPercent);
            Assert.Equal(blurRadius, received.BlurRadius);
            Assert.Equal(blurEnabled, received.BlurEnabled);
        });
    }

    [Fact]
    public Task Reconnect_resyncs_the_currently_open_map_with_its_latest_fog()
    {
        return HeadlessDispatch.RunAsync(async () =>
        {
            var client = await StartServerAndConnectedClientAsync();
            var floorId = Guid.NewGuid();
            var startingFog = FogMask.CreateFullyHidden(4, 4, 32);
            var floor = CreateFloor(floorId, [1, 2, 3], startingFog);

            var firstMapShow = WaitForEvent<MapShowParams>(h => client.MapShowReceived += h, h => client.MapShowReceived -= h);
            await _server!.PublishMapShowAsync(Guid.NewGuid(), "Test Map", [floor]).ConfigureAwait(false);
            await firstMapShow;

            // Reveal a cell - PublishMapFogUpdateAsync also updates the SERVER's own cached
            // _openMap.Floors[].CurrentFog (see TcpPlayerServerService), which is exactly what a
            // reconnect resync must read from - not the floor object this test built, which the
            // server never sees again after PublishMapShowAsync.
            await _server.PublishMapFogUpdateAsync(floorId, [new FogCellDto { X = 1, Y = 1, Revealed = true }])
                .ConfigureAwait(false);

            client.Disconnect();

            var secondMapShow = WaitForEvent<MapShowParams>(h => client.MapShowReceived += h, h => client.MapShowReceived -= h);
            await client.ConnectAsync("127.0.0.1", _port).ConfigureAwait(false);
            var resynced = await secondMapShow;

            var resyncedFog = FogMaskSerializer.Deserialize(Convert.FromBase64String(resynced.Floors[0].CurrentFogBase64));
            Assert.True(resyncedFog.IsRevealed(1, 1));
        });
    }
}

[CollectionDefinition(nameof(MapFogIntegrationCollection), DisableParallelization = true)]
public sealed class MapFogIntegrationCollection;
