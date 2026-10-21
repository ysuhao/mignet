using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopStamp.Imaging
{
    /// <summary>
    /// Processes imported seal images (PNG with alpha, or opaque photos/scans via red color extraction),
    /// normalizes opacity to 80–85% with feathered edges, generates non-destructive per-impression
    /// ink noise/gaps and edge diffusion, and synthesizes built-in SAMPLE/样章 demonstration seals.
    /// </summary>
    public static class SealImageProcessor
    {
        private static readonly HashSet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif"
        };

        /// <summary>
        /// Target peak opacity band: 80% to 85% (alpha 204..217 out of 255).
        /// </summary>
        public const double TargetMinOpacity = 0.80;
        public const double TargetMaxOpacity = 0.85;
        public const byte TargetMasterMaxAlpha = 211; // ~82.75% opacity

        /// <summary>
        /// Imports a seal image from disk without locking or modifying the source file.
        /// </summary>
        public static SealImportResult LoadAndProcessSeal(string filePath, string customName = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return SealImportResult.Failure("No file path provided.");

            if (!File.Exists(filePath))
                return SealImportResult.Failure($"File not found: {Path.GetFileName(filePath)}");

            string ext = Path.GetExtension(filePath);
            if (string.IsNullOrEmpty(ext) || !SupportedExtensions.Contains(ext))
            {
                return SealImportResult.Failure(
                    $"Unsupported file format '{ext}'. Drop a PNG, JPG, BMP, or TIFF seal image.");
            }

            try
            {
                byte[] rawBytes = File.ReadAllBytes(filePath);
                using (var ms = new MemoryStream(rawBytes))
                using (var rawImage = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true))
                using (var sourceBmp = new Bitmap(rawImage))
                {
                    string displayName = !string.IsNullOrWhiteSpace(customName)
                        ? customName
                        : Path.GetFileNameWithoutExtension(filePath);

                    return ProcessSealBitmap(sourceBmp, displayName, filePath, isBuiltInSample: false);
                }
            }
            catch (Exception ex)
            {
                return SealImportResult.Failure($"Could not decode image '{Path.GetFileName(filePath)}': {ex.Message}");
            }
        }

        /// <summary>
        /// Processes an in-memory Bitmap into a normalized SealAsset:
        /// 1. Preserves valid alpha if present; otherwise extracts red seal ink via chromatic redness thresholding + antialiasing.
        /// 2. Normalizes peak opacity into the 80–85% range and feathers edges.
        /// 3. Crops tightly around the ink imprint with a small feather margin so physical mm sizing matches the ink imprint.
        /// </summary>
        public static SealImportResult ProcessSealBitmap(
            Bitmap sourceBitmap,
            string name,
            string sourceFilePath = null,
            bool isBuiltInSample = false,
            double recommendedSizeMm = 40.0)
        {
            if (sourceBitmap == null || sourceBitmap.Width < 8 || sourceBitmap.Height < 8)
                return SealImportResult.Failure("Image is too small or invalid (minimum 8x8 pixels).");

            // Normalize maximum working dimension to 900px for fast, crisp processing
            Bitmap working = null;
            try
            {
                int maxDim = Math.Max(sourceBitmap.Width, sourceBitmap.Height);
                if (maxDim > 900)
                {
                    double scale = 900.0 / maxDim;
                    int w = Math.Max(8, (int)Math.Round(sourceBitmap.Width * scale));
                    int h = Math.Max(8, (int)Math.Round(sourceBitmap.Height * scale));
                    working = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(working))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(sourceBitmap, new Rectangle(0, 0, w, h));
                    }
                }
                else
                {
                    working = new Bitmap(sourceBitmap.Width, sourceBitmap.Height, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(working))
                    {
                        g.DrawImage(sourceBitmap, new Rectangle(0, 0, working.Width, working.Height));
                    }
                }

                int width = working.Width;
                int height = working.Height;
                var rect = new Rectangle(0, 0, width, height);
                BitmapData data = working.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                byte[] pixels = new byte[Math.Abs(data.Stride) * height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                int stride = Math.Abs(data.Stride);

                // Step 1: Inspect whether the bitmap has genuine alpha transparency
                bool hasValidAlpha = DetectValidAlphaChannel(pixels, width, height, stride);

                byte[] alphaMap = new byte[width * height];
                byte[] redMap = new byte[width * height];
                byte[] greenMap = new byte[width * height];
                byte[] blueMap = new byte[width * height];

                int foregroundPixels = 0;

                if (hasValidAlpha)
                {
                    // Preserve original alpha geometry; tint non-red or normalize seal red color while keeping alpha profile
                    int maxInputAlpha = 1;
                    for (int y = 0; y < height; y++)
                    {
                        int rowOffset = y * stride;
                        for (int x = 0; x < width; x++)
                        {
                            byte a = pixels[rowOffset + x * 4 + 3];
                            if (a > maxInputAlpha) maxInputAlpha = a;
                        }
                    }

                    for (int y = 0; y < height; y++)
                    {
                        int rowOffset = y * stride;
                        int idxRow = y * width;
                        for (int x = 0; x < width; x++)
                        {
                            byte b = pixels[rowOffset + x * 4 + 0];
                            byte g = pixels[rowOffset + x * 4 + 1];
                            byte r = pixels[rowOffset + x * 4 + 2];
                            byte a = pixels[rowOffset + x * 4 + 3];

                            if (a <= 4)
                            {
                                alphaMap[idxRow + x] = 0;
                                continue;
                            }

                            // Normalize alpha so peak lies in [80%, 85%] (~211/255)
                            double normA = (double)a / maxInputAlpha;
                            byte scaledA = (byte)Math.Max(0, Math.Min(255, Math.Round(normA * TargetMasterMaxAlpha)));
                            alphaMap[idxRow + x] = scaledA;

                            // Preserve red ink hue or map dark/monochrome mask to classic vermilion-crimson seal ink
                            if (r > g + 25 && r > b + 25)
                            {
                                redMap[idxRow + x] = (byte)Math.Max(165, Math.Min(230, (int)r));
                                greenMap[idxRow + x] = (byte)Math.Min(48, (int)g);
                                blueMap[idxRow + x] = (byte)Math.Min(48, (int)b);
                            }
                            else
                            {
                                redMap[idxRow + x] = 206;
                                greenMap[idxRow + x] = 26;
                                blueMap[idxRow + x] = 30;
                            }

                            if (scaledA >= 24)
                                foregroundPixels++;
                        }
                    }
                }
                else
                {
                    // Extract red seal ink via chromatic redness thresholding + smoothstep antialiasing
                    for (int y = 0; y < height; y++)
                    {
                        int rowOffset = y * stride;
                        int idxRow = y * width;
                        for (int x = 0; x < width; x++)
                        {
                            byte b = pixels[rowOffset + x * 4 + 0];
                            byte g = pixels[rowOffset + x * 4 + 1];
                            byte r = pixels[rowOffset + x * 4 + 2];

                            // Redness metrics:
                            // 1) Excess over green and blue: R - max(G, B)
                            // 2) Relative dominance over average of G and B
                            int maxGB = Math.Max(g, b);
                            int minGB = Math.Min(g, b);
                            double redExcess = r - maxGB;
                            double chroma = r - 0.5 * (g + b);
                            double relativeRed = chroma / Math.Max(50.0, (double)r);

                            // Reject neutral dark text (low R) and bright white/gray paper (low redExcess)
                            if (r < 65 || redExcess < 14.0 || relativeRed < 0.14)
                            {
                                alphaMap[idxRow + x] = 0;
                                continue;
                            }

                            // Combine excess and relative redness into a smooth antialiased strength in [0, 1]
                            double score = (redExcess - 14.0) / 58.0 * 0.65 + (relativeRed - 0.14) / 0.42 * 0.35;
                            double smoothAlpha = SmoothStep(0.0, 1.0, score);

                            if (smoothAlpha <= 0.02)
                            {
                                alphaMap[idxRow + x] = 0;
                                continue;
                            }

                            byte outA = (byte)Math.Max(0, Math.Min((int)TargetMasterMaxAlpha, (int)Math.Round(smoothAlpha * TargetMasterMaxAlpha)));
                            alphaMap[idxRow + x] = outA;

                            // Reconstruct clean crimson/vermilion ink color (free of white paper desaturation)
                            int outR = Math.Max(175, Math.Min(222, (int)Math.Round(182 + smoothAlpha * 32)));
                            int outG = Math.Max(14, Math.Min(42, (int)Math.Round(minGB * 0.22)));
                            int outB = Math.Max(16, Math.Min(44, (int)Math.Round(minGB * 0.24)));

                            redMap[idxRow + x] = (byte)outR;
                            greenMap[idxRow + x] = (byte)outG;
                            blueMap[idxRow + x] = (byte)outB;

                            if (outA >= 24)
                                foregroundPixels++;
                        }
                    }
                }

                working.UnlockBits(data);

                double coverage = (double)foregroundPixels / (width * height);
                if (foregroundPixels < 25 || coverage < 0.001)
                {
                    return SealImportResult.Failure(
                        hasValidAlpha
                            ? "Image contains no visible seal foreground in its alpha channel."
                            : "No red seal ink detected in opaque image. Please provide a transparent PNG or a photo/scan containing a red seal.");
                }

                // Step 2: Feather boundary edges gently (separable 3-tap kernel on boundary pixels) and find tight ink bounding box
                byte[] featheredAlpha = FeatherBoundaryAlpha(alphaMap, width, height, TargetMasterMaxAlpha);

                int minX = width, maxX = -1, minY = height, maxY = -1;
                byte peakAlpha = 0;
                for (int y = 0; y < height; y++)
                {
                    int idxRow = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        byte a = featheredAlpha[idxRow + x];
                        if (a > peakAlpha) peakAlpha = a;
                        if (a >= 12)
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }

                if (maxX < minX || maxY < minY)
                {
                    return SealImportResult.Failure("Extracted seal bounds are empty after edge feathering.");
                }

                // Ensure peak alpha is strictly within 80-85% (204..217)
                double normScale = peakAlpha > 0 ? (double)TargetMasterMaxAlpha / peakAlpha : 1.0;
                peakAlpha = TargetMasterMaxAlpha;

                int inkWidth = Math.Max(1, maxX - minX + 1);
                int inkHeight = Math.Max(1, maxY - minY + 1);
                int margin = Math.Max(4, (int)Math.Round(Math.Max(inkWidth, inkHeight) * 0.025));

                int outWidth = inkWidth + margin * 2;
                int outHeight = inkHeight + margin * 2;

                var masterBmp = new Bitmap(outWidth, outHeight, PixelFormat.Format32bppArgb);
                BitmapData outData = masterBmp.LockBits(
                    new Rectangle(0, 0, outWidth, outHeight),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);

                int outStride = Math.Abs(outData.Stride);
                byte[] outBytes = new byte[outStride * outHeight];

                for (int y = 0; y < inkHeight; y++)
                {
                    int srcY = minY + y;
                    int dstY = margin + y;
                    int srcRow = srcY * width;
                    int dstRow = dstY * outStride;

                    for (int x = 0; x < inkWidth; x++)
                    {
                        int srcX = minX + x;
                        int dstX = margin + x;
                        int srcIdx = srcRow + srcX;

                        byte a = (byte)Math.Max(0, Math.Min((int)TargetMasterMaxAlpha, (int)Math.Round(featheredAlpha[srcIdx] * normScale)));
                        if (a == 0) continue;

                        int dstByteIdx = dstRow + dstX * 4;
                        outBytes[dstByteIdx + 0] = blueMap[srcIdx];
                        outBytes[dstByteIdx + 1] = greenMap[srcIdx];
                        outBytes[dstByteIdx + 2] = redMap[srcIdx];
                        outBytes[dstByteIdx + 3] = a;
                    }
                }

                Marshal.Copy(outBytes, 0, outData.Scan0, outBytes.Length);
                masterBmp.UnlockBits(outData);

                var inkBounds = new Rectangle(margin, margin, inkWidth, inkHeight);
                double measuredPeakOpacity = peakAlpha / 255.0;

                var asset = new SealAsset(
                    Guid.NewGuid().ToString("N"),
                    name,
                    masterBmp,
                    inkBounds,
                    hasValidAlpha,
                    measuredPeakOpacity,
                    recommendedSizeMm,
                    sourceFilePath,
                    isBuiltInSample,
                    outBytes,
                    outStride);

                return SealImportResult.Ok(asset);
            }
            finally
            {
                working?.Dispose();
            }
        }

        /// <summary>
        /// Generates a non-destructive per-impression Bitmap from a SealAsset by cloning the master pixel buffer
        /// and applying subtle procedural paper fiber ink gaps and edge diffusion.
        /// Thread-safe and never mutates or locks <paramref name="seal"/>.MasterBitmap during UI rendering.
        /// </summary>
        public static Bitmap CreateImpressionBitmap(SealAsset seal, int impressionSeed)
        {
            if (seal == null || (seal.MasterPixelBytes == null && seal.MasterBitmap == null))
                throw new ArgumentNullException(nameof(seal));

            int width = seal.MasterWidth > 0 ? seal.MasterWidth : seal.MasterBitmap.Width;
            int height = seal.MasterHeight > 0 ? seal.MasterHeight : seal.MasterBitmap.Height;
            var impression = new Bitmap(width, height, PixelFormat.Format32bppArgb);

            var rect = new Rectangle(0, 0, width, height);
            BitmapData dstData = impression.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            try
            {
                int stride = Math.Abs(dstData.Stride);
                byte[] buffer;
                if (seal.MasterPixelBytes != null && seal.MasterStride == stride)
                {
                    buffer = (byte[])seal.MasterPixelBytes.Clone();
                }
                else
                {
                    BitmapData srcData = seal.MasterBitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        buffer = new byte[stride * height];
                        Marshal.Copy(srcData.Scan0, buffer, 0, buffer.Length);
                    }
                    finally
                    {
                        seal.MasterBitmap.UnlockBits(srcData);
                    }
                }

                // Precompute X grid coordinates and smoothstep weights once for the row width
                int[] ixTable = new int[width];
                double[] uTable = new double[width];
                for (int x = 0; x < width; x++)
                {
                    double gx = x * 0.16;
                    int ix = (int)gx;
                    double fx = gx - ix;
                    ixTable[x] = ix;
                    uTable[x] = fx * fx * (3.0 - 2.0 * fx);
                }

                int fineSeed = impressionSeed ^ 0x5A17;

                for (int y = 0; y < height; y++)
                {
                    int rowOffset = y * stride;
                    double gy = y * 0.16;
                    int iy = (int)gy;
                    double fy = gy - iy;
                    double v = fy * fy * (3.0 - 2.0 * fy);

                    for (int x = 0; x < width; x++)
                    {
                        int idx = rowOffset + x * 4;
                        byte a = buffer[idx + 3];
                        if (a == 0) continue;

                        int ix = ixTable[x];
                        double u = uTable[x];
                        double n00 = HashNoise(ix, iy, impressionSeed);
                        double n10 = HashNoise(ix + 1, iy, impressionSeed);
                        double n01 = HashNoise(ix, iy + 1, impressionSeed);
                        double n11 = HashNoise(ix + 1, iy + 1, impressionSeed);
                        double nx0 = n00 + (n10 - n00) * u;
                        double nx1 = n01 + (n11 - n01) * u;
                        double coarseNoise = nx0 + (nx1 - nx0) * v;
                        double fineNoise = HashNoise(x, y, fineSeed);

                        if (a < 130)
                        {
                            // Boundary pixel: apply subtle edge diffusion
                            double edgeMod = 0.78 + 0.36 * (coarseNoise * 0.65 + fineNoise * 0.35);
                            int newA = (int)Math.Round(a * edgeMod);
                            buffer[idx + 3] = (byte)Math.Max(0, Math.Min((int)TargetMasterMaxAlpha, newA));
                        }
                        else
                        {
                            // Interior ink stroke: apply subtle microscopic ink gaps / paper texture
                            double gapFactor;
                            double combined = coarseNoise * 0.6 + fineNoise * 0.4;
                            if (combined < 0.18)
                            {
                                gapFactor = 0.72 + (combined / 0.18) * 0.20;
                            }
                            else
                            {
                                gapFactor = 0.94 + (combined - 0.18) * 0.08;
                            }

                            int newA = (int)Math.Round(a * gapFactor);
                            buffer[idx + 3] = (byte)Math.Max(20, Math.Min(215, newA));
                        }
                    }
                }

                Marshal.Copy(buffer, 0, dstData.Scan0, buffer.Length);
            }
            finally
            {
                impression.UnlockBits(dstData);
            }

            return impression;
        }

        /// <summary>
        /// Creates the built-in procedural SAMPLE / 样章 seals (clearly marked SAMPLE/样章 with no real company identity).
        /// </summary>
        public static List<SealAsset> CreateBuiltInSampleSeals()
        {
            var list = new List<SealAsset>();

            using (Bitmap circleRaw = RenderCircularSampleSealBitmap(560))
            {
                var res = ProcessSealBitmap(circleRaw, "样章 · 圆形测试章 (SAMPLE)", null, isBuiltInSample: true, recommendedSizeMm: 40.0);
                if (res.Success) list.Add(res.Asset);
            }

            using (Bitmap ovalRaw = RenderOvalSampleSealBitmap(560, 400))
            {
                var res = ProcessSealBitmap(ovalRaw, "样章 · 椭圆核验章 (SAMPLE)", null, isBuiltInSample: true, recommendedSizeMm: 40.0);
                if (res.Success) list.Add(res.Asset);
            }

            using (Bitmap squareRaw = RenderSquareSampleSealBitmap(440))
            {
                var res = ProcessSealBitmap(squareRaw, "样章 · 方形测试印 (SAMPLE)", null, isBuiltInSample: true, recommendedSizeMm: 20.0);
                if (res.Success) list.Add(res.Asset);
            }

            return list;
        }

        /// <summary>
        /// Creates a built-in wide signature demonstration seal (clearly marked 样章 / SAMPLE)
        /// with measured, auto-fitted typography that stays strictly inside the signature border.
        /// </summary>
        public static SealAsset CreateWideSignatureSampleSeal(double recommendedSizeMm = 40.0)
        {
            using (Bitmap raw = RenderWideSignatureSampleSealBitmap(680, 176))
            {
                SealImportResult res = ProcessSealBitmap(
                    raw,
                    "样章 · 宽幅签名章 (SAMPLE)",
                    null,
                    isBuiltInSample: true,
                    recommendedSizeMm: recommendedSizeMm);
                return res.Success ? res.Asset : null;
            }
        }

        private static Bitmap RenderWideSignatureSampleSealBitmap(int width, int height)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                Color inkColor = Color.FromArgb(212, 206, 24, 28);
                float padX = width * 0.045f;
                float padY = height * 0.12f;
                var outerRect = new RectangleF(padX, padY, width - padX * 2f, height - padY * 2f);
                var innerRect = RectangleF.Inflate(outerRect, -6f, -6f);

                using (var outerPen = new Pen(inkColor, 4.5f))
                using (var innerPen = new Pen(inkColor, 1.6f))
                using (var brush = new SolidBrush(inkColor))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawRectangle(outerPen, outerRect.X, outerRect.Y, outerRect.Width, outerRect.Height);
                    g.DrawRectangle(innerPen, innerRect.X, innerRect.Y, innerRect.Width, innerRect.Height);

                    string mainLabel = "测试专用样章 · SAMPLE";
                    float maxTextWidth = innerRect.Width - 28f;
                    float mainFontSize = height * 0.25f;
                    while (mainFontSize > 12f)
                    {
                        using (var testFont = new Font("Microsoft YaHei UI", mainFontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                        {
                            if (g.MeasureString(mainLabel, testFont).Width <= maxTextWidth)
                                break;
                        }
                        mainFontSize -= 1.5f;
                    }

                    string subLabel = "样章演示 · 仅供测试 (SAMPLE ONLY)";
                    float subFontSize = height * 0.145f;
                    while (subFontSize > 10f)
                    {
                        using (var testFont = new Font("Microsoft YaHei UI", subFontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                        {
                            if (g.MeasureString(subLabel, testFont).Width <= maxTextWidth)
                                break;
                        }
                        subFontSize -= 1.0f;
                    }

                    using (var mainFont = new Font("Microsoft YaHei UI", mainFontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var subFont = new Font("Microsoft YaHei UI", subFontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                    {
                        float cx = width * 0.5f;
                        float cy = height * 0.5f;
                        g.DrawString(mainLabel, mainFont, brush, new PointF(cx, cy - height * 0.12f), sf);
                        g.DrawString(subLabel, subFont, brush, new PointF(cx, cy + height * 0.17f), sf);
                    }
                }
            }
            return bmp;
        }

        private static Bitmap RenderCircularSampleSealBitmap(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                Color inkColor = Color.FromArgb(212, 208, 24, 28);
                float cx = size * 0.5f;
                float cy = size * 0.5f;
                float outerR = size * 0.44f;

                using (var outerPen = new Pen(inkColor, size * 0.028f))
                using (var innerPen = new Pen(inkColor, size * 0.009f))
                using (var brush = new SolidBrush(inkColor))
                {
                    g.DrawEllipse(outerPen, cx - outerR, cy - outerR, outerR * 2, outerR * 2);
                    float innerR = outerR * 0.90f;
                    g.DrawEllipse(innerPen, cx - innerR, cy - innerR, innerR * 2, innerR * 2);

                    // Central 5-point star
                    DrawStar(g, brush, cx, cy - size * 0.01f, size * 0.125f, size * 0.048f);

                    // Top arc text: "测试专用样章 · SAMPLE SEAL"
                    string topText = "测试专用样章·SAMPLE";
                    float textRadius = outerR * 0.71f;
                    float startAngle = -156f;
                    float sweepAngle = 132f;
                    int count = topText.Length;

                    using (var font = new Font("Microsoft YaHei UI", size * 0.056f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    {
                        for (int i = 0; i < count; i++)
                        {
                            float t = count > 1 ? (float)i / (count - 1) : 0.5f;
                            float angleDeg = startAngle + t * sweepAngle;
                            double angleRad = angleDeg * Math.PI / 180.0;
                            float tx = cx + (float)(Math.Cos(angleRad) * textRadius);
                            float ty = cy + (float)(Math.Sin(angleRad) * textRadius);

                            GraphicsState state = g.Save();
                            g.TranslateTransform(tx, ty);
                            g.RotateTransform(angleDeg + 90f);
                            g.DrawString(topText[i].ToString(), font, brush, 0, 0, sf);
                            g.Restore(state);
                        }
                    }

                    // Bottom horizontal labels clearly stating SAMPLE / 样章
                    using (var bannerFont = new Font("Microsoft YaHei UI", size * 0.052f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var subFont = new Font("Segoe UI", size * 0.036f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    {
                        g.DrawString("样章演示", bannerFont, brush, new PointF(cx, cy + outerR * 0.43f), sf);
                        g.DrawString("SAMPLE ONLY", subFont, brush, new PointF(cx, cy + outerR * 0.62f), sf);
                    }
                }
            }
            return bmp;
        }

        private static Bitmap RenderOvalSampleSealBitmap(int width, int height)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                Color inkColor = Color.FromArgb(212, 206, 24, 28);
                float pad = width * 0.06f;
                var outerRect = new RectangleF(pad, pad, width - pad * 2, height - pad * 2);
                var innerRect = RectangleF.Inflate(outerRect, -width * 0.025f, -height * 0.025f);

                using (var outerPen = new Pen(inkColor, width * 0.024f))
                using (var innerPen = new Pen(inkColor, width * 0.008f))
                using (var brush = new SolidBrush(inkColor))
                using (var titleFont = new Font("Microsoft YaHei UI", height * 0.11f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var midFont = new Font("Segoe UI", height * 0.095f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var subFont = new Font("Microsoft YaHei UI", height * 0.072f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawEllipse(outerPen, outerRect);
                    g.DrawEllipse(innerPen, innerRect);

                    float cx = width * 0.5f;
                    float cy = height * 0.5f;
                    g.DrawString("测试专用样章", titleFont, brush, new PointF(cx, cy - height * 0.18f), sf);
                    g.DrawString("SAMPLE SEAL", midFont, brush, new PointF(cx, cy + height * 0.01f), sf);
                    g.DrawString("仅供演示 · 无效印章", subFont, brush, new PointF(cx, cy + height * 0.19f), sf);
                }
            }
            return bmp;
        }

        private static Bitmap RenderSquareSampleSealBitmap(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                Color inkColor = Color.FromArgb(212, 204, 22, 26);
                float pad = size * 0.08f;
                var rect = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);

                using (var borderPen = new Pen(inkColor, size * 0.042f))
                using (var innerPen = new Pen(inkColor, size * 0.012f))
                using (var brush = new SolidBrush(inkColor))
                using (var charFont = new Font("Microsoft YaHei UI", size * 0.24f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var subFont = new Font("Segoe UI", size * 0.062f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawRectangle(borderPen, rect.X, rect.Y, rect.Width, rect.Height);
                    g.DrawLine(innerPen, size * 0.5f, rect.Top + size * 0.04f, size * 0.5f, rect.Bottom - size * 0.14f);
                    g.DrawLine(innerPen, rect.Left + size * 0.04f, size * 0.45f, rect.Right - size * 0.04f, size * 0.45f);

                    g.DrawString("样", charFont, brush, new PointF(size * 0.31f, size * 0.28f), sf);
                    g.DrawString("章", charFont, brush, new PointF(size * 0.69f, size * 0.28f), sf);
                    g.DrawString("测", charFont, brush, new PointF(size * 0.31f, size * 0.62f), sf);
                    g.DrawString("试", charFont, brush, new PointF(size * 0.69f, size * 0.62f), sf);

                    g.DrawString("SAMPLE", subFont, brush, new PointF(size * 0.5f, rect.Bottom - size * 0.065f), sf);
                }
            }
            return bmp;
        }

        private static void DrawStar(Graphics g, Brush brush, float cx, float cy, float outerRadius, float innerRadius)
        {
            PointF[] pts = new PointF[10];
            for (int i = 0; i < 10; i++)
            {
                double angle = -Math.PI / 2.0 + i * Math.PI / 5.0;
                float r = (i % 2 == 0) ? outerRadius : innerRadius;
                pts[i] = new PointF(
                    cx + (float)(Math.Cos(angle) * r),
                    cy + (float)(Math.Sin(angle) * r));
            }
            g.FillPolygon(brush, pts);
        }

        private static bool DetectValidAlphaChannel(byte[] pixels, int width, int height, int stride)
        {
            int transparentCount = 0;
            int semiOrOpaqueCount = 0;
            int total = width * height;

            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < width; x++)
                {
                    byte a = pixels[rowOffset + x * 4 + 3];
                    if (a < 16)
                        transparentCount++;
                    else if (a > 40)
                        semiOrOpaqueCount++;
                }
            }

            // A valid transparent seal image has at least 5% transparent background and at least 0.15% foreground
            return transparentCount > total * 0.05 && semiOrOpaqueCount > total * 0.0015;
        }

        private static byte[] FeatherBoundaryAlpha(byte[] alphaMap, int width, int height, byte maxAlpha)
        {
            byte[] temp = new byte[width * height];
            byte[] output = new byte[width * height];
            Array.Copy(alphaMap, temp, alphaMap.Length);
            Array.Copy(alphaMap, output, alphaMap.Length);

            // Horizontal 3-tap smoothing on non-interior pixels
            for (int y = 1; y < height - 1; y++)
            {
                int row = y * width;
                for (int x = 1; x < width - 1; x++)
                {
                    byte c = alphaMap[row + x];
                    if (c < maxAlpha - 8)
                    {
                        int sum = alphaMap[row + x - 1] + c * 2 + alphaMap[row + x + 1];
                        temp[row + x] = (byte)(sum / 4);
                    }
                }
            }

            // Vertical 3-tap smoothing on non-interior pixels
            for (int y = 1; y < height - 1; y++)
            {
                int row = y * width;
                for (int x = 1; x < width - 1; x++)
                {
                    byte c = temp[row + x];
                    if (c < maxAlpha - 8)
                    {
                        int sum = temp[(y - 1) * width + x] + c * 2 + temp[(y + 1) * width + x];
                        output[row + x] = (byte)Math.Min((int)maxAlpha, sum / 4);
                    }
                }
            }

            return output;
        }

        private static double SmoothStep(double edge0, double edge1, double x)
        {
            double t = Math.Max(0.0, Math.Min(1.0, (x - edge0) / (edge1 - edge0)));
            return t * t * (3.0 - 2.0 * t);
        }

        private static double HashNoise(int x, int y, int seed)
        {
            unchecked
            {
                int n = x * 374761393 + y * 668265263 + seed * 1442695041;
                n = (n ^ (n >> 13)) * 1274126177;
                n = n ^ (n >> 16);
                return (n & 0x7FFFFFFF) / (double)0x7FFFFFFF;
            }
        }

        private static double ValueNoise2D(double x, double y, int seed)
        {
            int ix = (int)Math.Floor(x);
            int iy = (int)Math.Floor(y);
            double fx = x - ix;
            double fy = y - iy;

            double u = fx * fx * (3.0 - 2.0 * fx);
            double v = fy * fy * (3.0 - 2.0 * fy);

            double n00 = HashNoise(ix, iy, seed);
            double n10 = HashNoise(ix + 1, iy, seed);
            double n01 = HashNoise(ix, iy + 1, seed);
            double n11 = HashNoise(ix + 1, iy + 1, seed);

            double nx0 = n00 + (n10 - n00) * u;
            double nx1 = n01 + (n11 - n01) * u;
            return nx0 + (nx1 - nx0) * v;
        }
    }
}
