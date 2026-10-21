using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using DesktopStamp.Core;
using DesktopStamp.Imaging;

namespace DesktopStamp.UI
{
    /// <summary>
    /// Minimal dark frosted context menu opened by right-clicking the parked upright stamp.
    /// Provides seal switching, image import fallback, 20mm/40mm physical size locks,
    /// free Ctrl+wheel scaling toggle, angle reset, and an accessible Exit command,
    /// with full keyboard navigation (Up/Down/Home/End/Enter/Space/Escape) and DPI scaling.
    /// </summary>
    public sealed class FrostedContextMenuForm : Form
    {
        private sealed class MenuEntry
        {
            public string Label { get; set; }
            public bool IsHeader { get; set; }
            public bool IsSeparator { get; set; }
            public bool IsChecked { get; set; }
            public bool IsDanger { get; set; }
            public Action OnClick { get; set; }
            public Rectangle Bounds { get; set; }
            public bool IsSelectable => !IsHeader && !IsSeparator && OnClick != null;
        }

        private readonly List<MenuEntry> _entries = new List<MenuEntry>();
        private int _hoveredIndex = -1;
        private float _dpiScale = 1.0f;

        public int HighlightedIndex => _hoveredIndex;
        public string HighlightedLabel => (_hoveredIndex >= 0 && _hoveredIndex < _entries.Count)
            ? _entries[_hoveredIndex].Label
            : null;

        public FrostedContextMenuForm(
            IReadOnlyList<SealAsset> seals,
            SealAsset activeSeal,
            StampStateMachine stateMachine,
            Action<SealAsset> onSelectSeal,
            Action onImportSealImage,
            Action<SizeLockMode> onSelectSizeLock,
            Action onResetRotation,
            Action onExitRequested,
            double monitorDpi = 96.0)
        {
            _dpiScale = (float)Math.Max(1.0, Math.Min(3.0, monitorDpi / 96.0));
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            KeyPreview = true;
            BackColor = Color.FromArgb(20, 21, 25);
            ForeColor = Color.FromArgb(236, 238, 244);
            Opacity = 0.96;

            BuildEntries(
                seals,
                activeSeal,
                stateMachine,
                onSelectSeal,
                onImportSealImage,
                onSelectSizeLock,
                onResetRotation,
                onExitRequested);

            LayoutEntries(_dpiScale);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys keyCode = keyData & Keys.KeyCode;
            if (keyCode == Keys.Up || keyCode == Keys.Down ||
                keyCode == Keys.Home || keyCode == Keys.End ||
                keyCode == Keys.Enter || keyCode == Keys.Space ||
                keyCode == Keys.Tab || keyCode == Keys.Escape)
            {
                return true;
            }

            return base.IsInputKey(keyData);
        }

        private void BuildEntries(
            IReadOnlyList<SealAsset> seals,
            SealAsset activeSeal,
            StampStateMachine stateMachine,
            Action<SealAsset> onSelectSeal,
            Action onImportSealImage,
            Action<SizeLockMode> onSelectSizeLock,
            Action onResetRotation,
            Action onExitRequested)
        {
            _entries.Clear();

            _entries.Add(new MenuEntry { Label = "印章切换 / ACTIVE SEAL", IsHeader = true });
            if (seals != null)
            {
                foreach (SealAsset seal in seals)
                {
                    SealAsset captured = seal;
                    _entries.Add(new MenuEntry
                    {
                        Label = captured.Name,
                        IsChecked = activeSeal != null && captured.Id == activeSeal.Id,
                        OnClick = () => onSelectSeal?.Invoke(captured)
                    });
                }
            }

            _entries.Add(new MenuEntry
            {
                Label = "导入印章图片... (PNG / 照片 / 扫描件)",
                OnClick = () => onImportSealImage?.Invoke()
            });

            _entries.Add(new MenuEntry { IsSeparator = true });
            _entries.Add(new MenuEntry { Label = "物理尺寸锁定 / PHYSICAL SIZE", IsHeader = true });

            _entries.Add(new MenuEntry
            {
                Label = "锁定 20mm (20mm Size Lock)",
                IsChecked = stateMachine.SizeLock == SizeLockMode.Locked20mm,
                OnClick = () => onSelectSizeLock?.Invoke(SizeLockMode.Locked20mm)
            });

            _entries.Add(new MenuEntry
            {
                Label = "锁定 40mm (40mm Size Lock)",
                IsChecked = stateMachine.SizeLock == SizeLockMode.Locked40mm,
                OnClick = () => onSelectSizeLock?.Invoke(SizeLockMode.Locked40mm)
            });

            _entries.Add(new MenuEntry
            {
                Label = $"自由缩放 (Ctrl+滚轮 · 当前 {stateMachine.PhysicalSizeMm:F0}mm)",
                IsChecked = stateMachine.SizeLock == SizeLockMode.Unlocked,
                OnClick = () => onSelectSizeLock?.Invoke(SizeLockMode.Unlocked)
            });

            _entries.Add(new MenuEntry { IsSeparator = true });
            _entries.Add(new MenuEntry
            {
                Label = $"重置印章角度 (当前 {stateMachine.BaseRotationDeg:+0.0;-0.0;0.0}°)",
                OnClick = () => onResetRotation?.Invoke()
            });

            _entries.Add(new MenuEntry { IsSeparator = true });
            _entries.Add(new MenuEntry
            {
                Label = "退出物理印章 (Exit Stamp)",
                IsDanger = true,
                OnClick = () => onExitRequested?.Invoke()
            });
        }

