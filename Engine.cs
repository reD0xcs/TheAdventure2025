using System.Reflection;
using System.Text.Json;
using Silk.NET.Maths;
using TheAdventure.Models;
using TheAdventure.Models.Data;
using TheAdventure.Scripting;

namespace TheAdventure;

public class Engine
{
    private readonly GameRenderer _renderer;
    private readonly Input _input;
    private readonly ScriptEngine _scriptEngine = new();

    private int _gameOverTextureId;
    private bool _isGameOver = false;
    private DateTimeOffset _gameOverTime;
    private bool _showingGameOver = false;

    // Day/Night Cycle
    private bool _isDayTime = true;
    private DateTimeOffset _lastCycleChange = DateTimeOffset.Now;
    private float _nightDimAlpha = 0.7f;
    private float _currentCycleProgress = 0f;
    private const float DayLength = 10f;
    private const float NightLength = 10f;

    // HUD Elements
    private const int HudIconSize = 48;
    private const int HudPadding = 20;
    private int _sunTextureId;
    private int _moonTextureId;
    private TextureData _sunTextureData;
    private TextureData _moonTextureData;
	private TextureData _gameOverTextureData;

    private readonly Dictionary<int, GameObject> _gameObjects = new();
    private readonly Dictionary<string, TileSet> _loadedTileSets = new();
    private readonly Dictionary<int, Tile> _tileIdMap = new();

    private Level _currentLevel = new();
    private PlayerObject? _player;
    private DateTimeOffset _lastUpdate = DateTimeOffset.Now;

    public Engine(GameRenderer renderer, Input input)
    {
        _renderer = renderer;
        _input = input;
        _input.OnMouseClick += (_, coords) => AddBomb(coords.x, coords.y);
        SetupWorld();
    }

    public void SetupWorld()
{
    // Clear existing textures and objects
    _gameObjects.Clear();
    _loadedTileSets.Clear();
    _tileIdMap.Clear();
    
    _gameOverTextureId = _renderer.LoadTexture(Path.Combine("Assets", "game_over.png"), out _gameOverTextureData);
    _isGameOver = false;
    _showingGameOver = false;
    _player = new(SpriteSheet.Load(_renderer, "Player.json", "Assets"), 100, 100);
    _player.OnGameOver += () => _isGameOver = true;

    var levelContent = File.ReadAllText(Path.Combine("Assets", "terrain.tmj"));
    var level = JsonSerializer.Deserialize<Level>(levelContent) ?? throw new Exception("Failed to load level");

    foreach (var tileSetRef in level.TileSets)
    {
        var tileSetContent = File.ReadAllText(Path.Combine("Assets", tileSetRef.Source));
        var tileSet = JsonSerializer.Deserialize<TileSet>(tileSetContent) ?? throw new Exception("Failed to load tile set");

        foreach (var tile in tileSet.Tiles)
        {
            // Skip if tile ID already exists
            if (tile.Id.HasValue && !_tileIdMap.ContainsKey(tile.Id.Value))
            {
                tile.TextureId = _renderer.LoadTexture(Path.Combine("Assets", tile.Image), out _);
                _tileIdMap.Add(tile.Id.Value, tile);
            }
        }
        _loadedTileSets.Add(tileSet.Name, tileSet);
    }

    if (level.Width == null || level.Height == null || level.TileWidth == null || level.TileHeight == null)
        throw new Exception("Invalid level dimensions");

    _renderer.SetWorldBounds(new Rectangle<int>(0, 0, 
        level.Width.Value * level.TileWidth.Value,
        level.Height.Value * level.TileHeight.Value));

    _currentLevel = level;
    _scriptEngine.LoadAll(Path.Combine("Assets", "Scripts"));
    
    // Load HUD textures
    _sunTextureId = _renderer.LoadTexture(Path.Combine("Assets", "sun.png"), out _sunTextureData);
    _moonTextureId = _renderer.LoadTexture(Path.Combine("Assets", "moon.png"), out _moonTextureData);
}

    private void UpdateDayNightCycle()
    {
        var now = DateTimeOffset.Now;
        var elapsed = (now - _lastCycleChange).TotalSeconds;
        float cycleLength = _isDayTime ? DayLength : NightLength;
        
        _currentCycleProgress = (float)(elapsed / cycleLength);
        
        if (_currentCycleProgress >= 1f)
        {
            _isDayTime = !_isDayTime;
            _lastCycleChange = now;
            _currentCycleProgress = 0f;
        }
    }

    private void RenderDayNightIcon()
    {
        var (screenWidth, _) = _renderer.GetScreenDimensions();
        var position = new Vector2D<int>(screenWidth - HudIconSize - HudPadding, HudPadding);

        var textureId = _isDayTime ? _sunTextureId : _moonTextureId;
        var textureData = _isDayTime ? _sunTextureData : _moonTextureData;
        
        var srcRect = new Rectangle<int>(0, 0, textureData.Width, textureData.Height);
        var dstRect = new Rectangle<int>(position.X, position.Y, HudIconSize, HudIconSize);
        _renderer.RenderTextureScreenSpace(textureId, srcRect, dstRect);
    }

