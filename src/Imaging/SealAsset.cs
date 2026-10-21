using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DesktopStamp.Imaging
{
    /// <summary>
    /// Represents a loaded and processed seal artwork ready for aiming ghost preview
    /// and per-impression non-destructive stamping.
    /// </summary>
    public sealed class SealAsset : IDisposable
    {
        public string Id { get; }
        public string Name { get; set; }
        public string SourceFilePath { get; }
        public bool IsBuiltInSample { get; }
        public bool PreservedOriginalAlpha { get; }

        /// <summary>
        /// Processed master 32bpp ARGB bitmap (80-85% red opacity, feathered edges, cropped to tight ink bounds + margin).
        /// Never mutated by per-impression noise.
        /// </summary>
        public Bitmap MasterBitmap { get; }

        /// <summary>
        /// Immutable copy of the processed 32bpp BGRA pixel buffer so background impression generation
        /// is 100% thread-safe and never locks MasterBitmap while the UI renderer is drawing it.
        /// </summary>
        internal byte[] MasterPixelBytes { get; }
        internal int MasterStride { get; }
        internal int MasterWidth { get; }
        internal int MasterHeight { get; }

        /// <summary>
        /// Tight bounding rectangle of the ink imprint inside MasterBitmap.
        /// </summary>
        public Rectangle InkBounds { get; }

        /// <summary>
        /// Height / Width ratio of the actual ink imprint.
        /// </summary>
        public double InkAspectRatio => InkBounds.Width > 0 ? (double)InkBounds.Height / InkBounds.Width : 1.0;

        /// <summary>
        /// Ratio of InkBounds.Width to MasterBitmap.Width (used so physical mm sizing applies to the ink imprint width).
        /// </summary>
        public double InkToCanvasWidthRatio => MasterWidth > 0
            ? (double)InkBounds.Width / MasterWidth
            : 1.0;

        /// <summary>
        /// Ratio of InkBounds.Height to MasterBitmap.Height (independent of width ratio so equal pixel padding margins
        /// on non-square seals never distort vertical aspect ratio).
        /// </summary>
        public double InkToCanvasHeightRatio => MasterHeight > 0
            ? (double)InkBounds.Height / MasterHeight
            : 1.0;

        /// <summary>
        /// Height / Width aspect ratio of the padded MasterBitmap canvas.
        /// </summary>
        public double CanvasAspectRatio => MasterWidth > 0
            ? (double)MasterHeight / MasterWidth
            : 1.0;

        /// <summary>
        /// Recommended physical width in millimeters (e.g. 40mm for circular/oval, 20mm for small square chop).
        /// </summary>
        public double RecommendedSizeMm { get; }

        /// <summary>
        /// Measured peak opacity of the processed master seal (0.0 .. 1.0, target 0.80 .. 0.85).
        /// </summary>
        public double PeakOpacity { get; }

        public SealAsset(
            string id,
            string name,
            Bitmap masterBitmap,
            Rectangle inkBounds,
            bool preservedOriginalAlpha,
            double peakOpacity,
            double recommendedSizeMm = 40.0,
            string sourceFilePath = null,
            bool isBuiltInSample = false,
            byte[] masterPixelBytes = null,
            int masterStride = 0)
        {
            Id = id ?? Guid.NewGuid().ToString("N");
            Name = string.IsNullOrWhiteSpace(name) ? "未命名印章" : name;
            MasterBitmap = masterBitmap ?? throw new ArgumentNullException(nameof(masterBitmap));
            MasterWidth = masterBitmap.Width;
            MasterHeight = masterBitmap.Height;
            InkBounds = inkBounds;
            PreservedOriginalAlpha = preservedOriginalAlpha;
            PeakOpacity = peakOpacity;
            RecommendedSizeMm = recommendedSizeMm;
            SourceFilePath = sourceFilePath;
            IsBuiltInSample = isBuiltInSample;

            if (masterPixelBytes != null && masterStride > 0)
            {
                MasterPixelBytes = (byte[])masterPixelBytes.Clone();
                MasterStride = masterStride;
            }
            else
            {
                var rect = new Rectangle(0, 0, MasterWidth, MasterHeight);
                BitmapData data = masterBitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    MasterStride = Math.Abs(data.Stride);
                    MasterPixelBytes = new byte[MasterStride * MasterHeight];
                    Marshal.Copy(data.Scan0, MasterPixelBytes, 0, MasterPixelBytes.Length);
                }
                finally
                {
                    masterBitmap.UnlockBits(data);
                }
            }
        }

        public void Dispose()
        {
            MasterBitmap?.Dispose();
        }
    }

    /// <summary>
    /// Structured result of importing an external seal image.
    /// </summary>
    public sealed class SealImportResult
    {
        public bool Success { get; private set; }
        public SealAsset Asset { get; private set; }
        public string ErrorMessage { get; private set; }

        public static SealImportResult Ok(SealAsset asset) => new SealImportResult
        {
            Success = true,
            Asset = asset
        };

        public static SealImportResult Failure(string errorMessage) => new SealImportResult
        {
            Success = false,
            ErrorMessage = errorMessage
        };
    }
}
