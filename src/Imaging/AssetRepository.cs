using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopStamp.Imaging
{
    /// <summary>
    /// Holds a loaded sprite bitmap along with its measured occupied-alpha bounding box
    /// and normalized visual anchors so pose transitions never shift or stretch the base.
    /// </summary>
    public sealed class SpriteMetricAsset : IDisposable
    {
        public string FileName { get; }
        public Bitmap Bitmap { get; }
        public Rectangle OccupiedBounds { get; }

        /// <summary>
        /// Center of the occupied alpha bounds in bitmap pixel coordinates.
        /// </summary>
        public PointF OccupiedCenter => new PointF(
            OccupiedBounds.Left + OccupiedBounds.Width * 0.5f,
            OccupiedBounds.Top + OccupiedBounds.Height * 0.5f);

        /// <summary>
        /// Bottom-center anchor of the occupied alpha bounds in bitmap pixel coordinates.
        /// </summary>
        public PointF OccupiedBaseBottom => new PointF(
            OccupiedBounds.Left + OccupiedBounds.Width * 0.5f,
            OccupiedBounds.Bottom);

        /// <summary>
        /// For annular reticle sprites: measured outer radius in source bitmap pixels.
        /// </summary>
        public float OuterRadiusPx { get; }

        /// <summary>
        /// For annular reticle sprites: measured transparent inner aperture radius in source bitmap pixels.
        /// </summary>
        public float InnerApertureRadiusPx { get; }

        public SpriteMetricAsset(
            string fileName,
            Bitmap bitmap,
            Rectangle occupiedBounds,
            float outerRadiusPx = 0f,
            float innerApertureRadiusPx = 0f)
        {
            FileName = fileName;
            Bitmap = bitmap;
            OccupiedBounds = occupiedBounds;
            OuterRadiusPx = outerRadiusPx;
            InnerApertureRadiusPx = innerApertureRadiusPx;
        }

        public void Dispose()
        {
            Bitmap?.Dispose();
        }
    }

    /// <summary>
    /// Dynamically loads Codex-supplied transparent PNG sprites from assets/ without recompilation,
    /// computes their occupied-alpha bounds and inner/outer aperture radii, and watches for live updates.
    /// Falls back gracefully to procedural vector/shader geometry if any sprite file is absent.
    /// </summary>
    public sealed class AssetRepository : IDisposable
    {
        private readonly object _sync = new object();
        private readonly string _assetsDirectory;
        private FileSystemWatcher _watcher;

        public SpriteMetricAsset IdleSprite { get; private set; }
        public SpriteMetricAsset DragSprite { get; private set; }
        public SpriteMetricAsset ReticleSprite { get; private set; }

        public string AssetsDirectory => _assetsDirectory;
        public event Action AssetsReloaded;

        public AssetRepository(string explicitAssetsDirectory = null)
        {
            _assetsDirectory = ResolveAssetsDirectory(explicitAssetsDirectory);
            ReloadAssets();
            StartWatcher();
        }

        public static string ResolveAssetsDirectory(string explicitDirectory = null)
        {
            if (!string.IsNullOrWhiteSpace(explicitDirectory) && Directory.Exists(explicitDirectory))
                return Path.GetFullPath(explicitDirectory);

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates = new[]
            {
                Path.Combine(baseDir, "assets"),
                Path.Combine(baseDir, "..", "assets"),
                Path.Combine(baseDir, "..", "..", "assets"),
                Path.Combine(Environment.CurrentDirectory, "assets"),
                Path.Combine(Environment.CurrentDirectory, "stamp-project", "assets")
            };

            foreach (string candidate in candidates)
            {
                try
                {
                    string full = Path.GetFullPath(candidate);
                    if (Directory.Exists(full))
                        return full;
                }
                catch
                {
                    // Ignore invalid path candidates
                }
            }

            return Path.Combine(baseDir, "assets");
        }

        public void ReloadAssets()
        {
            if (string.IsNullOrEmpty(_assetsDirectory) || !Directory.Exists(_assetsDirectory))
                return;

            SpriteMetricAsset nextIdle = TryLoadSprite(Path.Combine(_assetsDirectory, "stamp-idle-v1.png"), isReticle: false);
            SpriteMetricAsset nextDrag = TryLoadSprite(Path.Combine(_assetsDirectory, "stamp-drag-v1.png"), isReticle: false);
            SpriteMetricAsset nextReticle = TryLoadSprite(Path.Combine(_assetsDirectory, "stamp-reticle-v1.png"), isReticle: true);

            lock (_sync)
            {
                if (nextIdle != null)
                {
                    IdleSprite?.Dispose();
                    IdleSprite = nextIdle;
                }
                if (nextDrag != null)
                {
                    DragSprite?.Dispose();
                    DragSprite = nextDrag;
                }
                if (nextReticle != null)
                {
                    ReticleSprite?.Dispose();
                    ReticleSprite = nextReticle;
                }
            }

            AssetsReloaded?.Invoke();
        }

        private static SpriteMetricAsset TryLoadSprite(string path, bool isReticle)
        {
            if (!File.Exists(path))
                return null;

            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                using (var ms = new MemoryStream(bytes))
                using (var img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true))
                {
                    var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.DrawImage(img, new Rectangle(0, 0, bmp.Width, bmp.Height));
                    }

                    Rectangle bounds = MeasureOccupiedAlphaBounds(bmp, alphaThreshold: 16);
                    float outerR = Math.Max(bounds.Width, bounds.Height) * 0.5f;
                    float innerR = outerR * 0.82f;

                    if (isReticle)
                    {
                        MeasureReticleRadii(bmp, bounds, out outerR, out innerR);
                        MaskReticleApertureInMemory(bmp, bounds, innerR, outerR);
                    }

                    return new SpriteMetricAsset(Path.GetFileName(path), bmp, bounds, outerR, innerR);
                }
            }
            catch
            {
                return null;
            }
        }

        public static Rectangle MeasureOccupiedAlphaBounds(Bitmap bmp, byte alphaThreshold = 16)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            var rect = new Rectangle(0, 0, w, h);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            try
            {
                int stride = Math.Abs(data.Stride);
                byte[] raw = new byte[stride * h];
                Marshal.Copy(data.Scan0, raw, 0, raw.Length);

                int minX = w, maxX = 0, minY = h, maxY = 0;
                bool found = false;

                for (int y = 0; y < h; y += 2)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x += 2)
                    {
                        if (raw[row + x * 4 + 3] > alphaThreshold)
                        {
                            found = true;
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }

                if (!found)
                    return new Rectangle(0, 0, w, h);

                return new Rectangle(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private static void MeasureReticleRadii(Bitmap bmp, Rectangle occupiedBounds, out float outerRadius, out float innerRadius)
        {
            outerRadius = (occupiedBounds.Width + occupiedBounds.Height) * 0.25f;
            innerRadius = outerRadius * 0.83f;

            int cx = occupiedBounds.Left + occupiedBounds.Width / 2;
            int cy = occupiedBounds.Top + occupiedBounds.Height / 2;
            if (cy < 0 || cy >= bmp.Height || cx <= 0 || cx >= bmp.Width)
                return;

            int firstOpaqueX = -1;
            int firstInnerClearX = -1;
            for (int x = 0; x < cx; x++)
            {
                byte a = bmp.GetPixel(x, cy).A;
                if (firstOpaqueX == -1 && a > 24)
                    firstOpaqueX = x;
                else if (firstOpaqueX != -1 && firstInnerClearX == -1 && a < 10)
                    firstInnerClearX = x;
            }

            if (firstOpaqueX >= 0 && firstInnerClearX > firstOpaqueX)
            {
                outerRadius = cx - firstOpaqueX;
                innerRadius = cx - firstInnerClearX;
            }
        }

        /// <summary>
        /// Explicitly masks the central aperture (and exterior padding) of the in-memory reticle sprite to alpha zero
        /// so image-generation antialiasing residue (up to 1/255 alpha) is eliminated without modifying the disk file
        /// or erasing the separately rendered seal ghost.
        /// </summary>
        private static void MaskReticleApertureInMemory(Bitmap bmp, Rectangle occupiedBounds, float innerRadius, float outerRadius)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            var rect = new Rectangle(0, 0, w, h);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

            try
            {
                int stride = Math.Abs(data.Stride);
                byte[] raw = new byte[stride * h];
                Marshal.Copy(data.Scan0, raw, 0, raw.Length);

                float cx = occupiedBounds.Left + occupiedBounds.Width * 0.5f;
                float cy = occupiedBounds.Top + occupiedBounds.Height * 0.5f;
                float clearInnerR = Math.Max(1f, innerRadius - 1.5f);
                float clearInnerSq = clearInnerR * clearInnerR;
                float clearOuterR = outerRadius + 4.0f;
                float clearOuterSq = clearOuterR * clearOuterR;

                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    float dy = y - cy;
                    float dySq = dy * dy;
                    for (int x = 0; x < w; x++)
                    {
                        float dx = x - cx;
                        float distSq = dx * dx + dySq;
                        if (distSq < clearInnerSq || distSq > clearOuterSq)
                        {
                            int idx = row + x * 4;
                            raw[idx + 0] = 0;
                            raw[idx + 1] = 0;
                            raw[idx + 2] = 0;
                            raw[idx + 3] = 0;
                        }
                    }
                }

                Marshal.Copy(raw, 0, data.Scan0, raw.Length);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private void StartWatcher()
        {
            try
            {
                if (!Directory.Exists(_assetsDirectory))
                    return;

                _watcher = new FileSystemWatcher(_assetsDirectory, "*.png")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };
                _watcher.Changed += OnAssetFileEvent;
                _watcher.Created += OnAssetFileEvent;
                _watcher.Renamed += OnAssetFileEvent;
            }
            catch
            {
                // Watcher is optional convenience
            }
        }

        private void OnAssetFileEvent(object sender, FileSystemEventArgs e)
        {
            try
            {
                ReloadAssets();
            }
            catch
            {
                // Ignore transient file write lock during copy
            }
        }

        public void Dispose()
        {
            _watcher?.Dispose();
            lock (_sync)
            {
                IdleSprite?.Dispose();
                DragSprite?.Dispose();
                ReticleSprite?.Dispose();
            }
        }
    }
}