        private void LayoutEntries(float dpiScale)
        {
            _dpiScale = Math.Max(1.0f, Math.Min(3.0f, dpiScale));
            int width = (int)Math.Round(284 * _dpiScale);
            int y = (int)Math.Round(10 * _dpiScale);
            int padH = (int)Math.Round(6 * _dpiScale);
            int sepPadH = (int)Math.Round(10 * _dpiScale);
            int sepH = Math.Max(6, (int)Math.Round(9 * _dpiScale));
            int headerH = (int)Math.Round(22 * _dpiScale);
            int itemH = (int)Math.Round(30 * _dpiScale);

            for (int i = 0; i < _entries.Count; i++)
            {
                MenuEntry e = _entries[i];
                if (e.IsSeparator)
                {
                    e.Bounds = new Rectangle(sepPadH, y, width - sepPadH * 2, sepH);
                    y += sepH;
                }
                else if (e.IsHeader)
                {
                    e.Bounds = new Rectangle(sepPadH, y, width - sepPadH * 2, headerH);
                    y += headerH;
                }
                else
                {
                    e.Bounds = new Rectangle(padH, y, width - padH * 2, itemH);
                    y += itemH;
                }
            }

            y += (int)Math.Round(8 * _dpiScale);
            ClientSize = new Size(width, y);
        }

        public void ShowNearPoint(Point screenPoint)
        {
            double monitorDpi = NativeMethods.GetDpiForMonitorPoint(screenPoint);
            float nextScale = (float)Math.Max(1.0, Math.Min(3.0, monitorDpi / 96.0));
            if (Math.Abs(nextScale - _dpiScale) > 0.02f)
            {
                LayoutEntries(nextScale);
            }

            Screen scr = Screen.FromPoint(screenPoint);
            Rectangle work = scr.WorkingArea;

            int margin = (int)Math.Round(8 * _dpiScale);
            int x = screenPoint.X - Width - margin;
            int y = screenPoint.Y - Height + (int)Math.Round(24 * _dpiScale);

            if (x < work.Left + margin) x = screenPoint.X + (int)Math.Round(12 * _dpiScale);
            if (x + Width > work.Right - margin) x = work.Right - Width - margin;
            if (x < work.Left + margin) x = work.Left + margin;
            if (y < work.Top + margin) y = work.Top + margin;
            if (y + Height > work.Bottom - margin) y = work.Bottom - Height - margin;

            Location = new Point(x, y);
            Show();
            Activate();
        }

        public bool HandleKeyboardNavigation(Keys keyCode)
        {
            switch (keyCode)
            {
                case Keys.Escape:
                    Close();
                    return true;

                case Keys.Down:
                case Keys.Tab:
                    MoveHighlight(step: 1);
                    return true;

                case Keys.Up:
                    MoveHighlight(step: -1);
                    return true;

                case Keys.Home:
                    SetHighlightToExtreme(first: true);
                    return true;

                case Keys.End:
                    SetHighlightToExtreme(first: false);
                    return true;

                case Keys.Enter:
                case Keys.Space:
                    if (_hoveredIndex >= 0 && _hoveredIndex < _entries.Count && _entries[_hoveredIndex].IsSelectable)
                    {
                        Action callback = _entries[_hoveredIndex].OnClick;
                        Close();
                        callback?.Invoke();
                        return true;
                    }
                    break;
            }

            return false;
        }

        private void MoveHighlight(int step)
        {
            int count = _entries.Count;
            if (count == 0)
                return;

            int start = _hoveredIndex;
            if (start < 0 || start >= count)
            {
                start = step > 0 ? -1 : count;
            }

            for (int n = 1; n <= count; n++)
            {
                int idx = (start + step * n) % count;
                if (idx < 0) idx += count;
                if (_entries[idx].IsSelectable)
                {
                    _hoveredIndex = idx;
                    Invalidate();
                    return;
                }
            }
        }

