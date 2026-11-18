using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SingleAppRemote.Host;

internal static class AppIconGenerator
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon CreateAppIcon(int size = 64)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            // 背景の角丸グラデーション（ディープブルー〜シアン）
            using var bgPath = CreateRoundedRect(2, 2, size - 4, size - 4, size * 0.22f);
            using var bgBrush = new LinearGradientBrush(
                new Rectangle(0, 0, size, size),
                Color.FromArgb(37, 99, 235),
                Color.FromArgb(14, 165, 233),
                LinearGradientMode.ForwardDiagonal);
            g.FillPath(bgBrush, bgPath);

            // 内側のアプリケーションウィンドウ枠
            float winX = size * 0.16f;
            float winY = size * 0.20f;
            float winW = size * 0.68f;
            float winH = size * 0.52f;

            using var winPath = CreateRoundedRect(winX, winY, winW, winH, size * 0.06f);
            using var winFill = new SolidBrush(Color.FromArgb(245, 15, 23, 42));
            g.FillPath(winFill, winPath);

            using var winPen = new Pen(Color.White, Math.Max(2f, size * 0.045f));
            g.DrawPath(winPen, winPath);

            // ウィンドウのタイトルバーライン
            float barY = winY + winH * 0.28f;
            g.DrawLine(winPen, winX, barY, winX + winW, barY);

            // ウィンドウ中央のキャスト/ターゲットドット（エメラルドグリーン）
            float dotR = size * 0.09f;
            float centerX = size * 0.50f;
            float centerY = barY + (winH * 0.72f) * 0.5f;
            using var dotBrush = new SolidBrush(Color.FromArgb(52, 211, 153));
            g.FillEllipse(dotBrush, centerX - dotR, centerY - dotR, dotR * 2, dotR * 2);

            // 下部スタンド
            using var standPen = new Pen(Color.White, Math.Max(2f, size * 0.055f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            float standTop = winY + winH + size * 0.02f;
            float standBottom = size * 0.82f;
            g.DrawLine(standPen, centerX, standTop, centerX, standBottom);
            g.DrawLine(standPen, size * 0.34f, standBottom, size * 0.66f, standBottom);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var tempIcon = Icon.FromHandle(hIcon);
            return (Icon)tempIcon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath CreateRoundedRect(float x, float y, float width, float height, float radius)
    {
        float d = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
