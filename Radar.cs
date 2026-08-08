using ExileCore2;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using ExileCore2.Shared.Helpers;
using GameOffsets2;
using GameOffsets2.Native;
using ImGuiNET;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Positioned = ExileCore2.PoEMemory.Components.Positioned;
using RectangleF = SixLabors.ImageSharp.RectangleF;

namespace Radar;

public partial class Radar : BaseSettingsPlugin<RadarSettings>
{
    private const string TextureName = "radar_minimap";
    private const int TileToGridConversion = 23;
    private const int TileToWorldConversion = 250;
    public const float GridToWorldMultiplier = TileToWorldConversion / (float)TileToGridConversion;
    private const double CameraAngle = 38.7 * Math.PI / 180;
    private static readonly float CameraAngleCos = (float)Math.Cos(CameraAngle);
    private static readonly float CameraAngleSin = (float)Math.Sin(CameraAngle);
    private double _mapScale;

    private ConcurrentDictionary<string, List<TargetDescription>> _targetDescriptions = new();
    private Vector2i? _areaDimensions;
    private TerrainData _terrainMetadata;
    private float[][] _heightData;
    private int[][] _processedTerrainData;
    private int[][] _processedTerrainTargetingData;
    private Dictionary<string, TargetDescription> _targetDescriptionsInArea = new();
    private List<(Regex, TargetDescription x)> _currentZoneTargetEntityPaths = new();
    private CancellationTokenSource _findPathsCts = new CancellationTokenSource();
    private ConcurrentDictionary<string, TargetLocations> _clusteredTargetLocations = new();
    private ConcurrentDictionary<string, List<Vector2i>> _allTargetLocations = new();
    private ConcurrentDictionary<string, List<Room>> _rooms = [];
    private ConcurrentDictionary<Vector2i, List<string>> _locationsByPosition = new();
    private ExileCore2.Shared.RectangleF _rect;
    private ImDrawListPtr _backGroundWindowPtr;
    private ConcurrentDictionary<Vector2, RouteDescription> _routes = new();
    private bool _settingsHooksAttached;
    private volatile bool _restartPathFindingRequested;
    private volatile bool _mapTextureRefreshRequested;
    private volatile bool _dumpRequested;

    public override bool Initialise()
    {
        GameController.PluginBridge.SaveMethod("Radar.LookForRoute",
            (Vector2 target, Action<List<Vector2i>> callback, CancellationToken cancellationToken) =>
                AddRoute(target, callback, cancellationToken));
        GameController.PluginBridge.SaveMethod("Radar.ClusterTarget",
            (string targetName, int expectedCount) => ClusterTarget(targetName, null, expectedCount));
        GameController.PluginBridge.SaveMethod("Radar.GetMapImage",
            (bool includeRoutes) => GetMapImageBytes(includeRoutes));
        GameController.PluginBridge.SaveMethod("Radar.GetMapSvg",
            (bool includeRoutes) => GetMapSvgString(includeRoutes));

        Input.RegisterKey(Settings.InstanceDumpSettings.ManualDumpHotkey.Value);
        Settings.InstanceDumpSettings.ManualDumpButton.OnPressed += RunDump;
        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        StopPathFinding();
        if (!Settings.Enable)
            return;
        if (GameController.Game.IsInGameState || GameController.Game.IsEscapeState)
        {
            _targetDescriptionsInArea = GetTargetDescriptionsInArea().DistinctBy(x => x.EqualityId).ToDictionary(x => x.EqualityId);
            _currentZoneTargetEntityPaths = _targetDescriptionsInArea.Values.Where(x => x.TargetType == TargetType.Entity).DistinctBy(x => x.Name).Select(x=>(x.Name.ToLikeRegex(), x)).ToList();
            _terrainMetadata = GameController.IngameState.Data.Terrain;
            _heightData = GameController.IngameState.Data.RawTerrainHeightData;
            _allTargetLocations = GetTargets();
            _rooms = GetRooms();
            _locationsByPosition = new ConcurrentDictionary<Vector2i, List<string>>(_allTargetLocations
                .SelectMany(x => x.Value.Select(y => (x.Key, y)))
                .ToLookup(x => x.y, x => x.Key)
                .ToDictionary(x => x.Key, x => x.ToList()));
            _areaDimensions = GameController.IngameState.Data.AreaDimensions;
            _processedTerrainData = GameController.IngameState.Data.RawPathfindingData;
            _processedTerrainTargetingData = GameController.IngameState.Data.RawTerrainTargetingData;

            if (Settings.InstanceDumpSettings.AutoDumpOnAreaChange)
            {
                RunDump();
            }

            GenerateMapTexture();
            _clusteredTargetLocations = ClusterTargets();
            StartPathFinding();
        }
    }

