using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using static KyxEngine.DataCapacities.Byte;

namespace AtlasSystem
{
    public static class AtlasSystem
    {
        private static bool _initialized = false;
        private static bool _debug = false;
        private static GraphicsDevice _graphicsDevice;

        private static Sheet[] _sheets;
        private static Sprite[] _sprites;
        private static readonly Dictionary<string, Sprite> _spriteByName = new();
        private static readonly ConcurrentQueue<Sheet> _loadQueue = new();

        public static int Version { get; private set; }
        public static long Size { get; private set; } = 0;
        public static long Capacity { get; private set; } = GigaByte;

        static AtlasSystem()
        {
            // Loop until a thread dedicated to cache system is created
            while (!ThreadPool.QueueUserWorkItem((o) =>
            {
                // Set this thread to lowest prioroty as to not compete with other tasks
                Thread.CurrentThread.Priority = ThreadPriority.Lowest;

                // Variables isolated from rest of code base, used for cache management
                Sheet _head = null, _tail = null;

                bool RemoveTail()
                {
                    if (_tail != null)
                    {
                        Sheet crntTail = _tail;
                        _tail = _tail._prev;
                        crntTail._prev = crntTail._next = null;
                        if (_debug)
                            Console.WriteLine($"Sheet: {crntTail._sheetFile}\n\tUnloading.");
                        crntTail._sheet?.Dispose();
                        return true;
                    }
                    return false;
                }

                void AlignTail(long spare = 0)
                {
                    while (Size + spare > Capacity)
                        if (!RemoveTail())
                            throw new Exception("Not enough VRAM");
                }

                // Adds a long running loop (acts as an Actor to manage the Sheet cache)
                while (true)
                {
                    if (_loadQueue.IsEmpty)
                    {
                        // even when in context, sleep for at least 1ms to be scheduled again
                        Thread.Sleep(1);
                    }
                    else
                    {
                        if (_loadQueue.TryDequeue(out Sheet crntSheet))
                        {
                            // if sheet not loaded, load the sheet
                            if (crntSheet._sheet == null)
                            {
                                AlignTail(crntSheet.Size);
                                using FileStream fs = new(crntSheet._sheetFile, FileMode.Open);
                                while (fs.Seek(8, SeekOrigin.Begin) != 8) ;
                                crntSheet._sheet = Texture2D.FromStream(_graphicsDevice, fs);
                            }

                            // if current sheet is the tail then 
                            if (_tail == crntSheet)
                            {
                                _tail = crntSheet._prev;
                                crntSheet._prev = null;
                                crntSheet._next = _head;
                                _head = crntSheet;
                            }
                            else
                            {
                                // if current sheet is the head, no changes needed
                                if (_head != crntSheet)
                                {
                                    // Cross reassociation
                                    if (crntSheet._prev != null)
                                        crntSheet._prev._next = crntSheet._next;
                                    if (crntSheet._next != null)
                                        crntSheet._next._prev = crntSheet._prev;
                                    // Set to head
                                    crntSheet._prev = null;
                                    crntSheet._next = _head;
                                    _head = crntSheet;
                                }
                            }

                            // Resolve the tail if necessary
                            if (_tail == null)
                            {
                                _tail = _head;
                                while (_tail._next != null)
                                    _tail = _tail._next;
                            }
                        }
                    }
                }
            })) ;
        }

        private class Sheet // 8 + N bytes each
        {
            internal readonly string _sheetFile;
            internal Texture2D _sheet;
            internal Sheet _prev;
            internal Sheet _next;
            internal int _index;

            internal long Size { get; init; }

            public Sheet(string fileName)
            {
                using FileStream fs = new(_sheetFile, FileMode.Open);

                byte[] buffer = new byte[16];
                fs.Read(buffer, 0, buffer.Length);

                // Read the Magic
                if (System.Text.Encoding.UTF8.GetString(buffer, 0, 8) == "KYXSHEET")
                {
                    // Set sheet file location
                    _sheetFile = fileName;

                    // Read the Sheet index
                    _index = BitConverter.ToInt32(buffer, 8);
                    _sheets[_index] = this;

                    // Read VRAM Size 
                    Size = BitConverter.ToInt32(buffer, 12);

                    if (_debug)
                        Console.WriteLine($"Sheet: {fileName}\n\tLoaded({_index})");
                }
                else
                {
                    if (_debug)
                        Console.WriteLine($"Sheet: {fileName}\n\tSkipped");
                }
            }