        private void SetHighlightToExtreme(bool first)
        {
            if (first)
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i].IsSelectable)
                    {
                        _hoveredIndex = i;
                        Invalidate();
                        return;
                    }
                }
            }
            else
            {
                for (int i = _entries.Count - 1; i >= 0; i--)
                {
                    if (_entries[i].IsSelectable)
                    {
                        _hoveredIndex = i;
                        Invalidate();
                        return;
                    }
                }
            }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (!IsDisposed)
            {
                Close();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (HandleKeyboardNavigation(e.KeyCode))
            {
                e.Handled = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int nextHover = -1;
            for (int i = 0; i < _entries.Count; i++)
            {
                MenuEntry item = _entries[i];
                if (item.IsSelectable && item.Bounds.Contains(e.Location))
                {
                    nextHover = i;
                    break;
                }
            }

            if (nextHover != _hoveredIndex)
            {
                _hoveredIndex = nextHover;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoveredIndex != -1)
            {
                _hoveredIndex = -1;
                Invalidate();
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left)
                return;

            for (int i = 0; i < _entries.Count; i++)
            {
                MenuEntry item = _entries[i];
                if (item.IsSelectable && item.Bounds.Contains(e.Location))
                {
                    Action callback = item.OnClick;
                    Close();
                    callback?.Invoke();
                    return;
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var fullRect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using (var bgBrush = new LinearGradientBrush(
                fullRect,
                Color.FromArgb(255, 25, 27, 32),
                Color.FromArgb(255, 17, 18, 22),
                LinearGradientMode.Vertical))
            using (var borderPen = new Pen(Color.FromArgb(190, 72, 76, 88), 1f))
            using (var crimsonAccentPen = new Pen(Color.FromArgb(190, 195, 28, 38), 1.5f * _dpiScale))
            {
                g.FillRectangle(bgBrush, ClientRectangle);
                g.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
                int accentMargin = (int)Math.Round(16 * _dpiScale);
                g.DrawLine(crimsonAccentPen, accentMargin, Height - 1, Width - accentMargin, Height - 1);
            }

            using (var headerFont = new Font("Microsoft YaHei UI", 10.5f * _dpiScale, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var itemFont = new Font("Microsoft YaHei UI", 12.0f * _dpiScale, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var headerBrush = new SolidBrush(Color.FromArgb(135, 142, 155)))
            using (var itemBrush = new SolidBrush(Color.FromArgb(236, 238, 244)))
            using (var dangerBrush = new SolidBrush(Color.FromArgb(245, 92, 98)))
            using (var sepPen = new Pen(Color.FromArgb(46, 49, 58), 1f))
            using (var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    MenuEntry item = _entries[i];
                    if (item.IsSeparator)
                    {
                        int midY = item.Bounds.Top + item.Bounds.Height / 2;
                        g.DrawLine(sepPen, item.Bounds.Left, midY, item.Bounds.Right, midY);
                        continue;
                    }

                    if (item.IsHeader)
                    {
                        float hPad = 8f * _dpiScale;
                        var textRect = new RectangleF(item.Bounds.Left + hPad, item.Bounds.Top, item.Bounds.Width - hPad * 1.5f, item.Bounds.Height);
                        g.DrawString(item.Label, headerFont, headerBrush, textRect, sf);
                        continue;
                    }

                    if (i == _hoveredIndex)
                    {
                        Color hoverColor = item.IsDanger
                            ? Color.FromArgb(75, 195, 28, 36)
                            : Color.FromArgb(255, 38, 41, 49);

                        using (GraphicsPath path = StampRenderer.CreateRoundedRectPath(item.Bounds, 5f * _dpiScale))
                        using (var hoverBrush = new SolidBrush(hoverColor))
                        {
                            g.FillPath(hoverBrush, path);
                        }
                    }

                    if (item.IsChecked)
                    {
                        float dotSize = 7f * _dpiScale;
                        float dotX = item.Bounds.Left + 10f * _dpiScale;
                        float dotY = item.Bounds.Top + item.Bounds.Height * 0.5f - dotSize * 0.5f;
                        using (var dotBrush = new SolidBrush(Color.FromArgb(228, 36, 44)))
                        {
                            g.FillEllipse(dotBrush, dotX, dotY, dotSize, dotSize);
                        }
                    }

                    float textLeftOffset = 24f * _dpiScale;
                    var itemTextRect = new RectangleF(
                        item.Bounds.Left + textLeftOffset,
                        item.Bounds.Top,
                        item.Bounds.Width - textLeftOffset - 4f * _dpiScale,
                        item.Bounds.Height);
                    g.DrawString(
                        item.Label,
                        itemFont,
                        (item.IsDanger && i != _hoveredIndex) ? dangerBrush : itemBrush,
                        itemTextRect,
                        sf);
                }
            }
        }
    }
}
