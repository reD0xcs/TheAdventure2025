using Silk.NET.Maths;
using Silk.NET.SDL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TheAdventure.Models;
using Point = Silk.NET.SDL.Point;
using Rectangle = Silk.NET.Maths.Rectangle<int>;

namespace TheAdventure;

public unsafe class GameRenderer
{
    private readonly Sdl _sdl;
    private readonly Renderer* _renderer;
    private readonly GameWindow _window;
    private readonly Camera _camera;

    private readonly Dictionary<int, IntPtr> _texturePointers = new();
    private readonly Dictionary<int, TextureData> _textureData = new();
    private int _textureId;
    
    private Rectangle _savedViewport;
    private (float X, float Y) _savedScale;

    
    
    public GameRenderer(Sdl sdl, GameWindow window)
    {
        _sdl = sdl;
        _renderer = (Renderer*)window.CreateRenderer();
        _sdl.SetRenderDrawBlendMode(_renderer, BlendMode.Blend);
        _window = window;
        var windowSize = window.Size;
        _camera = new Camera(windowSize.Width, windowSize.Height);
    }

    public void SetWorldBounds(Rectangle<int> bounds)
    {
        _camera.SetWorldBounds(bounds);
    }

    public void CameraLookAt(int x, int y)
    {
        _camera.LookAt(x, y);
    }

    public (int Width, int Height) GetScreenDimensions()
    {
        return (_camera.Width, _camera.Height);
    }

    public void SaveState()
    {
        unsafe
        {
            // Save viewport
            fixed (Rectangle* viewportPtr = &_savedViewport)
            {
                _sdl.RenderGetViewport(_renderer, viewportPtr);
            }
            
            // Save scale
            float scaleX, scaleY;
            _sdl.RenderGetScale(_renderer, &scaleX, &scaleY);
            _savedScale = (scaleX, scaleY);
        }
    }

    public void RestoreState()
    {
        // Restore viewport (using 'in' for readonly reference)
        _sdl.RenderSetViewport(_renderer, in _savedViewport);
        
        // Restore scale
        _sdl.RenderSetScale(_renderer, _savedScale.X, _savedScale.Y);
    }

    public void ResetView()
    {
        // Create temporary variable for viewport to satisfy 'ref readonly'
        var viewport = new Rectangle(0, 0, _camera.Width, _camera.Height);
        _sdl.RenderSetViewport(_renderer, in viewport);
        
        _sdl.RenderSetScale(_renderer, 1.0f, 1.0f);
    }

    public void ApplyDimEffect(float alpha)
    {
        // Create a semi-transparent black rectangle over the whole screen
        var dimRect = new Rectangle(0, 0, _camera.Width, _camera.Height);
        
        // Set draw color to black with specified alpha
        _sdl.SetRenderDrawColor(_renderer, 0, 0, 0, (byte)(alpha * 255));
        _sdl.SetRenderDrawBlendMode(_renderer, BlendMode.Blend);
        _sdl.RenderFillRect(_renderer, in dimRect);
    }
    
    public void RenderTextureScreenSpace(int textureId, Rectangle src, Rectangle dst)
    {
        if (!_texturePointers.TryGetValue(textureId, out var imageTexture)) return;

        SaveState();
        ResetView();
        
        // Create local copies to satisfy 'ref readonly' parameters
        var srcCopy = src;
        var dstCopy = dst;
        _sdl.RenderCopy(_renderer, (Texture*)imageTexture, in srcCopy, in dstCopy);
        
        RestoreState();
    }

    public void ClearTextures()
    {
        foreach (var texturePtr in _texturePointers.Values)
        {
            _sdl.DestroyTexture((Texture*)texturePtr);
        }
        _texturePointers.Clear();
        _textureData.Clear();
        _textureId = 0; // Reset the counter
    }
    
    public int LoadTexture(string fileName, out TextureData textureInfo)
    {
        var textureId = _textureId++; // Increment first to start from 1
        Console.WriteLine($"Loading texture {fileName} with ID {textureId}");
        using (var fStream = new FileStream(fileName, FileMode.Open))
        {
            var image = Image.Load<Rgba32>(fStream);
            textureInfo = new TextureData()
            {
                Width = image.Width,
                Height = image.Height
            };
            var imageRawData = new byte[textureInfo.Width * textureInfo.Height * 4];
            image.CopyPixelDataTo(imageRawData.AsSpan());
            fixed (byte* data = imageRawData)
            {
                var imageSurface = _sdl.CreateRGBSurfaceWithFormatFrom(data, textureInfo.Width,
                    textureInfo.Height, 8, textureInfo.Width * 4, (uint)PixelFormatEnum.Rgba32);
                if (imageSurface == null)
                {
                    throw new Exception("Failed to create surface from image data.");
                }
            
                var imageTexture = _sdl.CreateTextureFromSurface(_renderer, imageSurface);
                if (imageTexture == null)
                {
                    _sdl.FreeSurface(imageSurface);
                    throw new Exception("Failed to create texture from surface.");
                }
            
                _sdl.FreeSurface(imageSurface);
            
                _textureData[textureId] = textureInfo;
                _texturePointers[textureId] = (IntPtr)imageTexture;
            }
        }
        return textureId;
    }

    public void RenderTexture(int textureId, Rectangle src, Rectangle dst,
        RendererFlip flip = RendererFlip.None, double angle = 0.0, Point center = default)
    {
        if (_texturePointers.TryGetValue(textureId, out var imageTexture))
        {
            var translatedDst = _camera.ToScreenCoordinates(dst);
            _sdl.RenderCopyEx(_renderer, (Texture*)imageTexture, in src,
                in translatedDst,
                angle,
                in center, flip);
        }
    }

    public Vector2D<int> ToWorldCoordinates(int x, int y)
    {
        return _camera.ToWorldCoordinates(new Vector2D<int>(x, y));
    }

    public void SetDrawColor(byte r, byte g, byte b, byte a)
    {
        _sdl.SetRenderDrawColor(_renderer, r, g, b, a);
    }

    public void ClearScreen()
    {
        _sdl.RenderClear(_renderer);
    }

    public void PresentFrame()
    {
        _sdl.RenderPresent(_renderer);
    }
}