    private ConcurrentDictionary<string, List<Room>> GetRooms()
    {
        return new ConcurrentDictionary<string, List<Room>>(GameController.IngameState.Data.AreaGraphs.SelectMany(x => x.Rooms).Select(ToRoom).Where(x => x.Name != null).GroupBy(x => x.Name)
            .ToDictionary(x => x.Key, x => x.ToList()));
    }

    private static string SanitizeAreaName(string name)
    {
        return name.Replace(" ", "_")
            .Replace(":", "")
            .Replace("/", "")
            .Replace("\\", "");
    }

    public override void DrawSettings()
    {
        try { Settings.PathfindingSettings.CurrentZoneName.Value = GameController.Area.CurrentArea.Area.Id; }
        catch { /* area is not available while the settings panel is open */ }
        base.DrawSettings();
    }

    private static readonly List<Color> RainbowColors = new List<Color>
    {
        Color.Red,
        Color.LightGreen,
        Color.White,
        Color.Yellow,
        Color.LightBlue,
        Color.Violet,
        Color.Blue,
        Color.Orange,
        Color.Indigo,
    };

    public override void OnLoad()
    {
        LoadTargets();
        AttachSettingsHooks();
    }

    private void AttachSettingsHooks()
    {
        if (_settingsHooksAttached) return;
        Settings.Reload.OnPressed += ReloadTargets;
        Settings.InstanceDumpSettings.ManualDumpHotkey.OnValueChanged += OnManualDumpHotkeyChanged;
        Settings.MaximumPathCount.OnValueChanged += OnMaximumPathCountChanged;
        Settings.Enable.OnValueChanged += OnEnableChanged;
        Settings.TerrainColor.OnValueChanged += OnTerrainColorChanged;
        Settings.Debug.DrawHeightMap.OnValueChanged += OnBoolMapRenderSettingChanged;
        Settings.Debug.StandardEdgeSettings.SkipEdgeDetector.OnValueChanged += OnBoolMapRenderSettingChanged;
        Settings.Debug.StandardEdgeSettings.SkipNeighborFill.OnValueChanged += OnBoolMapRenderSettingChanged;
        Settings.Debug.StandardEdgeSettings.SkipRecoloring.OnValueChanged += OnBoolMapRenderSettingChanged;
        Settings.Debug.DisableHeightAdjust.OnValueChanged += OnBoolMapRenderSettingChanged;
        Settings.MaximumMapTextureDimension.OnValueChanged += OnMaximumMapTextureDimensionChanged;
        Settings.Debug.AlternativeEdgeMethod.OnValueChanged += OnBoolMapRenderSettingChanged;
        Settings.Debug.AlternativeEdgeSettings.OutlineBlurSigma.OnValueChanged += OnFloatMapRenderSettingChanged;
        Settings.Debug.AlternativeEdgeSettings.OutlineTransitionThreshold.OnValueChanged += OnFloatMapRenderSettingChanged;
        Settings.Debug.AlternativeEdgeSettings.OutlineFeatherWidth.OnValueChanged += OnFloatMapRenderSettingChanged;
        _settingsHooksAttached = true;
    }