    public void ProcessFrame()
    {
        if (_isGameOver)
        {
            if (!_showingGameOver)
            {
                _gameOverTime = DateTimeOffset.Now;
                _showingGameOver = true;
            }
            else if ((DateTimeOffset.Now - _gameOverTime).TotalSeconds >= 5)
            {
                ResetGame();
            }
            return;
        }
    
        var currentTime = DateTimeOffset.Now;
        var msSinceLastFrame = (currentTime - _lastUpdate).TotalMilliseconds;
        _lastUpdate = currentTime;

        UpdateDayNightCycle();
        
        if (_player == null) return;

        double up = _input.IsUpPressed() ? 1.0 : 0.0;
        double down = _input.IsDownPressed() ? 1.0 : 0.0;
        double left = _input.IsLeftPressed() ? 1.0 : 0.0;
        double right = _input.IsRightPressed() ? 1.0 : 0.0;
        bool isAttacking = _input.IsKeyAPressed() && (up + down + left + right <= 1);

        _player.UpdatePosition(up, down, left, right, 48, 48, msSinceLastFrame);
        if (isAttacking) _player.Attack();
        
        _scriptEngine.ExecuteAll(this);

        if (_input.IsKeyBPressed())
        {
            AddBomb(_player.Position.X, _player.Position.Y, false);
        }
    }

    public void RenderFrame()
    {
        _renderer.SetDrawColor(0, 0, 0, 255);
        _renderer.ClearScreen();

        var playerPosition = _player!.Position;
        _renderer.CameraLookAt(playerPosition.X, playerPosition.Y);

        RenderTerrain();
        RenderAllObjects();

        if (!_isGameOver)
        {
            float darkness = 0f;
            if (_isDayTime)
            {
                if (_currentCycleProgress > 0.75f) 
                {
                    darkness = (_currentCycleProgress - 0.75f) * 4f;
                }
            }
            else
            {
                if (_currentCycleProgress <= 0.25f)
                {
                    darkness = 1f;
                }
                else if (_currentCycleProgress > 0.75f)
                {
                    darkness = 1f - ((_currentCycleProgress - 0.75f) * 4f);
                }
                else
                {
                    darkness = 1f;
                }
            }

            if (darkness > 0f)
            {
                _renderer.ApplyDimEffect(darkness * _nightDimAlpha);
            }

            RenderDayNightIcon();
        }
        else if (_showingGameOver)
        {
            _renderer.ApplyDimEffect(0.7f);
            RenderGameOverScreen();
        }

        _renderer.PresentFrame();
    }

    private void RenderGameOverScreen()
{
    var (screenWidth, screenHeight) = _renderer.GetScreenDimensions();
    
    int width = _gameOverTextureData.Width;
    int height = _gameOverTextureData.Height;
    int x = (screenWidth - width) / 2;
    int y = (screenHeight - height) / 2;
    
    var srcRect = new Rectangle<int>(0, 0, width, height);
    var dstRect = new Rectangle<int>(x, y, width, height);
    _renderer.RenderTextureScreenSpace(_gameOverTextureId, srcRect, dstRect);
}

    public void RenderAllObjects()
    {
        var toRemove = new List<int>();
        foreach (var gameObject in GetRenderables())
        {
            gameObject.Render(_renderer);
            if (gameObject is TemporaryGameObject { IsExpired: true } tempGameObject)
            {
                toRemove.Add(tempGameObject.Id);
            }
        }

        foreach (var id in toRemove)
        {
            _gameObjects.Remove(id, out var gameObject);
            if (_player == null) continue;

            var tempGameObject = (TemporaryGameObject)gameObject!;
            var deltaX = Math.Abs(_player.Position.X - tempGameObject.Position.X);
            var deltaY = Math.Abs(_player.Position.Y - tempGameObject.Position.Y);
            if (deltaX < 32 && deltaY < 32)
            {
                _player.GameOver();
            }
        }

        _player?.Render(_renderer);
    }

    public void ResetGame()
{
    // Clear all game objects and textures
    _gameObjects.Clear();
    _loadedTileSets.Clear();
    _tileIdMap.Clear();
    
    // Reset game state
    _isGameOver = false;
    _showingGameOver = false;
    
    // Setup a fresh world
    SetupWorld();
}

    public void RenderTerrain()
    {
        foreach (var currentLayer in _currentLevel.Layers)
        {
            for (int i = 0; i < _currentLevel.Width; ++i)
            {
                for (int j = 0; j < _currentLevel.Height; ++j)
                {
                    int? dataIndex = j * currentLayer.Width + i;
                    if (dataIndex == null) continue;

                    var currentTileId = currentLayer.Data[dataIndex.Value] - 1;
                    if (currentTileId == null) continue;

                    var currentTile = _tileIdMap[currentTileId.Value];
                    var tileWidth = currentTile.ImageWidth ?? 0;
                    var tileHeight = currentTile.ImageHeight ?? 0;

                    var sourceRect = new Rectangle<int>(0, 0, tileWidth, tileHeight);
                    var destRect = new Rectangle<int>(i * tileWidth, j * tileHeight, tileWidth, tileHeight);
                    _renderer.RenderTexture(currentTile.TextureId, sourceRect, destRect);
                }
            }
        }
    }

    public IEnumerable<RenderableGameObject> GetRenderables()
    {
        foreach (var gameObject in _gameObjects.Values)
        {
            if (gameObject is RenderableGameObject renderableGameObject)
            {
                yield return renderableGameObject;
            }
        }
    }

    public (int X, int Y) GetPlayerPosition()
    {
        return _player!.Position;
    }

    public void AddBomb(int X, int Y, bool translateCoordinates = true)
    {
        var worldCoords = translateCoordinates ? _renderer.ToWorldCoordinates(X, Y) : new Vector2D<int>(X, Y);

        SpriteSheet spriteSheet = SpriteSheet.Load(_renderer, "BombExploding.json", "Assets");
        spriteSheet.ActivateAnimation("Explode");

        TemporaryGameObject bomb = new(spriteSheet, 2.1, (worldCoords.X, worldCoords.Y));
        _gameObjects.Add(bomb.Id, bomb);
    }
}