            // Return the Atlas (texture2D)
            public static implicit operator Texture2D(Sheet sheet)
            {
                _loadQueue.Enqueue(sheet);
                if (_debug)
                    Console.WriteLine($"Sheet: {sheet._index}\n\tQueued");
                return sheet._sheet;
            }
        }

        public class Sprite : IEnumerable // 13 + N bytes each
        {
            private readonly SpriteFragment[] _fragments;

            public string Name { get; init; }
            public int Width { get; init; }
            public int Height { get; init; }

            public Sprite(Stream stream)
            {
                byte[] buffer = new byte[13];
                stream.Read(buffer, 0, buffer.Length);

                // Read Width bytes
                Width = BitConverter.ToInt32(buffer, 0);

                // Read Height bytes
                Height = BitConverter.ToInt32(buffer, 4);

                // Read number of fragments
                _fragments = new SpriteFragment[BitConverter.ToInt32(buffer, 8)];

                // Read Name bytes
                buffer = new byte[buffer[12]];
                stream.Read(buffer, 0, buffer.Length);
                Name = System.Text.Encoding.UTF8.GetString(buffer);

                if (_debug)
                    Console.WriteLine($"Sprite: {Name} {Width}x{Height}\n\tLoaded.");

                // Construct Fragments
                for (int i = 0; i < _fragments.Length; i++)
                    _fragments[i] = new(stream, Name, i);
            }

            public void Allocate() // Readies the Sheets by loading them into VRAM, no guarantee that they will be loaded upon use
            {
                foreach (SpriteFragment sf in _fragments)
                    _ = (Texture2D)_sheets[sf.Sheet];
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                foreach (SpriteFragment fragment in _fragments)
                    yield return fragment;
            }
        }

        private class SpriteFragment // 28 bytes each
        {
            // Location relative to sprite
            public Vector2 Position { get; init; }

            // Location and size relative to sheet
            public Rectangle FragmentRectangle { get; init; }

            // Location relative to sheet
            public int Sheet { get; init; }

            public SpriteFragment(Stream stream, string name, int index)
            {
                byte[] buffer = new byte[28];
                stream.Read(buffer, 0, buffer.Length);
                Position = new Vector2(BitConverter.ToSingle(buffer, 0), BitConverter.ToSingle(buffer, 4));
                FragmentRectangle = new Rectangle(BitConverter.ToInt32(buffer, 8), BitConverter.ToInt32(buffer, 12), BitConverter.ToInt32(buffer, 16), BitConverter.ToInt32(buffer, 20));
                Sheet = BitConverter.ToInt32(buffer, 8);
                if (_debug)
                    Console.WriteLine($"SpriteFragment: {name}({index})\n\tLoaded.");
            }
        }

        // Load sprites and atlases
        public static void Init(GraphicsDevice graphicsDevice, string workingFolder, long capacity = GigaByte, bool debug = false)
        {
            if (_initialized)
                throw new Exception("AtlasSystem already initialized");

            _graphicsDevice = graphicsDevice;
            _debug = debug;

            if (_debug)
                Console.WriteLine($"Initialzing AtlasSystem from {workingFolder}, Capacity = {capacity}");

            string folderPath = Path.Combine(workingFolder, "/Atlas");
            string filePath = Path.Combine(folderPath, "/Meta.atlas");

            if (File.Exists(filePath))
            {
                byte[] buffer;
                using FileStream fs = new(filePath, FileMode.Open);

                // Read Header
                buffer = new byte[17];
                fs.Read(buffer, 0, buffer.Length);

                // If MAGIC header is correct
                if (System.Text.Encoding.UTF8.GetString(buffer, 0, 8) == "KYXATLAS")
                {
                    // Read how many sprites there are
                    _sprites = new Sprite[BitConverter.ToInt32(buffer, 8)];

                    // Read how many sheets there are
                    _sheets = new Sheet[BitConverter.ToInt32(buffer, 12)];

                    // Read version number
                    Version = buffer[16];

                    // Construct Sprite objects
                    for (int i = 0; i < _sprites.Length; i++)
                    {
                        Sprite newSprite = new(fs);
                        _spriteByName.Add(newSprite.Name, newSprite);
                        _sprites[i] = newSprite;
                    }

                    // Initialize all found Sheet files
                    string[] sheetPaths = Directory.GetFiles(folderPath, "*.kyxsheet");
                    foreach (string path in sheetPaths)
                        _ = new Sheet(path);

                    // Check to see all Sheet objects are initialized
                    for (int i = 0; i < _sheets.Length; i++)
                        if (_sheets[i] == null)
                            throw new Exception($"Missing a Sheet[{i}] file which one or more sprites need");

                    Capacity = capacity;
                    _initialized = true;
                }
                else
                    throw new Exception("Invalid Atlas file");
            }
            else
                throw new Exception("Missing Atlas file");
        }