    private void ReloadTargets()
    {
        try
        {
            LoadTargets();
            if (GameController?.Area?.CurrentArea is { } area)
                AreaChange(area);
        }
        catch (Exception ex) { DebugWindow.LogError($"Radar reload failed: {ex}"); }
    }

    private void OnManualDumpHotkeyChanged() => Input.RegisterKey(Settings.InstanceDumpSettings.ManualDumpHotkey.Value);
    private void OnMaximumPathCountChanged(object _, int __) => _restartPathFindingRequested = true;
    private void OnTerrainColorChanged(object _, Color __) => _mapTextureRefreshRequested = true;
    private void OnBoolMapRenderSettingChanged(object _, bool __) => _mapTextureRefreshRequested = true;
    private void OnMaximumMapTextureDimensionChanged(object _, int __) => _mapTextureRefreshRequested = true;
    private void OnFloatMapRenderSettingChanged(object _, float __) => _mapTextureRefreshRequested = true;

    private void OnEnableChanged(object _, bool enabled)
    {
        if (enabled)
        {
            _restartPathFindingRequested = false;
            if (GameController?.Area?.CurrentArea is { } area) AreaChange(area);
            return;
        }
        StopPathFinding();
    }

    public override void OnPluginDestroyForHotReload()
    {
        DetachSettingsHooks();
        _restartPathFindingRequested = false;
        _mapTextureRefreshRequested = false;
        _dumpRequested = false;
        StopPathFinding();
        base.OnPluginDestroyForHotReload();
    }

    public override void Dispose()
    {
        DetachSettingsHooks();
        _restartPathFindingRequested = false;
        _mapTextureRefreshRequested = false;
        _dumpRequested = false;
        StopPathFinding();
        base.Dispose();
    }

    private void DetachSettingsHooks()
    {
        if (!_settingsHooksAttached) return;
        Settings.Reload.OnPressed -= ReloadTargets;
        Settings.InstanceDumpSettings.ManualDumpHotkey.OnValueChanged -= OnManualDumpHotkeyChanged;
        Settings.InstanceDumpSettings.ManualDumpButton.OnPressed -= RunDump;
        Settings.MaximumPathCount.OnValueChanged -= OnMaximumPathCountChanged;
        Settings.Enable.OnValueChanged -= OnEnableChanged;
        Settings.TerrainColor.OnValueChanged -= OnTerrainColorChanged;
        Settings.Debug.DrawHeightMap.OnValueChanged -= OnBoolMapRenderSettingChanged;
        Settings.Debug.StandardEdgeSettings.SkipEdgeDetector.OnValueChanged -= OnBoolMapRenderSettingChanged;
        Settings.Debug.StandardEdgeSettings.SkipNeighborFill.OnValueChanged -= OnBoolMapRenderSettingChanged;
        Settings.Debug.StandardEdgeSettings.SkipRecoloring.OnValueChanged -= OnBoolMapRenderSettingChanged;
        Settings.Debug.DisableHeightAdjust.OnValueChanged -= OnBoolMapRenderSettingChanged;
        Settings.MaximumMapTextureDimension.OnValueChanged -= OnMaximumMapTextureDimensionChanged;
        Settings.Debug.AlternativeEdgeMethod.OnValueChanged -= OnBoolMapRenderSettingChanged;
        Settings.Debug.AlternativeEdgeSettings.OutlineBlurSigma.OnValueChanged -= OnFloatMapRenderSettingChanged;
        Settings.Debug.AlternativeEdgeSettings.OutlineTransitionThreshold.OnValueChanged -= OnFloatMapRenderSettingChanged;
        Settings.Debug.AlternativeEdgeSettings.OutlineFeatherWidth.OnValueChanged -= OnFloatMapRenderSettingChanged;
        _settingsHooksAttached = false;
    }

    public override void EntityAdded(Entity entity)
    {
        var positioned = entity.GetComponent<Positioned>();
        if (positioned != null)
        {
            var path = entity.Path;
            if (_currentZoneTargetEntityPaths.FirstOrDefault(x=>x.Item1.IsMatch(path)).x is {} targetDescription)
            {
                bool alreadyContains = false;
                var truncatedPos = positioned.GridPos.Truncate();
                _allTargetLocations.AddOrUpdate(path, _ => [truncatedPos],
                    // ReSharper disable once AssignmentInConditionalExpression
                    (_, l) => (alreadyContains = l.Contains(truncatedPos)) ? l : [..l, truncatedPos]);
                _locationsByPosition.AddOrUpdate(truncatedPos, _ => [path],
                    (_, l) => l.Contains(path) ? l : [..l, path]);
                if (!alreadyContains)
                {
                    var oldValue = _clusteredTargetLocations.GetValueOrDefault(targetDescription.EqualityId);
                    var newValue = _clusteredTargetLocations.AddOrUpdate(targetDescription.EqualityId,
                        _ => ClusterTarget(_targetDescriptionsInArea[targetDescription.EqualityId]),
                        (_, _) => ClusterTarget(_targetDescriptionsInArea[targetDescription.EqualityId]));
                    foreach (var newLocation in newValue.Locations.Except(oldValue?.Locations ?? []))
                    {
                        AddRoute(newLocation, [targetDescription], entity);
                    }
                }
            }
        }
    }

    private Vector2 GetPlayerPosition()
    {
        var player = GameController.Game.IngameState.Data.LocalPlayer;
        var playerPositionComponent = player.GetComponent<Positioned>();
        if (playerPositionComponent == null)
            return new Vector2(0, 0);
        var playerPosition = new Vector2(playerPositionComponent.GridX, playerPositionComponent.GridY);
        return playerPosition;
    }

    public override void Render()
    {
        if (!Settings.Enable)
            return;

        if (_restartPathFindingRequested)
        {
            _restartPathFindingRequested = false;
            RestartPathFinding();
        }

        if (_mapTextureRefreshRequested)
        {
            _mapTextureRefreshRequested = false;
            GenerateMapTexture();
        }

        if (_dumpRequested)
        {
            _dumpRequested = false;
            DumpCurrentAreaInstance();
        }

        if (Settings.InstanceDumpSettings.ManualDumpHotkey.PressedOnce())
        {
            RunDump();
        }

        var ingameUi = GameController.Game.IngameState.IngameUi;
        if (!Settings.Debug.IgnoreFullscreenPanels &&
            ingameUi.FullscreenPanels.Any(x => x.IsVisible))
        {
            return;
        }

        if (!Settings.Debug.IgnoreLargePanels &&
            ingameUi.LargePanels.Any(x => x.IsVisible))
        {
            return;
        }

        _rect = GameController.Window.GetWindowRectangle() with { Location = Vector2.Zero };
        if (!Settings.Debug.DisableDrawRegionLimiting)
        {
            if (ingameUi.OpenRightPanel.IsVisible)
            {
                _rect.Right = ingameUi.OpenRightPanel.GetClientRectCache.Left;
            }

            if (ingameUi.OpenLeftPanel.IsVisible)
            {
                _rect.Left = ingameUi.OpenLeftPanel.GetClientRectCache.Right;
            }
        }

        ImGui.SetNextWindowSize(new Vector2(_rect.Width, _rect.Height));
        ImGui.SetNextWindowPos(new Vector2(_rect.Left, _rect.Top));

        ImGui.Begin("radar_background",
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoBackground);

        _backGroundWindowPtr = ImGui.GetWindowDrawList();
        var map = ingameUi.Map;
        var largeMap = map.LargeMap.AsObject<SubMap>();
        if (largeMap.IsVisible)
        {
            var mapCenter = largeMap.MapCenter + new Vector2(Settings.Debug.MapCenterOffsetX, Settings.Debug.MapCenterOffsetY);
            _mapScale = largeMap.MapScale * Settings.CustomScale;
            DrawLargeMap(mapCenter);
            DrawTargets(mapCenter);
        }

        DrawWorldPaths(largeMap);
        DrawPathLegend();
        ImGui.End();
        DrawRooms();
    }