        // Retrieve sprite objects from AtlasSystem
        public static Sprite GetSprite(string spriteName)
        {
            if (_spriteByName.TryGetValue(spriteName, out Sprite sprite))
                return sprite;
            return null;
        }
        public static Sprite GetSprite(int spriteIndex)
        {
            if (spriteIndex < _sprites.Length)
                return _sprites[spriteIndex];
            return null;
        }

        public static void SetCapacity(long capacity) =>
            Capacity = capacity;

        // Extension Methods for SpriteBatch //

        public static void Draw(this SpriteBatch @this, Sprite sprite, Vector2 position, Color color)
        {
            foreach (SpriteFragment sf in sprite)
            {
                Texture2D texture = _sheets[sf.Sheet];
                if (texture != null)
                    @this.Draw(_sheets[sf.Sheet], position + sf.Position, sf.FragmentRectangle, color);
            }
        }
        public static void Draw(this SpriteBatch @this, Sprite sprite, Vector2 position, Color color, float rotation, Vector2 origin, float scale, SpriteEffects effects, float layerDepth)
        {
            foreach (SpriteFragment sf in sprite)
            {
                Texture2D texture = _sheets[sf.Sheet];
                if (texture != null)
                    @this.Draw(_sheets[sf.Sheet], position, sf.FragmentRectangle, color, rotation, origin - sf.Position, scale, effects, layerDepth);
            }
        }
        public static void Draw(this SpriteBatch @this, Sprite sprite, Vector2 position, Color color, float rotation, Vector2 origin, Vector2 scale, SpriteEffects effects, float layerDepth)
        {
            foreach (SpriteFragment sf in sprite)
            {
                Texture2D texture = _sheets[sf.Sheet];
                if (texture != null)

                    @this.Draw(_sheets[sf.Sheet], position, sf.FragmentRectangle, color, rotation, origin - sf.Position, scale, effects, layerDepth);
            }
        }
        public static void Draw(this SpriteBatch @this, Sprite sprite, Rectangle destinationRectangle, Color color)
        {
            Vector2 scalar = new(destinationRectangle.Width / (float)sprite.Width, destinationRectangle.Height / (float)sprite.Height);
            foreach (SpriteFragment sf in sprite)
            {
                Texture2D texture = _sheets[sf.Sheet];
                if (texture != null)
                {
                    Rectangle newDestinationRectangle = new((int)(destinationRectangle.X + (sf.Position.X * scalar.X)), (int)(destinationRectangle.Y + (sf.Position.Y * scalar.Y)), (int)(sf.FragmentRectangle.Width * scalar.X), (int)(sf.FragmentRectangle.Height * scalar.Y));
                    @this.Draw(_sheets[sf.Sheet], newDestinationRectangle, sf.FragmentRectangle, color);
                }
            }
        }
        public static void Draw(this SpriteBatch @this, Sprite sprite, Rectangle destinationRectangle, Color color, float rotation, Vector2 origin, SpriteEffects effects, float layerDepth)
        {
            Vector2 scalar = new(destinationRectangle.Width / (float)sprite.Width, destinationRectangle.Height / (float)sprite.Height);
            foreach (SpriteFragment sf in sprite)
            {
                Texture2D texture = _sheets[sf.Sheet];
                if (texture != null)
                {
                    Rectangle newDestinationRectangle = new(destinationRectangle.X, destinationRectangle.Y, (int)(sf.FragmentRectangle.Width * scalar.X), (int)(sf.FragmentRectangle.Height * scalar.Y));
                    @this.Draw(texture, newDestinationRectangle, sf.FragmentRectangle, color, rotation, origin - sf.Position, effects, layerDepth);
                }
            }
        }
    }
}