    private void RunDump()
    {
        // The dump reads GameController/terrain state. Queue it for the main
        // render pass instead of touching host memory from a worker thread.
        _dumpRequested = true;
    }

    private void DumpCurrentAreaInstance()
    {
        try
        {
            var area = GameController?.Area?.CurrentArea;
            if (area == null)
                return;

            var areaId = area.Area.Id;
            var areaName = SanitizeAreaName(area.Area.Name);
            DumpInstanceData($@"{DirectoryFullName}\instance_dumps\{areaId}_{areaName}");
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"Radar dump request failed: {ex}");
        }
    }

    private void DrawRooms()
    {
        if (Settings.PathfindingSettings.ShowRooms)
        {
            var regex = string.IsNullOrEmpty(Settings.PathfindingSettings.TargetNameFilter)
                ? null
                : new Regex(Settings.PathfindingSettings.TargetNameFilter, RegexOptions.IgnoreCase);
            var areaCompositions = GameController.IngameState.Data.AreaGraphs;
            foreach (var composition in areaCompositions)
            {
                foreach (var room in composition.Rooms)
                {
                    if (regex != null && !regex.IsMatch(room.Name))
                    {
                        continue;
                    }
                    var minGrid = new Vector2(room.MinCoord.X * PoeMapExtension.TileToGridConversion, room.MinCoord.Y * PoeMapExtension.TileToGridConversion);
                    var maxGrid = new Vector2(room.MaxCoord.X * PoeMapExtension.TileToGridConversion, room.MaxCoord.Y * PoeMapExtension.TileToGridConversion);

                    var topLeftGrid = new Vector2(minGrid.X, minGrid.Y);
                    var topRightGrid = new Vector2(maxGrid.X, minGrid.Y);
                    var bottomRightGrid = new Vector2(maxGrid.X, maxGrid.Y);
                    var bottomLeftGrid = new Vector2(minGrid.X, maxGrid.Y);

                    var topLeft = Graphics.GridToMap(topLeftGrid, topLeftGrid, VisibleSubMap.Large);
                    var topRight = Graphics.GridToMap(topRightGrid, topRightGrid, VisibleSubMap.Large);
                    var bottomRight = Graphics.GridToMap(bottomRightGrid, bottomRightGrid, VisibleSubMap.Large);
                    var bottomLeft = Graphics.GridToMap(bottomLeftGrid, bottomLeftGrid, VisibleSubMap.Large);

                    var points = new[] { topLeft, topRight, bottomRight, bottomLeft, topLeft };
                    Graphics.DrawPolyLine(points, Color.YellowGreen, 2);

                    var centerGrid = new Vector2((minGrid.X + maxGrid.X) / 2f, (minGrid.Y + maxGrid.Y) / 2f);
                    var centerScreen = Graphics.GridToMap(centerGrid, centerGrid, VisibleSubMap.Large);
                    Graphics.DrawTextWithBackground(room.Name.Substring("Metadata/Terrain/".Length), centerScreen, Color.Red, FontAlign.Center, Color.Black);
                }
            }
        }
    }

    private void DrawWorldPaths(SubMap largeMap)
    {
        if (largeMap == null || _heightData == null)
            return;
        if (Settings.PathfindingSettings.WorldPathSettings.ShowPathsToTargets &&
            (!largeMap.IsVisible || !Settings.PathfindingSettings.WorldPathSettings.ShowPathsToTargetsOnlyWithClosedMap))
        {
            var player = GameController.Game.IngameState.Data.LocalPlayer;
            var playerRender = player?.GetComponent<ExileCore2.PoEMemory.Components.Render>();
            if (playerRender == null)
                return;
            var initPos = GameController.IngameState.Camera.WorldToScreen(playerRender.Pos with { Z = playerRender.UnclampedHeight });
            if (!IsFinite(initPos)) return;
            foreach (var (route, offsetAmount) in _routes.Values
                         .Where(x => x?.Path is { Count: > 0 })
                         .GroupBy(x => x.Path.Count < 2 ? 0 : (x.Path[1] - x.Path[0]) switch { var diff => Math.Atan2(diff.Y, diff.X) })
                         .SelectMany(group => group.Select((route, i) => (route, i - group.Count() / 2.0f + 0.5f))))
            {
                var p0 = initPos;
                var p0WithOffset = p0;
                var i = 0;
                foreach (var elem in route.Path)
                {
                    if (!InBounds(elem, _heightData) || !float.IsFinite(_heightData[elem.Y][elem.X]))
                    {
                        p0WithOffset = p0;
                        continue;
                    }
                    var p1 = GameController.IngameState.Camera.WorldToScreen(
                        new Vector3(elem.X * GridToWorldMultiplier, elem.Y * GridToWorldMultiplier, _heightData[elem.Y][elem.X]));
                    if (!IsFinite(p1)) { p0WithOffset = p0; continue; }
                    var offsetDirection = Settings.PathfindingSettings.WorldPathSettings.OffsetPaths
                        ? (p1 - p0) switch { var s when s.LengthSquared() > 0.0001f => new Vector2(s.Y, -s.X) / s.Length(), _ => Vector2.Zero }
                        : Vector2.Zero;
                    var finalOffset = offsetDirection * offsetAmount * Settings.PathfindingSettings.WorldPathSettings.PathThickness;
                    p0 = p1;
                    p1 += finalOffset;
                    if (++i % Settings.PathfindingSettings.WorldPathSettings.DrawEveryNthSegment == 0)
                    {
                        if (_rect.Contains(p0WithOffset) || _rect.Contains(p1))
                        {
                            Graphics.DrawLine(p0WithOffset, p1, Settings.PathfindingSettings.WorldPathSettings.PathThickness, route.WorldColor());
                        }
                        else
                        {
                            break;
                        }
                    }

                    p0WithOffset = p1;
                }
            }
        }
    }

	private void DrawPathLegend()
	{
	    if (!Settings.PathfindingSettings.ShowPathLegend) return;
	    if (_clusteredTargetLocations.Count == 0) return;
	
	    var padding = 10f;
	    var lineHeight = 22f;
	    var boxSize = 14f;
	    var legendWidth = 220f;
	    var startX = Settings.PathfindingSettings.PathLegendPositionX.Value;
	    var startY = Settings.PathfindingSettings.PathLegendPositionY.Value;
	
	    // Match each target to its correct route color via location
	    var entries = _clusteredTargetLocations.Values
	        .SelectMany(t => t.Locations.Select(loc => (t.DisplayName, loc)))
	        .Select(x => {
	            var routeKey = new Vector2(x.loc.X, x.loc.Y);
	            var color = _routes.TryGetValue(routeKey, out var route) 
	                ? route.MapColor() 
	                : Color.White;
	            return (x.DisplayName, color);
	        })
	        .DistinctBy(x => x.DisplayName)
	        .ToList();
	
	    if (entries.Count == 0) return;
	
	    var bgHeight = padding * 2 + entries.Count * lineHeight;
	
	    _backGroundWindowPtr.AddRectFilled(
	        new Vector2(startX - padding, startY - padding),
	        new Vector2(startX + legendWidth, startY + bgHeight),
	        Color.FromArgb(180, 0, 0, 0).ToImgui());
	
	    for (var i = 0; i < entries.Count; i++)
	    {
	        var (name, color) = entries[i];
	        var y = startY + i * lineHeight;
	
	        _backGroundWindowPtr.AddRectFilled(
	            new Vector2(startX, y + 2),
	            new Vector2(startX + boxSize, y + boxSize + 2),
	            color.ToImgui());
	
	        _backGroundWindowPtr.AddText(
	            new Vector2(startX + boxSize + 8, y),
	            Color.White.ToImgui(),
	            name);
	    }
	}

    private void DrawBox(Vector2 p0, Vector2 p1, Color color)
    {
        _backGroundWindowPtr.AddRectFilled(p0, p1, color.ToImgui());
    }

    private void DrawText(string text, Vector2 pos, Color color)
    {
        _backGroundWindowPtr.AddText(pos, color.ToImgui(), text);
    }

    private Vector2 TranslateGridDeltaToMapDelta(Vector2 delta, float deltaZ)
    {
        if (!IsFinite(delta) || !float.IsFinite(deltaZ) || !double.IsFinite(_mapScale) || _mapScale <= 0)
            return new Vector2(float.NaN, float.NaN);
        deltaZ /= GridToWorldMultiplier; //z is normally "world" units, translate to grid
        return (float)_mapScale * new Vector2((delta.X - delta.Y) * CameraAngleCos, (deltaZ - (delta.X + delta.Y)) * CameraAngleSin);
    }

    private void DrawLargeMap(Vector2 mapCenter)
    {
        if (!Settings.DrawWalkableMap || !Graphics.HasImage(TextureName) || _areaDimensions == null ||
            !IsFinite(mapCenter) || !double.IsFinite(_mapScale) || _mapScale <= 0)
            return;
        var player = GameController.Game.IngameState.Data.LocalPlayer;
        var playerRender = player.GetComponent<ExileCore2.PoEMemory.Components.Render>();
        if (playerRender == null)
            return;
        var rectangleF = new RectangleF(-playerRender.GridPos().X, -playerRender.GridPos().Y, _areaDimensions.Value.X, _areaDimensions.Value.Y);
        var playerHeight = -playerRender.UnclampedHeight;
        if (!float.IsFinite(playerHeight) || !IsFinite(playerRender.GridPos())) return;
        var p1 = mapCenter + TranslateGridDeltaToMapDelta(new Vector2(rectangleF.Left, rectangleF.Top), playerHeight);
        var p2 = mapCenter + TranslateGridDeltaToMapDelta(new Vector2(rectangleF.Right, rectangleF.Top), playerHeight);
        var p3 = mapCenter + TranslateGridDeltaToMapDelta(new Vector2(rectangleF.Right, rectangleF.Bottom), playerHeight);
        var p4 = mapCenter + TranslateGridDeltaToMapDelta(new Vector2(rectangleF.Left, rectangleF.Bottom), playerHeight);
        if (IsFinite(p1) && IsFinite(p2) && IsFinite(p3) && IsFinite(p4))
            _backGroundWindowPtr.AddImageQuad(Graphics.GetTextureId(TextureName), p1, p2, p3, p4);
    }

    private void DrawTargets(Vector2 mapCenter)
    {
        if (_heightData == null || !IsFinite(mapCenter) || !double.IsFinite(_mapScale) || _mapScale <= 0)
            return;
        var color = Settings.PathfindingSettings.TargetNameColor.Value;
        var player = GameController.Game.IngameState.Data.LocalPlayer;
        var playerRender = player.GetComponent<ExileCore2.PoEMemory.Components.Render>();
        if (playerRender == null)
            return;
        var playerPosition = new Vector2(playerRender.GridPos().X, playerRender.GridPos().Y);
        var playerHeight = -playerRender.UnclampedHeight;
        if (!IsFinite(playerPosition) || !float.IsFinite(playerHeight)) return;
        var ithElement = 0;
        if (Settings.PathfindingSettings.ShowPathsToTargetsOnMap)
        {
            foreach (var route in _routes.Values)
            {
                ithElement++;
                ithElement %= 5;
                foreach (var elem in route.Path.Skip(ithElement).GetEveryNth(5))
                {
                    if (!InBounds(elem, _heightData) || !float.IsFinite(_heightData[elem.Y][elem.X])) continue;
                    var mapDelta = TranslateGridDeltaToMapDelta(new Vector2(elem.X, elem.Y) - playerPosition, playerHeight + _heightData[elem.Y][elem.X]);
                    var mapPos = mapCenter + mapDelta;
                    if (IsFinite(mapPos)) DrawBox(mapPos - new Vector2(2, 2), mapPos + new Vector2(2, 2), route.MapColor());
                }
            }
        }

        if (Settings.PathfindingSettings.ShowAllTargets)
        {
            // Compile the filter once per frame instead of once per location: this loop can run
            // over thousands of _locationsByPosition entries and previously allocated a fresh Regex each.
            var nameFilter = string.IsNullOrEmpty(Settings.PathfindingSettings.TargetNameFilter)
                ? null
                : new Regex(Settings.PathfindingSettings.TargetNameFilter);
            var maxTargetNameCount = Settings.PathfindingSettings.MaxTargetNameCount;

            bool TargetFilter(string t) =>
                (nameFilter?.IsMatch(t) ?? true) &&
                _allTargetLocations.GetValueOrDefault(t) is { } list && list.Count <= maxTargetNameCount;

            foreach (var (location, texts) in _locationsByPosition)
            {
                var text = string.Join("\n", texts.Distinct().Where(TargetFilter));
                if (string.IsNullOrEmpty(text) || !InBounds(location, _heightData) || !float.IsFinite(_heightData[location.Y][location.X])) continue;
                var textOffset = Graphics.MeasureText(text) / 2f;
                var mapDelta = TranslateGridDeltaToMapDelta(location - playerPosition, playerHeight + _heightData[location.Y][location.X]);
                var mapPos = mapCenter + mapDelta;
                if (!IsFinite(mapPos) || !IsFinite(textOffset)) continue;
                if (Settings.PathfindingSettings.EnableTargetNameBackground)
                    DrawBox(mapPos - textOffset, mapPos + textOffset, Color.Black);
                DrawText(text, mapPos - textOffset, color);
            }
        }
        else if (Settings.PathfindingSettings.ShowSelectedTargets)
        {
            foreach (var (_, description) in _clusteredTargetLocations)
            {
                foreach (var clusterPosition in description.Locations)
                {
                    float clusterHeight = 0;
                    if (InBounds(clusterPosition, _heightData))
                        clusterHeight = _heightData[(int)clusterPosition.Y][(int)clusterPosition.X];
                    if (!float.IsFinite(clusterHeight)) continue;
                    var text = description.DisplayName;
                    var textOffset = Graphics.MeasureText(text) / 2f;
                    var mapDelta = TranslateGridDeltaToMapDelta(clusterPosition - playerPosition, playerHeight + clusterHeight);
                    var mapPos = mapCenter + mapDelta;
                    if (!IsFinite(mapPos) || !IsFinite(textOffset)) continue;
                    if (Settings.PathfindingSettings.EnableTargetNameBackground)
                        DrawBox(mapPos - textOffset, mapPos + textOffset, Color.Black);
                    DrawText(text, mapPos - textOffset, color);
                }
            }
        }
    }

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool InBounds(Vector2i point, float[][] data)
        => data != null && point.Y >= 0 && point.Y < data.Length && data[point.Y] != null &&
           point.X >= 0 && point.X < data[point.Y].Length;

    private static bool InBounds(Vector2 point, float[][] data)
        => data != null && IsFinite(point) && point.X >= 0 && point.Y >= 0 &&
           point.Y < data.Length && data[(int)point.Y] != null && point.X < data[(int)point.Y].Length;
}
